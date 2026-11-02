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
            bool subdivideForearm,
            Action<ElbowFixerException> skip
        )
        {
            // Build copies can be far from the origin, where world-space math loses precision.
            var position = avatarRoot.transform.position;

            try
            {
                avatarRoot.transform.position = Vector3.zero;

                return BuildAtOrigin(avatarRoot, renderers, subdivideForearm, skip);
            }
            finally
            {
                avatarRoot.transform.position = position;
            }
        }

        private static string BuildAtOrigin(
            GameObject avatarRoot,
            SkinnedMeshRenderer[] renderers,
            bool subdivideForearm,
            Action<ElbowFixerException> skip
        )
        {
            var arms = Arm.Resolve(avatarRoot);
            var meshSwaps = FindMeshSwaps(avatarRoot, renderers);

            // Sleeve cuffs often follow the fingers partly, so only the mesh with the most vertices
            // fully on the fingers gets the skin twist.
            var skin = renderers
                .Select(renderer =>
                    (
                        Renderer: renderer,
                        Count: MeshWeightPlan.RigidFingerVertexCount(renderer, arms)
                    )
                )
                .OrderByDescending(candidate => candidate.Count)
                .FirstOrDefault(candidate => candidate.Count > 0)
                .Renderer;
            var hasSkin = skin != null;
            var hasClothing = renderers.Any(renderer => renderer != skin);
            var plans = renderers
                .Select(renderer =>
                    TryPlan(
                        renderer,
                        renderer == skin,
                        subdivideForearm,
                        arms,
                        hasSkin,
                        hasClothing,
                        meshSwaps,
                        skip
                    )
                )
                .Where(plan => plan != null)
                .ToArray();

            var survivingSkin = plans.Any(plan => plan.IsSkin);
            var survivingClothing = plans.Any(plan => !plan.IsSkin);

            if (survivingSkin != hasSkin || survivingClothing != hasClothing)
            {
                hasSkin = survivingSkin;
                hasClothing = survivingClothing;
                plans = plans
                    .Select(plan =>
                        TryPlan(
                            plan.Renderer,
                            plan.IsSkin,
                            subdivideForearm,
                            arms,
                            hasSkin,
                            hasClothing,
                            meshSwaps,
                            skip
                        )
                    )
                    .Where(plan => plan != null)
                    .ToArray();
            }

            if (plans.Length == 0)
            {
                return null;
            }

            var rigs = arms.Select(arm => TwistRig.Build(arm, hasSkin, hasClothing)).ToArray();
            var supports = rigs.Select(rig => rig.Supports).ToArray();
            var applied = plans
                .Select(plan => TryApply(plan, supports, skip))
                .Where(result => result != null)
                .Select(result => result.Value)
                .ToArray();

            if (applied.Length == 0)
            {
                foreach (var root in rigs.SelectMany(rig => rig.Roots))
                {
                    UnityEngine.Object.DestroyImmediate(root.gameObject);
                }

                return null;
            }

            var boneCount =
                supports.Sum(bones => bones.Length)
                + applied.Sum(result => result.Plan.MirrorCount);
            // Each arm's Roll is a reference object with an aim constraint but no skin weights.
            var report = new StringBuilder(
                Text(
                    "messages.BuildSummary",
                    boneCount,
                    hasSkin ? boneCount + arms.Length : boneCount,
                    arms.Length * TwistRig.ReferenceCount(hasSkin)
                )
            );

            foreach (var (plan, drift) in applied)
            {
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
            bool isSkin,
            bool subdivideForearm,
            Arm[] arms,
            bool hasSkin,
            bool hasClothing,
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
                var supportCounts = Enumerable
                    .Repeat(TwistRig.SupportCount(hasSkin, hasClothing), arms.Length)
                    .ToArray();
                var plan = MeshWeightPlan.Create(
                    renderer,
                    arms,
                    supportCounts,
                    isSkin,
                    subdivideForearm && isSkin
                );
                Require(plan.RedistributedCount > 0, "NoForearmWeights", renderer);

                return plan;
            }
            catch (ElbowFixerException e)
            {
                skip(e);

                return null;
            }
        }

        private static (MeshWeightPlan Plan, float Drift)? TryApply(
            MeshWeightPlan plan,
            Transform[][] supports,
            Action<ElbowFixerException> skip
        )
        {
            try
            {
                return (plan, plan.Apply(supports));
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
