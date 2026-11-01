// Aim-stabilized support informed by Prefabulous Universal (MIT).
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using VRC.Dynamics;
using VRC.SDK3.Dynamics.Constraint.Components;

namespace KOZ39.ElbowFixer
{
    internal static class TwistRig
    {
        internal const string NamePrefix = "ElbowFixer.";

        private const int SkinNodeCount = 9;
        private const int SleeveNodeCount = 3;

        // Must match Build's support order: Stable, the skin twists, then the sleeve twists.
        // Clothing gets only the LowerArm's own twist, so it skips the nodes that carry the hand's.
        internal static int[] Nodes(bool isSkin, int supportCount) =>
            isSkin
                ? Enumerable.Range(0, SkinNodeCount).ToArray()
                : Enumerable
                    .Range(supportCount - SleeveNodeCount, SleeveNodeCount)
                    .Prepend(0)
                    .ToArray();

        internal static int ReferenceCount(bool hasSkin) => hasSkin ? 3 : 1;

        internal static (Transform[] Supports, Transform[] Roots) Build(
            Arm arm,
            bool hasSkin,
            bool hasClothing
        )
        {
            var forearm = arm.Hand.position - arm.Lower.position;
            var upOffset = arm.UpDirection * forearm.magnitude;
            var stable = CreateChild(
                $"{NamePrefix}Stable.{arm.Suffix}",
                arm.Lower,
                arm.Lower.position,
                arm.Lower.rotation
            );
            var up = CreateChild(
                $"{NamePrefix}Up.{arm.Suffix}",
                arm.Upper,
                arm.Lower.position + upOffset,
                arm.Upper.rotation
            );

            AddAimConstraint(stable, arm.Hand, up, forearm.normalized);

            var supports = new[] { stable };
            var roots = new List<Transform> { stable, up };

            if (hasSkin)
            {
                var handUp = CreateChild(
                    $"{NamePrefix}HandUp.{arm.Suffix}",
                    arm.Hand,
                    arm.Hand.position + upOffset,
                    arm.Hand.rotation
                );
                var roll = CreateChild(
                    $"{NamePrefix}Roll.{arm.Suffix}",
                    arm.Lower,
                    arm.Lower.position,
                    arm.Lower.rotation
                );
                AddAimConstraint(roll, arm.Hand, handUp, forearm.normalized);
                roots.Add(handUp);
                roots.Add(roll);
                supports = CreateSkinTwists(arm, stable, roll);
            }

            if (hasClothing)
            {
                supports = supports.Concat(CreateSleeveTwists(arm, stable)).ToArray();
            }

            return (supports, roots.ToArray());
        }

        internal static Transform[] Mirror(Transform hiddenBone, IEnumerable<Transform> supports) =>
            supports
                .Select(support =>
                {
                    var mirror = CreateChild(
                        $"{support.name}.Hidden",
                        hiddenBone,
                        support.position,
                        support.rotation
                    );
                    AddRotationConstraint(mirror, support);

                    return mirror;
                })
                .ToArray();

        private static Transform[] CreateSkinTwists(Arm arm, Transform stable, Transform roll)
        {
            var last = SkinNodeCount - 1;
            var twists = new Transform[SkinNodeCount];
            twists[0] = stable;

            // Blending from the LowerArm instead of Stable keeps the twist from flipping when the
            // hand turns past 180 degrees.
            twists[last] = CreateTwist(arm, stable, $"Twist{last}", arm.Lower, roll);

            // Two equal sources blend to the exact midpoint, while a GlobalWeight fraction spaces
            // the twist unevenly and thins the forearm between bones.
            for (var step = last / 2; step > 0; step /= 2)
            {
                for (var i = step; i < last; i += step * 2)
                {
                    twists[i] = CreateTwist(
                        arm,
                        stable,
                        $"Twist{i}",
                        twists[i - step],
                        twists[i + step]
                    );
                }
            }

            return twists;
        }

        private static Transform[] CreateSleeveTwists(Arm arm, Transform stable)
        {
            var middle = CreateTwist(arm, stable, "SleeveTwist2", stable, arm.Lower);

            return new[]
            {
                CreateTwist(arm, stable, "SleeveTwist1", stable, middle),
                middle,
                CreateTwist(arm, stable, "SleeveTwist3", middle, arm.Lower),
            };
        }

        private static Transform CreateChild(
            string name,
            Transform parent,
            Vector3 position,
            Quaternion rotation
        )
        {
            var child = new GameObject(name).transform;
            child.SetParent(parent, false);
            child.SetPositionAndRotation(position, rotation);

            return child;
        }

        private static void AddAimConstraint(
            Transform bone,
            Transform hand,
            Transform up,
            Vector3 aimDirection
        )
        {
            var aim = bone.gameObject.AddComponent<VRCAimConstraint>();
            aim.Sources.Add(new VRCConstraintSource(hand, 1));
            aim.AimAxis = bone.InverseTransformDirection(aimDirection);
            aim.UpAxis = bone.InverseTransformDirection((up.position - bone.position).normalized);
            aim.WorldUp = VRCConstraintBase.WorldUpType.ObjectUp;
            aim.WorldUpTransform = up;
            aim.ZeroConstraint();
        }

        private static Transform CreateTwist(
            Arm arm,
            Transform stable,
            string name,
            params Transform[] sources
        )
        {
            var twist = CreateChild(
                $"{NamePrefix}{name}.{arm.Suffix}",
                stable,
                arm.Lower.position,
                arm.Lower.rotation
            );
            AddRotationConstraint(twist, sources);

            return twist;
        }

        private static void AddRotationConstraint(Transform bone, params Transform[] sources)
        {
            var constraint = bone.gameObject.AddComponent<VRCRotationConstraint>();

            foreach (var source in sources)
            {
                constraint.Sources.Add(new VRCConstraintSource(source, 1));
            }

            constraint.ZeroConstraint();
        }
    }
}
