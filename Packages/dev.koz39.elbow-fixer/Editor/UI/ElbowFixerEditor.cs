using System;
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

        // Finding the meshes reads every vertex, so the skipped ones are kept from the last auto add.
        private (SkinnedMeshRenderer Mesh, Transform Blocker)[] _skippedMeshes = Array.Empty<(
            SkinnedMeshRenderer,
            Transform
        )>();

        private bool _isSkippedListExpanded;

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
                var isIgnored = DrawIgnoredWarning(fixer, avatarRoot);

                using (new EditorGUI.DisabledScope(isIgnored))
                {
                    DrawSettings(avatarRoot);
                }

                if (!isIgnored)
                {
                    DrawMeshWarnings(fixer, avatarRoot);
                }
            }

            EditorGUILayout.Space();
            LanguageSwitcher.DrawImmediate();
        }

        private static bool DrawIgnoredWarning(ElbowFixer fixer, GameObject avatarRoot)
        {
            var (primaryFixer, fixers) = ElbowFixerUtility.FindFixers(avatarRoot);

            if (!fixers.Contains(fixer))
            {
                EditorGUILayout.HelpBox(Text("messages.Excluded"), MessageType.Warning);
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

                var (targets, skipped) = TargetMeshes.Find(avatarRoot);
                _skippedMeshes = skipped;
                _isSkippedListExpanded = true;

                foreach (var mesh in targets.Where(mesh => !current.Contains(mesh)))
                {
                    _targetMeshesProperty.arraySize++;
                    _targetMeshesProperty
                        .GetArrayElementAtIndex(_targetMeshesProperty.arraySize - 1)
                        .objectReferenceValue = mesh;
                }
            }

            serializedObject.ApplyModifiedProperties();
        }

        // Only the cheap checks; the per-vertex ones stay in the build.
        private void DrawMeshWarnings(ElbowFixer fixer, GameObject avatarRoot)
        {
            var (meshes, skipped) = TargetMeshes.Select(avatarRoot, fixer);
            var problems = skipped.Concat(
                meshes.Select(mesh => (Mesh: mesh, Problem: MeshWeightPlan.FindMeshProblem(mesh)))
            );

            foreach (var (mesh, problem) in problems)
            {
                // The VRChat SDK's upload panel already reports Read/Write, with a button that fixes it.
                if (problem == null || problem == MeshWeightPlan.MeshNotReadable)
                {
                    continue;
                }

                // The build's MissingMesh text blames other tools, which only fits the build copy.
                var key =
                    problem == MeshWeightPlan.MissingMesh ? "messages.NoMesh" : "errors." + problem;
                EditorGUILayout.HelpBox(Text(key, mesh.name), MessageType.Warning);
            }

            DrawSkippedMeshes(fixer);
        }

        // The meshes share one reason, so one line above the folded list gives it.
        private void DrawSkippedMeshes(ElbowFixer fixer)
        {
            var skipped = _skippedMeshes
                .Where(item =>
                    item.Mesh != null
                    && item.Blocker != null
                    && !fixer.targetMeshes.Contains(item.Mesh)
                )
                .ToArray();

            if (skipped.Length == 0)
            {
                return;
            }

            // A foldout's arrow sits left of the content, so one indent level lines it up with the button above.
            using (new EditorGUI.IndentLevelScope())
            {
                _isSkippedListExpanded = EditorGUILayout.Foldout(
                    _isSkippedListExpanded,
                    Text("labels.SkippedMeshes", skipped.Length),
                    true
                );
            }

            if (!_isSkippedListExpanded)
            {
                return;
            }

            // Read-only fields still ping their object when clicked, like the Script field.
            using (new EditorGUI.DisabledScope(true))
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    EditorGUILayout.LabelField(
                        Text("messages.SkippedExistingTwist"),
                        EditorStyles.wordWrappedMiniLabel
                    );
                }

                foreach (var (mesh, blocker) in skipped)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        // One indent level, like the reason above.
                        GUILayout.Space(15f);
                        EditorGUILayout.ObjectField(mesh, typeof(SkinnedMeshRenderer), true);
                        EditorGUILayout.ObjectField(blocker, typeof(Transform), true);
                    }
                }
            }
        }
    }
}
