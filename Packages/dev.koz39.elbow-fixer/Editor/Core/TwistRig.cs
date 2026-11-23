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
            var roll = CreateChild(
                $"{NamePrefix}Roll.{arm.Suffix}",
                arm.Lower,
                arm.Lower.position,
                arm.Lower.rotation
            );
            AddAimConstraint(roll, arm.Hand, handUp, forearm.normalized);

            return (CreateTwists(arm, stable, roll), new[] { stable, up, handUp, roll });
        }

        internal static Transform Mirror(Transform hiddenBone, Transform support)
        {
            var mirror = CreateChild(
                $"{support.name}.Hidden",
                hiddenBone,
                support.position,
                support.rotation
            );
            AddRotationConstraint(mirror, (support, 1));

            return mirror;
        }

        private static Transform[] CreateTwists(Arm arm, Transform stable, Transform roll)
        {
            var last = SupportCount - 1;
            var twists = new Transform[SupportCount];
            twists[0] = stable;

            twists[last] = CreateTwist(arm, stable, $"Twist{last}", (roll, 1));

            // The node nearest the LowerArm's share of the hand twist follows the LowerArm, so each
            // blend stays below a half turn for longer.
            // Off that share, a little of the nearer end brings the node back to its own share.
            var share = arm.LowerArmTwist * last;
            var anchor = Mathf.Clamp(Mathf.RoundToInt(share), 1, last - 1);
            var anchorSources =
                Mathf.Approximately(share, anchor) ? new[] { (arm.Lower, 1f) }
                : share > anchor
                    ? new[] { (stable, (share - anchor) / share), (arm.Lower, anchor / share) }
                : new[]
                {
                    (arm.Lower, (last - anchor) / (last - share)),
                    (twists[last], (anchor - share) / (last - share)),
                };
            twists[anchor] = CreateTwist(arm, stable, $"Twist{anchor}", anchorSources);
            Fill(0, anchor);
            Fill(anchor, last);

            return twists;

            // Two equal sources blend to the exact midpoint, but unequal weights land a little off
            // their share, so they split only spans that can't be halved.
            void Fill(int from, int to)
            {
                if (to - from > 1 && (to - from) % 2 == 0)
                {
                    var middle = (from + to) / 2;
                    twists[middle] = CreateTwist(
                        arm,
                        stable,
                        $"Twist{middle}",
                        (twists[from], 1),
                        (twists[to], 1)
                    );
                    Fill(from, middle);
                    Fill(middle, to);
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
            AddRotationConstraint(twist, sources);

            return twist;
        }

        private static void AddRotationConstraint(
            Transform bone,
            params (Transform Source, float Weight)[] sources
        )
        {
            var constraint = bone.gameObject.AddComponent<VRCRotationConstraint>();

            foreach (var (source, weight) in sources)
            {
                constraint.Sources.Add(new VRCConstraintSource(source, weight));
            }

            constraint.ZeroConstraint();
        }
    }
}
