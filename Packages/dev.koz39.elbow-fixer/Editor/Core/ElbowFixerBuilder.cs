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
        private const float MinSkinShare = 0.5f;

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

            var skins = FindSkins(renderers, arms);
            var plans = renderers
                .Select(renderer =>
                    TryPlan(
                        renderer,
                        skins.Contains(renderer),
                        subdivideForearm,
                        arms,
                        meshSwaps,
                        skip
                    )
                )
                .Where(plan => plan != null)
                .ToArray();

            if (plans.Length == 0)
            {
                return null;
            }

            var rigs = arms.Select(TwistRig.Build).ToArray();
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
                    boneCount + arms.Length,
                    arms.Length * TwistRig.ReferenceCount
                )
            );

            foreach (var (plan, drift) in applied)
            {
                report
                    .AppendLine()
                    .Append(
                        Text(
                            plan.IsSkin ? "messages.SkinPlanSummary" : "messages.PlanSummary",
                            plan.Renderer.name,
                            plan.RedistributedCount,
                            plan.NearestCount
                        )
                    );

                if (plan.AddedVertexCount > 0)
                {
                    report
                        .AppendLine()
                        .Append(
                            Text(
                                "messages.SubdivisionSummary",
                                plan.Renderer.name,
                                plan.AddedVertexCount
                            )
                        );
                }

                report
                    .AppendLine()
                    .Append(Text("messages.RestPoseDrift", plan.Renderer.name, drift * 1000));
            }

            return report.ToString();
        }

        private static HashSet<SkinnedMeshRenderer> FindSkins(
            SkinnedMeshRenderer[] renderers,
            Arm[] arms
        )
        {
            // Cuffs follow the fingers only partly, which the share filters out.
            // The Hand bone counts only when no mesh uses finger bones, because cuffs are often
            // fully on it.
            foreach (var includeHand in new[] { false, true })
            {
                var counts = renderers
                    .Select(renderer =>
                        (
                            Renderer: renderer,
                            Count: MeshWeightPlan.RigidHandVertexCount(renderer, arms, includeHand)
                        )
                    )
                    .ToArray();
                var max = counts.Max(candidate => candidate.Count);

                if (max > 0)
                {
                    return counts
                        .Where(candidate => candidate.Count >= MinSkinShare * max)
                        .Select(candidate => candidate.Renderer)
                        .ToHashSet();
                }
            }

            return new();
        }

        private static MeshWeightPlan TryPlan(
            SkinnedMeshRenderer renderer,
            bool isSkin,
            bool subdivideForearm,
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
                var plan = MeshWeightPlan.Create(
                    renderer,
                    arms,
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
