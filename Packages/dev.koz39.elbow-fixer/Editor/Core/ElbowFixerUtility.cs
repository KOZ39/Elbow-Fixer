using System;
using System.Linq;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace KOZ39.ElbowFixer
{
    internal static class ElbowFixerUtility
    {
        private const int MinForearmVertices = 20;
        private const float MinForearmCoverage = 0.3f;
        private const float RigidWeight = 0.999f;

        // Prefix from Modular Avatar's NaNimationFilter; the rest of the name is the shape key.
        private const string HiddenBonePrefix = "NaNimatedBone for ";

        // NDMF builds the outermost avatar, so the inspector must judge fixers against the same root.
        internal static GameObject FindAvatarRoot(Component component)
        {
            var descriptors = component.GetComponentsInParent<VRCAvatarDescriptor>(true);

            return descriptors.Length > 0 ? descriptors[^1].gameObject : null;
        }

        // Clothes with their own armature are matched by bone name, since Modular Avatar merges them at build time.
        internal static SkinnedMeshRenderer[] FindTargetMeshes(GameObject avatarRoot)
        {
            var animator = avatarRoot.GetComponent<Animator>();

            if (animator == null || !animator.isHuman)
            {
                return Array.Empty<SkinnedMeshRenderer>();
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

            return avatarRoot
                .GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Where(renderer => IsIncluded(renderer.transform, avatarRoot.transform))
                .Where(renderer => renderer.sharedMesh != null && renderer.sharedMesh.isReadable)
                .Where(renderer => arms.Any(arm => FollowsForearm(renderer, arm.Lower, arm.Hand)))
                .ToArray();
        }

        private static bool FollowsForearm(
            SkinnedMeshRenderer renderer,
            Transform lower,
            Transform hand
        )
        {
            var mesh = renderer.sharedMesh;
            var bindposes = mesh.bindposes;
            var index = Array.FindIndex(
                renderer.bones,
                bone => bone != null && (bone == lower || bone.name == lower.name)
            );

            if (index < 0 || index >= bindposes.Length)
            {
                return false;
            }

            var forearm = new ForearmAxis(bindposes[index], hand.localPosition);

            // Negated so that NaN also fails.
            if (!(forearm.Length > 0))
            {
                return false;
            }

            var vertices = mesh.vertices;
            var weights = mesh.boneWeights;
            var count = 0;
            var blended = false;
            var min = 1f;
            var max = 0f;

            for (var v = 0; v < vertices.Length && v < weights.Length; v++)
            {
                var weight = ForearmWeights.SourceWeight(weights[v], index);

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

        internal static (ElbowFixer Primary, ElbowFixer[] All) FindFixers(GameObject avatarRoot)
        {
            var fixers = avatarRoot
                .GetComponentsInChildren<ElbowFixer>(true)
                .Where(fixer => IsIncluded(fixer.transform, avatarRoot.transform))
                .OrderBy(fixer => fixer.GetComponentsInParent<Transform>(true).Length)
                .ToArray();

            return (fixers.FirstOrDefault(), fixers);
        }

        // A bone Modular Avatar scales to NaN to hide part of a mesh.
        internal static bool IsHiddenBone(Transform bone) =>
            bone.name.StartsWith(HiddenBonePrefix, StringComparison.Ordinal);

        // Objects under an EditorOnly object never reach the build.
        internal static bool IsIncluded(Transform target, Transform avatarRoot) =>
            BelongsToAvatar(target, avatarRoot) && !IsEditorOnly(target, avatarRoot);

        // Objects under a nested avatar belong to that avatar.
        internal static bool BelongsToAvatar(Transform target, Transform avatarRoot)
        {
            var descriptor = target.GetComponentInParent<VRCAvatarDescriptor>(true);

            return descriptor != null && descriptor.transform == avatarRoot;
        }

        internal static bool IsEditorOnly(Transform target, Transform avatarRoot) =>
            target
                .GetComponentsInParent<Transform>(true)
                .TakeWhile(t => t != avatarRoot.parent)
                .Any(t => t.CompareTag("EditorOnly"));
    }
}
