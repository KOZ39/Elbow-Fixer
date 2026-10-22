// Aim-stabilized support informed by Prefabulous Universal (MIT). See LICENSE.
using System.Linq;
using UnityEngine;
using VRC.Dynamics;
using VRC.SDK3.Dynamics.Constraint.Components;

namespace KOZ39.ElbowFixer
{
    // Stable aims at the hand with its up toward a reference on the UpperArm, so it carries none of the forearm twist.
    // Roll aims the same way with its up toward a reference on the Hand, so it carries the whole twist at the wrist:
    // the LowerArm's and the Hand's, which a humanoid splits between them.
    internal static class TwistRig
    {
        internal const string NamePrefix = "ElbowFixer.";

        // Stable, the twist bones and Roll.
        internal const int SupportCount = 6;

        // Up and HandUp, which are not skinned.
        internal const int ReferenceCount = 2;

        // Must match the bone order written by MeshWeightPlan.Apply: each arm's supports, then each mirror set.
        internal static int[] WeightNodes(int boneCount, int group) =>
            Enumerable.Range(boneCount + group * SupportCount, SupportCount).ToArray();

        internal static Transform[] Build(Arm arm)
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

            // Placed straight off the wrist along the up direction, so bending the wrist either way does not turn Roll.
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

        // A single source's weight is normalized by the SDK, so only GlobalWeight can scale the twist. It blends the
        // rotations, not the angle: 0.25 of a 180-degree twist gives 36.9 degrees, not 45.
        private static void AddTwistConstraint(Transform twist, Transform source, float weight)
        {
            var constraint = twist.gameObject.AddComponent<VRCRotationConstraint>();
            constraint.Sources.Add(new VRCConstraintSource(source, 1));
            constraint.GlobalWeight = weight;
            constraint.ZeroConstraint();
        }
    }
}
