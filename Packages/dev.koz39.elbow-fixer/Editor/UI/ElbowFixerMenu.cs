using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace KOZ39.ElbowFixer
{
    internal static class ElbowFixerMenu
    {
        private const string PrefabGuid = "541798df2ab64c3580d5155c243797b5";
        private const string GameObjectMenuPath = "GameObject/Elbow Fixer/Setup";

        [MenuItem(GameObjectMenuPath, true)]
        private static bool ValidateSetup() => GetTargetAvatars().Length > 0;

        [MenuItem(GameObjectMenuPath)]
        private static void Setup(MenuCommand command)
        {
            var context = command.context as GameObject;

            // With multiple objects selected, Unity invokes the menu once per object.
            if (context != null && context != Selection.activeGameObject)
            {
                return;
            }

            var targets = GetTargetAvatars(context);

            if (targets.Length == 0)
            {
                return;
            }

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                AssetDatabase.GUIDToAssetPath(PrefabGuid)
            );

            if (prefab == null)
            {
                Debug.LogError($"{ElbowFixerPackageInfo.LogPrefix} Could not load the prefab.");
                return;
            }

            Undo.IncrementCurrentGroup();
            var undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName($"Setup {ElbowFixerPackageInfo.DisplayName}");

            var created = targets
                .Select(target => SetupAvatar(target, prefab))
                .Where(instance => instance != null)
                .ToArray();

            Undo.CollapseUndoOperations(undoGroup);

            if (created.Length > 0)
            {
                Selection.objects = created;
                EditorGUIUtility.PingObject(created[0]);
            }
        }

        private static GameObject[] GetTargetAvatars(GameObject context = null) =>
            Selection
                .gameObjects.Append(context)
                .Where(target => target != null)
                .Where(target => !PrefabUtility.IsPartOfPrefabAsset(target))
                .Where(target => target.TryGetComponent<VRCAvatarDescriptor>(out _))
                .Distinct()
                .ToArray();

        private static GameObject SetupAvatar(GameObject avatar, GameObject prefab)
        {
            var displayName = ElbowFixerPackageInfo.DisplayName;

            if (ElbowFixerUtility.FindFixers(avatar).Primary != null)
            {
                Debug.LogWarning(
                    $"{ElbowFixerPackageInfo.LogPrefix} Skipped avatar '{avatar.name}': {displayName} already exists.",
                    avatar
                );

                return null;
            }

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, avatar.transform);
            var fixer = instance.GetComponent<ElbowFixer>();
            fixer.targetMeshes = ElbowFixerUtility.FindTargetMeshes(avatar);

            PrefabUtility.RecordPrefabInstancePropertyModifications(fixer);
            Undo.RegisterCreatedObjectUndo(instance, $"Setup {displayName}");

            return instance;
        }
    }
}
