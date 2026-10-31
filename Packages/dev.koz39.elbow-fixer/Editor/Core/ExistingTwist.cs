using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Animations;
using VRC.Dynamics;
using VRC.SDK3.Dynamics.Constraint.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;

namespace KOZ39.ElbowFixer
{
    internal sealed class ExistingTwist
    {
        private const float CopyTolerance = 0.01f;

        private readonly ILookup<Transform, Transform[]> _sources;
        private readonly HashSet<Transform> _physBoneDriven;

        internal ExistingTwist(GameObject avatarRoot)
        {
            var unityConstraints = avatarRoot
                .GetComponentsInChildren<IConstraint>(true)
                .Where(constraint =>
                    !(constraint is PositionConstraint || constraint is ScaleConstraint)
                )
                .Select(constraint =>
                    (
                        Driven: ((Component)constraint).transform,
                        Sources: Enumerable
                            .Range(0, constraint.sourceCount)
                            .Select(i => constraint.GetSource(i).sourceTransform)
                            .ToArray()
                    )
                );
            var vrcConstraints = avatarRoot
                .GetComponentsInChildren<VRCConstraintBase>(true)
                .Where(constraint =>
                    !(constraint is VRCPositionConstraint || constraint is VRCScaleConstraint)
                )
                .Select(constraint =>
                    (
                        Driven: constraint.TargetTransform != null
                            ? constraint.TargetTransform
                            : constraint.transform,
                        Sources: Enumerable
                            .Range(0, constraint.Sources.Count)
                            .Select(i => constraint.Sources[i].SourceTransform)
                            .ToArray()
                    )
                );

            _sources = unityConstraints
                .Concat(vrcConstraints)
                .ToLookup(constraint => constraint.Driven, constraint => constraint.Sources);
            _physBoneDriven = avatarRoot
                .GetComponentsInChildren<VRCPhysBone>(true)
                .SelectMany(physBone =>
                {
                    var root = physBone.GetRootTransform();

                    return root.GetComponentsInChildren<Transform>(true)
                        .Where(t =>
                            t != root
                            && !physBone.ignoreTransforms.Any(ignored =>
                                ignored != null && t.IsChildOf(ignored)
                            )
                        );
                })
                .ToHashSet();
        }

        internal bool IsRotationDriven(Transform bone) => _sources.Contains(bone);

        internal Transform Find(Transform bone, Transform upper, Transform lower, Transform hand)
        {
            if (!bone.IsChildOf(upper) || (hand != null && bone.IsChildOf(hand)))
            {
                return null;
            }

            var ancestors = bone.GetComponentsInParent<Transform>(true);

            if (!bone.IsChildOf(lower))
            {
                return ancestors
                    .TakeWhile(t => t != upper)
                    .FirstOrDefault(t => TurnsWith(t, lower));
            }

            return ancestors
                .TakeWhile(t => t != lower)
                .FirstOrDefault(t =>
                    (
                        !ElbowFixerUtility.IsHiddenBone(t)
                        && t.name.Contains("twist", StringComparison.OrdinalIgnoreCase)
                    ) || IsRotationDriven(t)
                );
        }

        // Rebinding would cost a bone under a twist bone, like a PhysBone chain, its motion.
        internal (Transform ArmBone, Transform Blocker) FindRebind(
            Transform bone,
            Transform upper,
            Transform lower,
            Transform hand
        )
        {
            if (hand != null && IsLowerArmCopy(bone, lower, hand))
            {
                return (lower, null);
            }

            var twist = Find(bone, upper, lower, hand);

            if (twist == null)
            {
                return (null, null);
            }

            return twist == bone ? (bone.IsChildOf(lower) ? lower : upper, null) : (null, twist);
        }

        // Modular Avatar can leave an outfit's lower arm as a child of the avatar's,
        // and the sleeve weights then sit on a bone that only follows the LowerArm.
        private bool IsLowerArmCopy(Transform bone, Transform lower, Transform hand) =>
            bone.parent == lower
            && !ElbowFixerUtility.IsHiddenBone(bone)
            && !IsRotationDriven(bone)
            && !_physBoneDriven.Contains(bone)
            && Vector3.Distance(bone.position, lower.position)
                < CopyTolerance * Vector3.Distance(hand.position, lower.position);

        private bool TurnsWith(Transform bone, Transform lower) =>
            _sources[bone]
                .Any(sources => sources.Any(source => source != null && source.IsChildOf(lower)));
    }
}
