using System.Linq;
using System.Text;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using static KOZ39.ElbowFixer.ElbowFixerException;
using static KOZ39.ElbowFixer.ElbowFixerLocalization;

namespace KOZ39.ElbowFixer
{
    internal static class ElbowFixerBuilder
    {
        // Runs on the NDMF build copy only.
        internal static string Build(
            GameObject avatarRoot,
            ObjectReference fixer,
            SkinnedMeshRenderer[] renderers
        )
        {
            // Build copies can be far from the origin. Keep world-space rig and bindpose calculations precise.
            var position = avatarRoot.transform.position;

            try
            {
                avatarRoot.transform.position = Vector3.zero;

                return BuildAtOrigin(avatarRoot, fixer, renderers);
            }
            finally
            {
                avatarRoot.transform.position = position;
            }
        }

        private static string BuildAtOrigin(
            GameObject avatarRoot,
            ObjectReference fixer,
            SkinnedMeshRenderer[] renderers
        )
        {
            var arms = Arm.Resolve(avatarRoot);
            RejectMeshAnimations(avatarRoot, renderers);
            var plans = renderers
                .Select(renderer => MeshWeightPlan.Create(renderer, arms))
                .ToArray();

            foreach (var plan in plans)
            {
                Require(plan.RedistributedCount > 0, fixer, "NoForearmWeights", plan.Renderer);
            }

            var supports = arms.Select(TwistRig.Build).ToArray();

            // Every support and mirror bone carries one VRC constraint, and every arm adds one Up reference object.
            var boneCount =
                supports.Sum(bones => bones.Length) + plans.Sum(plan => plan.MirrorCount);
            var report = new StringBuilder(Text("messages.BuildSummary", boneCount, arms.Length));

            foreach (var plan in plans)
            {
                var drift = plan.Apply(supports, avatarRoot.transform);
                report
                    .AppendLine()
                    .Append(
                        Text(
                            "messages.PlanSummary",
                            plan.Renderer.name,
                            plan.RedistributedCount,
                            plan.NearestCount
                        )
                    );
                report
                    .AppendLine()
                    .Append(Text("messages.RestPoseDrift", plan.Renderer.name, drift * 1000));
            }

            return report.ToString();
        }

        // A renderer outside the build copy would be the original scene object.
        internal static SkinnedMeshRenderer[] SelectTargetMeshes(
            GameObject avatarRoot,
            ElbowFixer fixer
        )
        {
            var renderers = fixer
                .targetMeshes.Where(renderer => renderer != null)
                .Distinct()
                .ToArray();

            Require(renderers.Length > 0, fixer, "NoMeshes");

            foreach (var renderer in renderers)
            {
                Require(
                    renderer.transform.IsChildOf(avatarRoot.transform),
                    fixer,
                    "MeshOutsideAvatar",
                    renderer
                );
                Require(
                    !ElbowFixerUtility.IsEditorOnly(renderer.transform, avatarRoot.transform),
                    fixer,
                    "MeshEditorOnly",
                    renderer
                );
                Require(
                    !renderer.TryGetComponent<Cloth>(out _),
                    fixer,
                    "ClothNotSupported",
                    renderer
                );
            }

            return renderers;
        }

        // An animation that swaps the mesh would replace the fixed mesh at runtime.
        private static void RejectMeshAnimations(
            GameObject avatarRoot,
            SkinnedMeshRenderer[] renderers
        )
        {
            var root = avatarRoot.transform;
            var paths = renderers
                .Select(renderer =>
                    AnimationUtility.CalculateTransformPath(renderer.transform, root)
                )
                .ToHashSet();
            var descriptor = avatarRoot.GetComponent<VRCAvatarDescriptor>();
            var layers = descriptor.customizeAnimationLayers
                ? descriptor
                    .baseAnimationLayers.Concat(descriptor.specialAnimationLayers)
                    .Where(layer => !layer.isDefault)
                : Enumerable.Empty<VRCAvatarDescriptor.CustomAnimLayer>();
            var clips = layers
                .Select(layer => layer.animatorController)
                .Where(controller => controller != null)
                .SelectMany(controller => controller.animationClips)
                .Distinct();

            foreach (var clip in clips)
            {
                var replacesMesh = AnimationUtility
                    .GetObjectReferenceCurveBindings(clip)
                    .Any(binding =>
                        binding.type == typeof(SkinnedMeshRenderer)
                        && binding.propertyName == "m_Mesh"
                        && paths.Contains(binding.path)
                    );

                Require(!replacesMesh, "AnimatedMesh", clip);
            }
        }
    }
}
