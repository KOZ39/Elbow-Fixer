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

        internal const int SupportCount = 13;
        internal const int ReferenceCount = 3;
        private const float MiddleWristShare = 0.375f;

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
            AddRotationConstraint(halfLower, false, (stable, 1), (arm.Lower, 1));

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

        internal static float LowerArmNode(Arm arm)
        {
            var last = SupportCount - 1;
            var share = arm.LowerArmTwist;

            float Turn(float node) =>
                share * node / last
                + (1 - share)
                    * (
                        node <= last / 2
                            ? MiddleWristShare * node / (last / 2)
                            : MiddleWristShare
                                + (1 - MiddleWristShare) * (node - last / 2) / (last / 2)
                    );

            return Enumerable
                .Range(2, 2 * last - 3)
                .Select(half => half / 2f)
                .OrderBy(node => Mathf.Abs(Turn(node) - share))
                .First();
        }

        internal static Transform CreateBetween(Arm arm, Transform[] supports, int node) =>
            CreateTwist(
                arm,
                supports[0],
                $"Twist{node}.5",
                (supports[node], 1),
                (supports[node + 1], 1)
            );

        internal static Transform Mirror(Transform hiddenBone, Transform support)
        {
            var mirror = CreateChild(
                $"{support.name}.Hidden",
                hiddenBone,
                support.position,
                support.rotation
            );
            AddRotationConstraint(mirror, false, (support, 1));

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

            // HandUp never turns against the Hand, so in local space it takes three eighths of the
            // wrist's turn against the LowerArm.
            // Adding that to HalfLower keeps each blend below a half turn even when the LowerArm
            // turns with the hand.
            // Less than a half leaves more of the hand twist to the wrist half, so the elbow half
            // twists less.
            twists[middle] = CreateChild(
                $"{NamePrefix}Twist{middle}.{arm.Suffix}",
                halfLower,
                arm.Lower.position,
                arm.Lower.rotation
            );
            AddRotationConstraint(
                twists[middle],
                true,
                (handUp, 1 - MiddleWristShare),
                (wrist, MiddleWristShare)
            );

            Fill(0, middle);
            Fill(middle, last);

            return twists;

            // Two equal sources blend to the exact midpoint, while a GlobalWeight fraction spaces
            // the twist unevenly and thins the forearm between bones.
            // Unequal source weights land a little off their share at large turns, so they fill
            // only spans that can't be halved.
            void Fill(int from, int to)
            {
                if (to - from > 1 && (to - from) % 2 == 0)
                {
                    var center = (from + to) / 2;
                    twists[center] = CreateTwist(
                        arm,
                        stable,
                        $"Twist{center}",
                        (twists[from], 1),
                        (twists[to], 1)
                    );
                    Fill(from, center);
                    Fill(center, to);
                    return;
                }

                for (var i = from + 1; i < to; i++)
                {
                    twists[i] = CreateTwist(
                        arm,
                        stable,
                        $"Twist{i}",
                        (twists[from], (float)(to - i) / (to - from)),
                        (twists[to], (float)(i - from) / (to - from))
                    );
                }
            }
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
            params (Transform Source, float Weight)[] sources
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
            params (Transform Source, float Weight)[] sources
        )
        {
            var constraint = bone.gameObject.AddComponent<VRCRotationConstraint>();
            constraint.SolveInLocalSpace = solveInLocalSpace;

            foreach (var (source, weight) in sources)
            {
                constraint.Sources.Add(new VRCConstraintSource(source, weight));
            }

            constraint.ZeroConstraint();
        }
    }
}
