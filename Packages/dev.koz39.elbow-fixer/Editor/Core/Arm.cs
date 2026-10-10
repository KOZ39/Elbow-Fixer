using System;
using System.Linq;
using UnityEngine;
using UnityEngine.Animations;
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;
using static KOZ39.ElbowFixer.ElbowFixerException;
using static KOZ39.ElbowFixer.ElbowFixerLocalization;

namespace KOZ39.ElbowFixer
{
    internal sealed class ArmSide
    {
        internal static readonly ArmSide[] All =
        {
            new ArmSide(
                "L",
                "labels.Left",
                HumanBodyBones.LeftUpperArm,
                HumanBodyBones.LeftLowerArm,
                HumanBodyBones.LeftHand
            ),
            new ArmSide(
                "R",
                "labels.Right",
                HumanBodyBones.RightUpperArm,
                HumanBodyBones.RightLowerArm,
                HumanBodyBones.RightHand
            ),
        };

        internal readonly string Suffix;
        internal readonly string NameKey;
        internal readonly HumanBodyBones Upper;
        internal readonly HumanBodyBones Lower;
        internal readonly HumanBodyBones Hand;

        private ArmSide(
            string suffix,
            string nameKey,
            HumanBodyBones upper,
            HumanBodyBones lower,
            HumanBodyBones hand
        )
        {
            Suffix = suffix;
            NameKey = nameKey;
            Upper = upper;
            Lower = lower;
            Hand = hand;
        }
    }

    internal sealed class Arm
    {
        private const float MinForearmLength = 0.001f;
        private const float ScaleTolerance = 0.0001f;

        internal readonly string Suffix;
        internal readonly string Name;
        internal readonly Transform Upper;
        internal readonly Transform Lower;
        internal readonly Transform Hand;

        // Normal of the forward elbow bend plane; stays perpendicular to the forearm while bending.
        internal readonly Vector3 UpDirection;

        private Arm(
            ArmSide side,
            string name,
            Transform upper,
            Transform lower,
            Transform hand,
            Vector3 upDirection
        )
        {
            Suffix = side.Suffix;
            Name = name;
            Upper = upper;
            Lower = lower;
            Hand = hand;
            UpDirection = upDirection;
        }

        internal static Arm[] Resolve(GameObject avatarRoot)
        {
            var animator = avatarRoot.GetComponent<Animator>();

            Require(
                avatarRoot.TryGetComponent<VRCAvatarDescriptor>(out _)
                    && animator != null
                    && animator.isHuman,
                "InvalidAvatar"
            );
            Require(
                !avatarRoot
                    .GetComponentsInChildren<Transform>(true)
                    .Any(t => t.name.StartsWith(TwistRig.NamePrefix, StringComparison.Ordinal)),
                "AlreadyFixed"
            );

            return ArmSide
                .All.Select(side => ResolveSide(avatarRoot.transform, animator, side))
                .ToArray();
        }

        private static Arm ResolveSide(Transform avatarRoot, Animator animator, ArmSide side)
        {
            var name = Text(side.NameKey);
            var upper = animator.GetBoneTransform(side.Upper);
            var lower = animator.GetBoneTransform(side.Lower);
            var hand = animator.GetBoneTransform(side.Hand);

            Require(upper != null && lower != null && hand != null, "MissingArmBones", name);
            Require(
                upper.IsChildOf(avatarRoot) && lower.parent == upper && hand.parent == lower,
                "IndirectArmChain",
                name
            );

            // The direct child offset avoids subtracting rounded world positions far from the origin.
            var forearm = lower.TransformVector(hand.localPosition);
            Require(forearm.magnitude > MinForearmLength, "ShortForearm", name);

            // Ancestor scales also affect the arm, even above the avatar root.
            for (var t = hand; t != null; t = t.parent)
            {
                Require(IsUniformPositive(t.localScale), "UnsupportedScale", t.name);
            }

            RejectExistingTwist(lower, hand);

            var upDirection = Vector3.Cross(forearm.normalized, avatarRoot.forward);
            Require(upDirection.magnitude > 0.1f, "ForearmParallel", name);

            return new Arm(side, name, upper, lower, hand, upDirection.normalized);
        }

        private static bool IsUniformPositive(Vector3 scale) =>
            scale.x > 0
            && Mathf.Abs(scale.x - scale.y) < ScaleTolerance * scale.x
            && Mathf.Abs(scale.x - scale.z) < ScaleTolerance * scale.x;

        // Twist bones or constraints in the forearm would fight the generated rig.
        // Objects attached under the hand, such as accessories, are not part of the forearm.
        private static void RejectExistingTwist(Transform lower, Transform hand)
        {
            foreach (var t in lower.GetComponentsInChildren<Transform>(true))
            {
                var inForearm = !t.IsChildOf(hand);
                var isTwistBone =
                    inForearm
                    && t != lower
                    && t.name.IndexOf("twist", StringComparison.OrdinalIgnoreCase) >= 0;
                var hasConstraint =
                    (inForearm || t == hand)
                    && (
                        t.GetComponent<IConstraint>() != null
                        || t.GetComponent<VRCConstraintBase>() != null
                    );

                Require(!isTwistBone && !hasConstraint, "ExistingTwist", t.name);
            }
        }
    }
}
