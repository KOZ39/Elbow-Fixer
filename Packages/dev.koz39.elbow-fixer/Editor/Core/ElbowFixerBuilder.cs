using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEngine;
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
            AnimatorServicesContext animators,
            Action<ElbowFixerException> skip
        )
        {
            // Build copies can be far from the origin, where world-space math loses precision.
            var position = avatarRoot.transform.position;

            try
            {
                avatarRoot.transform.position = Vector3.zero;

                return BuildAtOrigin(avatarRoot, renderers, subdivideForearm, animators, skip);
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
            AnimatorServicesContext animators,
            Action<ElbowFixerException> skip
        )
        {
            // While AnimatorServicesContext is active, the clips live in its virtual controllers,
            // not in the avatar descriptor's.
            string VirtualPath(Transform target) =>
                animators.ObjectPathRemapper.GetVirtualPathForObject(target);

            var arms = Arm.Resolve(avatarRoot);
            var meshSwaps =
                animators == null
                    ? new()
                    : renderers
                        .Select(renderer =>
                            (
                                Renderer: renderer,
                                Clip: animators
                                    .AnimationIndex.GetClipsForBinding(
                                        EditorCurveBinding.PPtrCurve(
                                            VirtualPath(renderer.transform),
                                            typeof(SkinnedMeshRenderer),
                                            "m_Mesh"
                                        )
                                    )
                                    .FirstOrDefault()
                            )
                        )
                        .Where(swap => swap.Clip != null)
                        .ToDictionary(swap => swap.Renderer, swap => swap.Clip.Name);
            var hung = FindHungObjects(arms);
            var hungObjects = hung.SelectMany(objects => objects).ToArray();
            var carriers = FindCarriers(
                avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true),
                hung
            );

            var skins = FindSkins(renderers, arms);
            var plans = renderers
                .Select(renderer =>
                    TryPlan(
                        renderer,
                        skins.Contains(renderer),
                        subdivideForearm,
                        arms,
                        hungObjects
                            .Where(hungObject =>
                                carriers[hungObject].All(carrier => carrier == renderer)
                            )
                            .ToHashSet(),
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
                .Where(plan => plan != null)
                .ToArray();

            if (applied.Length == 0)
            {
                foreach (var root in rigs.SelectMany(rig => rig.Roots))
                {
                    UnityEngine.Object.DestroyImmediate(root.gameObject);
                }

                return null;
            }

            var betweenCount = MoveHungObjects(arms, hung, carriers, supports, applied);
            var boneCount =
                supports.Sum(bones => bones.Length) + applied.Sum(plan => plan.MirrorCount);
            // Each arm's Roll and each ribbon's half-node twist have constraints but no weights.
            var report = new StringBuilder(
                Text(
                    "messages.BuildSummary",
                    boneCount,
                    boneCount + arms.Length + betweenCount,
                    arms.Length * TwistRig.ReferenceCount + betweenCount
                )
            );

            foreach (var plan in applied)
            {
                report
                    .AppendLine()
                    .Append(
                        plan.AddedVertexCount > 0
                            ? Text(
                                "messages.SubdivisionSummary",
                                plan.Renderer.name,
                                plan.AddedVertexCount
                            )
                            : Text(
                                plan.IsSkin ? "messages.SkinPlanSummary" : "messages.PlanSummary",
                                plan.Renderer.name
                            )
                    );
            }

            return report.ToString();
        }

        private static Transform[][] FindHungObjects(Arm[] arms) =>
            arms.Select(arm =>
                    arm.Lower.Cast<Transform>()
                        .Where(child =>
                            child != arm.Hand
                            && !ElbowFixerUtility.IsHiddenBone(child)
                            && arm.FindRebind(child) == default
                        )
                        .Concat(
                            arm.Lower.Cast<Transform>()
                                .Where(arm.IsLowerArmCopy)
                                .SelectMany(copy => copy.Cast<Transform>())
                                .Where(arm.IsPhysBoneRoot)
                        )
                        .ToArray()
                )
                .ToArray();

        // A mesh that weights bones under a hung object together with bones outside it, such as a
        // sleeve on its own PhysBone chain, stretches between them once the object turns with a
        // twist bone.
        private static ILookup<Transform, SkinnedMeshRenderer> FindCarriers(
            IEnumerable<SkinnedMeshRenderer> renderers,
            Transform[][] hung
        )
        {
            var hungObjects = hung.SelectMany(objects => objects).ToArray();
            var carriers = new List<(Transform HungObject, SkinnedMeshRenderer Renderer)>();

            foreach (var renderer in renderers)
            {
                var mesh = renderer.sharedMesh;

                if (mesh == null)
                {
                    continue;
                }

                var used = ForearmWeights.UsedBones(mesh.boneWeights, renderer.bones);

                carriers.AddRange(
                    hungObjects
                        .Where(hungObject =>
                            used.Any(bone => bone.IsChildOf(hungObject))
                            && used.Any(bone => !bone.IsChildOf(hungObject))
                        )
                        .Select(hungObject => (hungObject, renderer))
                );
            }

            return carriers.ToLookup(carrier => carrier.HungObject, carrier => carrier.Renderer);
        }

        // An object hung on the LowerArm, such as a PhysBone chain or a watch, turns with the
        // LowerArm, while the skin under it follows the twist bone at its position.
        private static int MoveHungObjects(
            Arm[] arms,
            Transform[][] hung,
            ILookup<Transform, SkinnedMeshRenderer> carriersBefore,
            Transform[][] supports,
            MeshWeightPlan[] applied
        )
        {
            var appliedRenderers = applied.Select(plan => plan.Renderer).ToHashSet();
            var appliedCarriers = FindCarriers(appliedRenderers, hung);
            var ribbonNodes = applied
                .SelectMany(plan => plan.RibbonNodes)
                .GroupBy(pair => pair.Key, pair => pair.Value)
                .ToDictionary(group => group.Key, group => group.First());
            var betweens = new Dictionary<(int Side, int Node), Transform>();

            for (var side = 0; side < arms.Length; side++)
            {
                var arm = arms[side];
                var movable = hung[side]
                    .Where(hungObject =>
                        carriersBefore[hungObject]
                            .Where(carrier => !appliedRenderers.Contains(carrier))
                            .Concat(appliedCarriers[hungObject])
                            .All(carrier =>
                                applied.Any(plan =>
                                    plan.Renderer == carrier
                                    && plan.RibbonNodes.ContainsKey(hungObject)
                                )
                            )
                    )
                    .ToArray();

                foreach (var hungObject in movable)
                {
                    var node = ribbonNodes.TryGetValue(hungObject, out var ribbonNode)
                        ? ribbonNode
                        : TwistRig.NodeAt(arm, hungObject.position);
                    var lower = (int)node;

                    if (node > lower && !betweens.ContainsKey((side, lower)))
                    {
                        betweens[(side, lower)] = TwistRig.CreateBetween(
                            arm,
                            supports[side],
                            lower
                        );
                    }

                    hungObject.SetParent(
                        node > lower ? betweens[(side, lower)] : supports[side][lower],
                        true
                    );
                }
            }

            return betweens.Count;
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
            HashSet<Transform> movableRoots,
            Dictionary<SkinnedMeshRenderer, string> meshSwaps,
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
                    subdivideForearm && isSkin,
                    movableRoots
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

        private static MeshWeightPlan TryApply(
            MeshWeightPlan plan,
            Transform[][] supports,
            Action<ElbowFixerException> skip
        )
        {
            try
            {
                plan.Apply(supports);

                return plan;
            }
            catch (ElbowFixerException e)
            {
                skip(e);

                return null;
            }
        }
    }
}
