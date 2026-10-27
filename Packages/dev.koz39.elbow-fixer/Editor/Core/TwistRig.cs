// Aim-stabilized support informed by Prefabulous Universal (MIT).
using System.Linq;
using UnityEngine;
using VRC.Dynamics;
using VRC.SDK3.Dynamics.Constraint.Components;

namespace KOZ39.ElbowFixer
{
    internal static class TwistRig
    {
        internal const string NamePrefix = "ElbowFixer.";

        internal const int SupportCount = 6;

        internal const int ReferenceCount = 2;

        // Must match MeshWeightPlan.Apply's bone order: each arm's supports, then each mirror set.
        internal static int[] WeightNodes(int boneCount, int group) =>
            Enumerable.Range(boneCount + group * SupportCount, SupportCount).ToArray();

        internal static (Transform[] Supports, Transform[] Roots) Build(Arm arm)
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

            var supports = new Transform[SupportCount];
            supports[0] = stable;
            supports[SupportCount - 1] = roll;

            for (var i = 1; i < SupportCount - 1; i++)
            {
                supports[i] = CreateChild(
                    $"{NamePrefix}Twist{i}.{arm.Suffix}",
                    stable,
                    arm.Lower.position,
                    arm.Lower.rotation
                );
                AddTwistConstraint(supports[i], roll, (float)i / (SupportCount - 1));
            }

            return (supports, new[] { stable, up, handUp, roll });
        }

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

        // The SDK normalizes a single source's weight, so only GlobalWeight can scale the twist.
        // It blends rotations, not angles: 0.25 of a 180-degree twist gives 36.9 degrees, not 45.
        private static void AddTwistConstraint(Transform twist, Transform source, float weight)
        {
            var constraint = twist.gameObject.AddComponent<VRCRotationConstraint>();
            constraint.Sources.Add(new VRCConstraintSource(source, 1));
            constraint.GlobalWeight = weight;
            constraint.ZeroConstraint();
        }
    }
}
