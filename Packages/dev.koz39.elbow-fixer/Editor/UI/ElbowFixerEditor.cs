using System.Linq;
using nadena.dev.ndmf.ui;
using UnityEditor;
using UnityEngine;
using static KOZ39.ElbowFixer.ElbowFixerLocalization;

namespace KOZ39.ElbowFixer
{
    [CustomEditor(typeof(ElbowFixer))]
    internal class ElbowFixerEditor : Editor
    {
        private SerializedProperty _targetMeshesProperty;

        private void OnEnable()
        {
            _targetMeshesProperty = serializedObject.FindProperty(nameof(ElbowFixer.targetMeshes));
            _targetMeshesProperty.isExpanded = true;
        }

        public override void OnInspectorGUI()
        {
            var fixer = (ElbowFixer)target;

            EditorGUILayout.LabelField(
                $"Version: {ElbowFixerPackageInfo.Version}",
                EditorStyles.boldLabel
            );
            EditorGUILayout.Space();

            var avatarRoot = ElbowFixerUtility.FindAvatarRoot(fixer);

            if (avatarRoot == null)
            {
                EditorGUILayout.HelpBox(Text("messages.NoAvatar"), MessageType.Warning);
            }
            else
            {
                var isIgnored = DrawDuplicateWarning(fixer, avatarRoot);

                using (new EditorGUI.DisabledScope(isIgnored))
                {
                    DrawSettings(avatarRoot);
                }
            }

            EditorGUILayout.Space();
            LanguageSwitcher.DrawImmediate();
        }

        private static bool DrawDuplicateWarning(ElbowFixer fixer, GameObject avatarRoot)
        {
            var (primaryFixer, fixers) = ElbowFixerUtility.FindFixers(avatarRoot);

            if (!fixers.Contains(fixer))
            {
                EditorGUILayout.HelpBox(Text("messages.EditorOnlyIgnored"), MessageType.Warning);
                EditorGUILayout.Space();

                return true;
            }

            if (fixers.Length < 2)
            {
                return false;
            }

            var isIgnored = primaryFixer != fixer;

            using (new EditorGUILayout.HorizontalScope())
            {
                var message = Text(
                    isIgnored ? "messages.DuplicateIgnored" : "messages.DuplicateUsed"
                );
                EditorGUILayout.HelpBox(message, MessageType.Warning);

                if (
                    GUILayout.Button(
                        Text("buttons.Select"),
                        GUILayout.Width(80f),
                        GUILayout.ExpandHeight(true)
                    )
                )
                {
                    var gameObjects = fixers.Select(item => item.gameObject).ToArray();
                    Selection.objects = gameObjects;
                    EditorGUIUtility.PingObject(gameObjects[0]);
                }
            }

            EditorGUILayout.Space();

            return isIgnored;
        }

        private void DrawSettings(GameObject avatarRoot)
        {
            serializedObject.Update();

            EditorGUILayout.PropertyField(
                _targetMeshesProperty,
                new GUIContent(Text("labels.TargetMeshes"), Text("tooltips.TargetMeshes")),
                true
            );

            if (GUILayout.Button(new GUIContent(Text("buttons.AutoAdd"), Text("tooltips.AutoAdd"))))
            {
                var current = Enumerable
                    .Range(0, _targetMeshesProperty.arraySize)
                    .Select(i =>
                        _targetMeshesProperty.GetArrayElementAtIndex(i).objectReferenceValue
                    )
                    .ToArray();

                foreach (
                    var mesh in ElbowFixerUtility
                        .FindTargetMeshes(avatarRoot)
                        .Where(mesh => !current.Contains(mesh))
                )
                {
                    _targetMeshesProperty.arraySize++;
                    _targetMeshesProperty
                        .GetArrayElementAtIndex(_targetMeshesProperty.arraySize - 1)
                        .objectReferenceValue = mesh;
                }
            }

            serializedObject.ApplyModifiedProperties();
        }
    }
}
