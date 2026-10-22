using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace KOZ39.ElbowFixer
{
    internal static class TargetMeshes
    {
        private const int MinForearmVertices = 20;
        private const float MinForearmCoverage = 0.3f;
        private const float RigidWeight = 0.999f;

        // Shared by the build and the inspector.
        internal static (
            SkinnedMeshRenderer[] Meshes,
            (SkinnedMeshRenderer Mesh, string Problem)[] Skipped
        ) Select(GameObject avatarRoot, ElbowFixer fixer)
        {
            var selected = fixer
                .targetMeshes.Where(renderer => renderer != null)
                .Distinct()
                .Select(renderer =>
                    (Mesh: renderer, Problem: FindSelectionProblem(renderer, avatarRoot.transform))
                )
                .ToArray();

            return (
                selected.Where(item => item.Problem == null).Select(item => item.Mesh).ToArray(),
                selected.Where(item => item.Problem != null).ToArray()
            );
        }

        // A renderer outside the build copy would be the original scene object.
        internal static string FindSelectionProblem(
            SkinnedMeshRenderer renderer,
            Transform avatarRoot
        )
        {
            if (!ElbowFixerUtility.BelongsToAvatar(renderer.transform, avatarRoot))
            {
                return "MeshOutsideAvatar";
            }

            if (ElbowFixerUtility.IsEditorOnly(renderer.transform, avatarRoot))
            {
                return "MeshEditorOnly";
            }

            return renderer.TryGetComponent<Cloth>(out _) ? "ClothNotSupported" : null;
        }

        // Clothes with their own armature are matched by bone name, since Modular Avatar merges them at build time.
        // Meshes without Read/Write are included too; the VRChat SDK and the build report them.
        // Weights on a twist bone or a bone with a constraint count as forearm weights, since the build moves them
        // onto the LowerArm. Meshes the build would skip, for a bone under such a bone, are returned apart.
        internal static (
            SkinnedMeshRenderer[] Targets,
            (SkinnedMeshRenderer Mesh, Transform Blocker)[] Skipped
        ) Find(GameObject avatarRoot)
        {
            var animator = avatarRoot.GetComponent<Animator>();

            if (animator == null || !animator.isHuman)
            {
                return (
                    Array.Empty<SkinnedMeshRenderer>(),
                    Array.Empty<(SkinnedMeshRenderer, Transform)>()
                );
            }

            var arms = ArmSide
                .All.Select(side =>
                    (
                        Lower: animator.GetBoneTransform(side.Lower),
                        Hand: animator.GetBoneTransform(side.Hand)
                    )
                )
                .Where(arm => arm.Lower != null && arm.Hand != null)
                .ToArray();
            var existingTwist = new ExistingTwist(avatarRoot);
            var targets = new List<SkinnedMeshRenderer>();
            var skipped = new List<(SkinnedMeshRenderer Mesh, Transform Blocker)>();

            foreach (var renderer in avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var mesh = renderer.sharedMesh;

                // Outside play mode the editor can read a mesh even without Read/Write.
                if (
                    !ElbowFixerUtility.IsIncluded(renderer.transform, avatarRoot.transform)
                    || mesh == null
                    || (!mesh.isReadable && EditorApplication.isPlaying)
                )
                {
                    continue;
                }

                // Each read of these properties copies the whole array.
                var bones = renderer.bones;
                var bindposes = mesh.bindposes;
                var vertices = mesh.vertices;
                var weights = mesh.boneWeights;
                var usedBones = ForearmWeights.UsedBones(weights, bones);
                var follows = false;
                Transform blocker = null;

                foreach (var arm in arms)
                {
                    var forearm = FindForearm(bones, bindposes, usedBones, arm, existingTwist);

                    if (forearm == null)
                    {
                        continue;
                    }

                    var (sources, bindpose, forearmBlocker) = forearm.Value;
                    follows |= FollowsForearm(sources, bindpose, arm.Hand, vertices, weights);
                    blocker ??= forearmBlocker;
                }

                if (!follows)
                {
                    continue;
                }

                if (blocker == null)
                {
                    targets.Add(renderer);
                }
                else
                {
                    skipped.Add((renderer, blocker));
                }
            }

            return (targets.ToArray(), skipped.ToArray());
        }

        // The same check as the build: the bones whose weights follow this forearm once weights on twist bones and
        // bones with a constraint move onto the LowerArm, the LowerArm's bindpose, and a twist bone or a bone with a
        // constraint that has a used bone under it, which makes the build skip the mesh. A mesh may list only such a
        // bone, so its LowerArm is found above the bones it uses.
        private static (int[] Sources, Matrix4x4 Bindpose, Transform Blocker)? FindForearm(
            Transform[] bones,
            Matrix4x4[] bindposes,
            Transform[] usedBones,
            (Transform Lower, Transform Hand) arm,
            ExistingTwist existingTwist
        )
        {
            var lower = usedBones
                .Select(bone =>
                    bone.GetComponentsInParent<Transform>(true)
                        .FirstOrDefault(t => IsArmBone(t, arm.Lower))
                )
                .FirstOrDefault(t => t != null);

            if (lower == null || lower.parent == null)
            {
                return null;
            }

            var hand =
                lower == arm.Lower
                    ? arm.Hand
                    : lower.Cast<Transform>().FirstOrDefault(child => IsArmBone(child, arm.Hand));
            var reboundBones = new List<Transform>();
            Transform blocker = null;

            foreach (var bone in usedBones)
            {
                var rebind = FindRebind(bone, lower.parent, lower, hand, arm, existingTwist);
                blocker ??= rebind.Blocker;

                if (rebind.ArmBone == lower)
                {
                    reboundBones.Add(bone);
                }
            }

            var sources = Enumerable
                .Range(0, Math.Min(bones.Length, bindposes.Length))
                .Where(i => bones[i] == lower || reboundBones.Contains(bones[i]))
                .ToArray();

            if (sources.Length == 0)
            {
                return null;
            }

            // Derived from a rebound bone's bindpose when the mesh does not list the LowerArm, as the build does.
            var lowerIndex = Array.IndexOf(bones, lower);
            var bindpose =
                lowerIndex >= 0 && lowerIndex < bindposes.Length
                    ? bindposes[lowerIndex]
                    : MeshWeightPlan.BindposeFrom(lower, bones[sources[0]], bindposes[sources[0]]);

            return (sources, bindpose, blocker);
        }

        // Clothes are not merged yet, so a bone is checked in its own armature and then as the avatar bone Modular
        // Avatar will merge it into. A bone with no match ends up under the avatar bone its nearest matched parent
        // merges into, which counts as a bone under that twist bone. The arm bone returned is the clothing's own.
        private static (Transform ArmBone, Transform Blocker) FindRebind(
            Transform bone,
            Transform upper,
            Transform lower,
            Transform hand,
            (Transform Lower, Transform Hand) arm,
            ExistingTwist existingTwist
        )
        {
            var own = existingTwist.FindRebind(bone, upper, lower, hand);
            var avatarUpper = arm.Lower.parent;

            if (
                own.ArmBone != null
                || own.Blocker != null
                || upper == avatarUpper
                || !bone.IsChildOf(upper)
            )
            {
                return own;
            }

            foreach (
                var t in bone.GetComponentsInParent<Transform>(true).TakeWhile(t => t != upper)
            )
            {
                var merged = avatarUpper.Find(AnimationUtility.CalculateTransformPath(t, upper));

                if (merged == null)
                {
                    continue;
                }

                if (t != bone)
                {
                    return (null, existingTwist.Find(merged, avatarUpper, arm.Lower, arm.Hand));
                }

                var (armBone, blocker) = existingTwist.FindRebind(
                    merged,
                    avatarUpper,
                    arm.Lower,
                    arm.Hand
                );

                return (
                    armBone == null ? null
                    : armBone == arm.Lower ? lower
                    : upper,
                    blocker
                );
            }

            return (null, null);
        }

        private static bool IsArmBone(Transform bone, Transform avatarBone) =>
            bone != null && (bone == avatarBone || bone.name == avatarBone.name);

        private static bool FollowsForearm(
            int[] sources,
            Matrix4x4 bindpose,
            Transform hand,
            Vector3[] vertices,
            BoneWeight[] weights
        )
        {
            var forearm = new ForearmAxis(bindpose, hand.localPosition);

            // Negated so that NaN also fails.
            if (!(forearm.Length > 0))
            {
                return false;
            }

            var count = 0;
            var blended = false;
            var min = 1f;
            var max = 0f;

            for (var v = 0; v < vertices.Length && v < weights.Length; v++)
            {
                var weight = ForearmWeights
                    .Slots(weights[v])
                    .Where(slot => slot.Weight > 0 && sources.Contains(slot.Bone))
                    .Sum(slot => slot.Weight);

                if (weight <= 0)
                {
                    continue;
                }

                var t = forearm.Position(vertices[v]);
                count++;
                blended |= weight < RigidWeight;
                min = Mathf.Min(min, t);
                max = Mathf.Max(max, t);
            }

            // Below these, a mesh is likely a stray weight, a short accessory or a rigid one bound to a single bone.
            return count >= MinForearmVertices && max - min >= MinForearmCoverage && blended;
        }
    }
}
