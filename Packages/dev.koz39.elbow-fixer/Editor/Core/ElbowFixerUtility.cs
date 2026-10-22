using System;
using System.Linq;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace KOZ39.ElbowFixer
{
    internal static class ElbowFixerUtility
    {
        // Prefix from Modular Avatar's NaNimationFilter; the rest of the name is the shape key.
        private const string HiddenBonePrefix = "NaNimatedBone for ";

        // NDMF builds the outermost avatar, so the inspector must judge fixers against the same root.
        internal static GameObject FindAvatarRoot(Component component)
        {
            var descriptors = component.GetComponentsInParent<VRCAvatarDescriptor>(true);

            return descriptors.Length > 0 ? descriptors[^1].gameObject : null;
        }

        internal static (ElbowFixer Primary, ElbowFixer[] All) FindFixers(GameObject avatarRoot)
        {
            var fixers = avatarRoot
                .GetComponentsInChildren<ElbowFixer>(true)
                .Where(fixer => IsIncluded(fixer.transform, avatarRoot.transform))
                .OrderBy(fixer => fixer.GetComponentsInParent<Transform>(true).Length)
                .ToArray();

            return (fixers.FirstOrDefault(), fixers);
        }

        // A bone Modular Avatar scales to NaN to hide part of a mesh.
        internal static bool IsHiddenBone(Transform bone) =>
            bone.name.StartsWith(HiddenBonePrefix, StringComparison.Ordinal);

        // Objects under an EditorOnly object never reach the build.
        internal static bool IsIncluded(Transform target, Transform avatarRoot) =>
            BelongsToAvatar(target, avatarRoot) && !IsEditorOnly(target, avatarRoot);

        // Objects under a nested avatar belong to that avatar.
        internal static bool BelongsToAvatar(Transform target, Transform avatarRoot)
        {
            var descriptor = target.GetComponentInParent<VRCAvatarDescriptor>(true);

            return descriptor != null && descriptor.transform == avatarRoot;
        }

        internal static bool IsEditorOnly(Transform target, Transform avatarRoot) =>
            target
                .GetComponentsInParent<Transform>(true)
                .TakeWhile(t => t != avatarRoot.parent)
                .Any(t => t.CompareTag("EditorOnly"));
    }
}
