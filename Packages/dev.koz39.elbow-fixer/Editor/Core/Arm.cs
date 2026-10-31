using System;
using System.Linq;
using UnityEngine;
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

        private const string PrefabulousTwistType =
            "Prefabulous.Universal.Common.Runtime.PrefabulousGenerateTwistBones";

        internal readonly string Suffix;
        internal readonly string Name;
        internal readonly Transform Upper;
        internal readonly Transform Lower;
        internal readonly Transform Hand;
        internal readonly Vector3 UpDirection;
        private readonly ExistingTwist _existingTwist;

        private Arm(
            ArmSide side,
            string name,
            Transform upper,
            Transform lower,
            Transform hand,
            Vector3 upDirection,
            ExistingTwist existingTwist
        )
        {
            Suffix = side.Suffix;
            Name = name;
            Upper = upper;
            Lower = lower;
            Hand = hand;
            UpDirection = upDirection;
            _existingTwist = existingTwist;
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

            // ExistingTwist can miss twist bones Prefabulous adds later, so find its component.
            var prefabulousTwist = avatarRoot
                .GetComponentsInChildren<MonoBehaviour>(true)
                .FirstOrDefault(component =>
                    component != null && component.GetType().FullName == PrefabulousTwistType
                );
            Require(prefabulousTwist == null, "PrefabulousTwist", prefabulousTwist);

            var existingTwist = new ExistingTwist(avatarRoot);

            return ArmSide
                .All.Select(side =>
                    ResolveSide(avatarRoot.transform, animator, side, existingTwist)
                )
                .ToArray();
        }

        private static Arm ResolveSide(
            Transform avatarRoot,
            Animator animator,
            ArmSide side,
            ExistingTwist existingTwist
        )
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

            var forearm = lower.TransformVector(hand.localPosition);
            Require(forearm.magnitude > MinForearmLength, "ShortForearm", name);

            foreach (var t in hand.GetComponentsInParent<Transform>(true))
            {
                Require(IsUniformPositive(t.localScale), "UnsupportedScale", t);
            }

            Require(!existingTwist.IsRotationDriven(lower), "ExistingTwist", lower);
            Require(!existingTwist.IsRotationDriven(hand), "ExistingTwist", hand);

            var upDirection = Vector3.Cross(forearm.normalized, avatarRoot.forward);
            Require(upDirection.magnitude > 0.1f, lower, "ForearmParallel", name);

            return new Arm(side, name, upper, lower, hand, upDirection.normalized, existingTwist);
        }

        private static bool IsUniformPositive(Vector3 scale) =>
            scale.x > 0
            && Mathf.Abs(scale.x - scale.y) < ScaleTolerance * scale.x
            && Mathf.Abs(scale.x - scale.z) < ScaleTolerance * scale.x;

        internal (Transform ArmBone, Transform Blocker) FindRebind(Transform bone) =>
            _existingTwist.FindRebind(bone, Upper, Lower, Hand);
    }
}
