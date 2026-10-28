using System;
using System.Linq;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace KOZ39.ElbowFixer
{
    internal static class ElbowFixerUtility
    {
        private const string HiddenBonePrefix = "NaNimatedBone for ";
        private const string HiddenBufferPrefix = "NaNimatedBuffer";

        // NDMF builds the outermost avatar, so the inspector must judge fixers by that root.
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

        internal static bool IsHiddenBone(Transform bone) =>
            bone.name.StartsWith(HiddenBonePrefix, StringComparison.Ordinal);

        // The hidden bone keeps the bindpose from before the merge, so only where it sits tells
        // which bone it copies.
        internal static Transform CopiedBone(Transform hiddenBone) =>
            hiddenBone
                .GetComponentsInParent<Transform>(true)
                .Skip(1)
                .First(t => !t.name.StartsWith(HiddenBufferPrefix, StringComparison.Ordinal));

        internal static bool IsIncluded(Transform target, Transform avatarRoot) =>
            BelongsToAvatar(target, avatarRoot) && !IsEditorOnly(target, avatarRoot);

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
