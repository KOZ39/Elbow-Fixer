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

        internal const int SupportCount = 9;
        internal const int ReferenceCount = 3;

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

            // Beside the hand, wrist flexion never lines HandUp up with the aim.
            var handUp = CreateChild(
                $"{NamePrefix}HandUp.{arm.Suffix}",
                arm.Hand,
                arm.Hand.position
                    + Vector3.Cross(forearm.normalized, arm.UpDirection) * forearm.magnitude,
                arm.Hand.rotation
            );
            var wrist = CreateChild(
                $"{NamePrefix}Twist{SupportCount - 1}.{arm.Suffix}",
                arm.Lower,
                arm.Lower.position,
                arm.Lower.rotation
            );
            AddAimConstraint(wrist, arm.Hand, handUp, forearm.normalized);
            var halfLower = CreateChild(
                $"{NamePrefix}HalfLower.{arm.Suffix}",
                arm.Lower,
                arm.Lower.position,
                arm.Lower.rotation
            );
            AddRotationConstraint(halfLower, false, stable, arm.Lower);

            return (
                CreateTwists(arm, stable, halfLower, handUp, wrist),
                new[] { stable, up, handUp, wrist, halfLower }
            );
        }

        internal static int NodeAt(Arm arm, Vector3 position)
        {
            var forearm = arm.Hand.position - arm.Lower.position;
            var t = Vector3.Dot(position - arm.Lower.position, forearm) / forearm.sqrMagnitude;

            return Mathf.Clamp(Mathf.RoundToInt(t * (SupportCount - 1)), 1, SupportCount - 1);
        }

        internal static int LowerArmNode(Arm arm) =>
            Mathf.Clamp(
                Mathf.RoundToInt(arm.LowerArmTwist * (SupportCount - 1)),
                1,
                SupportCount - 2
            );

        internal static Transform CreateBetween(Arm arm, Transform[] supports, int node) =>
            CreateTwist(arm, supports[0], $"Twist{node}.5", supports[node], supports[node + 1]);

        internal static Transform Mirror(Transform hiddenBone, Transform support)
        {
            var mirror = CreateChild(
                $"{support.name}.Hidden",
                hiddenBone,
                support.position,
                support.rotation
            );
            AddRotationConstraint(mirror, false, support);

            return mirror;
        }

        private static Transform[] CreateTwists(
            Arm arm,
            Transform stable,
            Transform halfLower,
            Transform handUp,
            Transform wrist
        )
        {
            var last = SupportCount - 1;
            var middle = last / 2;
            var twists = new Transform[SupportCount];
            twists[0] = stable;
            twists[last] = wrist;

            // HandUp never turns against the Hand, so in local space it halves the wrist's turn
            // against the LowerArm.
            // Adding that to HalfLower keeps each blend below a half turn even when the LowerArm
            // turns with the hand.
            twists[middle] = CreateChild(
                $"{NamePrefix}Twist{middle}.{arm.Suffix}",
                halfLower,
                arm.Lower.position,
                arm.Lower.rotation
            );
            AddRotationConstraint(twists[middle], true, handUp, wrist);

            // Two equal sources blend to the exact midpoint, while a GlobalWeight fraction spaces
            // the twist unevenly and thins the forearm between bones.
            for (var step = middle / 2; step > 0; step /= 2)
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
            AddRotationConstraint(twist, false, sources);

            return twist;
        }

        private static void AddRotationConstraint(
            Transform bone,
            bool solveInLocalSpace,
            params Transform[] sources
        )
        {
            var constraint = bone.gameObject.AddComponent<VRCRotationConstraint>();
            constraint.SolveInLocalSpace = solveInLocalSpace;

            foreach (var source in sources)
            {
                constraint.Sources.Add(new VRCConstraintSource(source, 1));
            }

            constraint.ZeroConstraint();
        }
    }
}
