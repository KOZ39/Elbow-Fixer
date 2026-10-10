using System;
using System.Linq;
using nadena.dev.ndmf.runtime;
using UnityEngine;

namespace KOZ39.ElbowFixer
{
    internal static class ElbowFixerUtility
    {
        private const int MinForearmVertices = 20;
        private const float MinForearmCoverage = 0.3f;
        private const float RigidWeight = 0.999f;

        // The avatar root NDMF builds, which is the outermost one.
        internal static GameObject FindAvatarRoot(Component component) =>
            RuntimeUtil.FindAvatarInParents(component.transform)?.gameObject;

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
                .Where(renderer => !IsEditorOnly(renderer.transform, avatarRoot.transform))
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
                var weight = ForearmWeights.LowerWeight(weights[v], index);

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

        // Fixers under an EditorOnly object never reach the build.
        internal static (ElbowFixer Primary, ElbowFixer[] All) FindFixers(GameObject avatarRoot)
        {
            var fixers = avatarRoot
                .GetComponentsInChildren<ElbowFixer>(true)
                .Where(fixer => !IsEditorOnly(fixer.transform, avatarRoot.transform))
                .OrderBy(fixer => Depth(fixer.transform))
                .ToArray();

            return (fixers.FirstOrDefault(), fixers);
        }

        internal static bool IsEditorOnly(Transform target, Transform avatarRoot)
        {
            for (var t = target; t != null && t != avatarRoot.parent; t = t.parent)
            {
                if (t.CompareTag("EditorOnly"))
                {
                    return true;
                }
            }

            return false;
        }

        private static int Depth(Transform target)
        {
            var depth = 0;

            for (var t = target; t.parent != null; t = t.parent)
            {
                depth++;
            }

            return depth;
        }
    }
}
