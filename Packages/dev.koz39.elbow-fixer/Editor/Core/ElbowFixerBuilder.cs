using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using static KOZ39.ElbowFixer.ElbowFixerException;
using static KOZ39.ElbowFixer.ElbowFixerLocalization;

namespace KOZ39.ElbowFixer
{
    internal static class ElbowFixerBuilder
    {
        internal static string Build(
            GameObject avatarRoot,
            SkinnedMeshRenderer[] renderers,
            Action<ElbowFixerException> skip
        )
        {
            // Build copies can be far from the origin, where world-space math loses precision.
            var position = avatarRoot.transform.position;

            try
            {
                avatarRoot.transform.position = Vector3.zero;

                return BuildAtOrigin(avatarRoot, renderers, skip);
            }
            finally
            {
                avatarRoot.transform.position = position;
            }
        }

        private static string BuildAtOrigin(
            GameObject avatarRoot,
            SkinnedMeshRenderer[] renderers,
            Action<ElbowFixerException> skip
        )
        {
            var arms = Arm.Resolve(avatarRoot);
            var meshSwaps = FindMeshSwaps(avatarRoot, renderers);
            var plans = renderers
                .Select(renderer => TryPlan(renderer, arms, meshSwaps, skip))
                .Where(plan => plan != null)
                .ToArray();

            if (plans.Length == 0)
            {
                return null;
            }

            var supports = arms.Select(TwistRig.Build).ToArray();

            var boneCount =
                supports.Sum(bones => bones.Length) + plans.Sum(plan => plan.MirrorCount);
            var report = new StringBuilder(
                Text("messages.BuildSummary", boneCount, arms.Length * TwistRig.ReferenceCount)
            );

            foreach (var plan in plans)
            {
                var drift = plan.Apply(supports);
                report
                    .AppendLine()
                    .Append(
                        Text(
                            "messages.PlanSummary",
                            plan.Renderer.name,
                            plan.RedistributedCount,
                            plan.NearestCount
                        )
                    )
                    .AppendLine()
                    .Append(Text("messages.RestPoseDrift", plan.Renderer.name, drift * 1000));
            }

            return report.ToString();
        }

        private static MeshWeightPlan TryPlan(
            SkinnedMeshRenderer renderer,
            Arm[] arms,
            Dictionary<SkinnedMeshRenderer, AnimationClip> meshSwaps,
            Action<ElbowFixerException> skip
        )
        {
            try
            {
                Require(
                    !meshSwaps.TryGetValue(renderer, out var clip),
                    "AnimatedMesh",
                    renderer,
                    clip
                );
                var plan = MeshWeightPlan.Create(renderer, arms);
                Require(plan.RedistributedCount > 0, "NoForearmWeights", renderer);

                return plan;
            }
            catch (ElbowFixerException e)
            {
                skip(e);

                return null;
            }
        }

        private static Dictionary<SkinnedMeshRenderer, AnimationClip> FindMeshSwaps(
            GameObject avatarRoot,
            SkinnedMeshRenderer[] renderers
        )
        {
            var root = avatarRoot.transform;
            var renderersByPath = renderers.ToLookup(renderer =>
                AnimationUtility.CalculateTransformPath(renderer.transform, root)
            );
            var descriptor = avatarRoot.GetComponent<VRCAvatarDescriptor>();
            var layers = descriptor.customizeAnimationLayers
                ? descriptor
                    .baseAnimationLayers.Concat(descriptor.specialAnimationLayers)
                    .Where(layer => !layer.isDefault)
                : Enumerable.Empty<VRCAvatarDescriptor.CustomAnimLayer>();

            return layers
                .Select(layer => layer.animatorController)
                .Where(controller => controller != null)
                .SelectMany(controller => controller.animationClips)
                .Distinct()
                .SelectMany(clip =>
                    AnimationUtility
                        .GetObjectReferenceCurveBindings(clip)
                        .Where(binding =>
                            binding.type == typeof(SkinnedMeshRenderer)
                            && binding.propertyName == "m_Mesh"
                        )
                        .SelectMany(binding => renderersByPath[binding.path])
                        .Select(renderer => (Renderer: renderer, Clip: clip))
                )
                .GroupBy(swap => swap.Renderer)
                .ToDictionary(group => group.Key, group => group.First().Clip);
        }
    }
}
