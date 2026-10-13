// Aim-stabilized support informed by Prefabulous Universal (MIT). See LICENSE.
using System.Linq;
using UnityEngine;
using VRC.Dynamics;
using VRC.SDK3.Dynamics.Constraint.Components;

namespace KOZ39.ElbowFixer
{
    // Stable aims at the hand with its up toward a reference on the UpperArm, so it carries none of the forearm twist.
    internal static class TwistRig
    {
        internal const string NamePrefix = "ElbowFixer.";

        internal const int SupportCount = 4;

        // Must match the bone order written by MeshWeightPlan.Apply: each arm's supports, then each mirror set.
        internal static int[] WeightNodes(int boneCount, int group, int sourceIndex) =>
            Enumerable
                .Range(boneCount + group * SupportCount, SupportCount)
                .Append(sourceIndex)
                .ToArray();

        internal static Transform[] Build(Arm arm)
        {
            var forearm = arm.Hand.position - arm.Lower.position;
            var stable = CreateChild(
                $"{NamePrefix}Stable.{arm.Suffix}",
                arm.Lower,
                arm.Lower.position,
                arm.Lower.rotation
            );
            var up = CreateChild(
                $"{NamePrefix}Up.{arm.Suffix}",
                arm.Upper,
                arm.Lower.position + arm.UpDirection * forearm.magnitude,
                arm.Upper.rotation
            );

            AddAimConstraint(stable, arm.Hand, up, forearm.normalized);

            var supports = new Transform[SupportCount];
            supports[0] = stable;

            for (var i = 1; i < SupportCount; i++)
            {
                supports[i] = CreateChild(
                    $"{NamePrefix}Twist{i}.{arm.Suffix}",
                    stable,
                    arm.Lower.position,
                    arm.Lower.rotation
                );
                AddTwistConstraint(supports[i], arm.Lower, (float)i / SupportCount);
            }

            return supports;
        }

        // The hidden bone's NaN scale reaches the copies through the hierarchy, so they hide and show with it.
        internal static Transform[] Mirror(Transform hiddenBone, Transform[] supports) =>
            supports
                .Select(support =>
                {
                    var mirror = CreateChild(
                        $"{support.name}.Hidden",
                        hiddenBone,
                        support.position,
                        support.rotation
                    );
                    AddTwistConstraint(mirror, support, 1);

                    return mirror;
                })
                .ToArray();

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
            Transform stable,
            Transform hand,
            Transform up,
            Vector3 aimDirection
        )
        {
            var aim = stable.gameObject.AddComponent<VRCAimConstraint>();
            aim.Sources.Add(new VRCConstraintSource(hand, 1));
            aim.AimAxis = stable.InverseTransformDirection(aimDirection);
            aim.UpAxis = stable.InverseTransformDirection(
                (up.position - stable.position).normalized
            );
            aim.WorldUp = VRCConstraintBase.WorldUpType.ObjectUp;
            aim.WorldUpTransform = up;
            aim.ZeroConstraint();
        }

        // A single source's weight is normalized by the SDK, so only GlobalWeight can set the twist fraction.
        private static void AddTwistConstraint(Transform twist, Transform lower, float weight)
        {
            var constraint = twist.gameObject.AddComponent<VRCRotationConstraint>();
            constraint.Sources.Add(new VRCConstraintSource(lower, 1));
            constraint.GlobalWeight = weight;
            constraint.ZeroConstraint();
        }
    }
}
