using System.Linq;
using UnityEngine;

namespace KOZ39.ElbowFixer
{
    internal static class TargetMeshes
    {
        // Shared by the build and the inspector.
        internal static (
            SkinnedMeshRenderer[] Meshes,
            (SkinnedMeshRenderer Mesh, string Problem)[] Skipped
        ) Select(GameObject avatarRoot, ElbowFixer fixer)
        {
            var selected = fixer
                .targetMeshes.Where(renderer => renderer != null)
                .Distinct()
                .Select(renderer =>
                    (Mesh: renderer, Problem: FindSelectionProblem(renderer, avatarRoot.transform))
                )
                .ToArray();

            return (
                selected.Where(item => item.Problem == null).Select(item => item.Mesh).ToArray(),
                selected.Where(item => item.Problem != null).ToArray()
            );
        }

        // A renderer outside the build copy would be the original scene object.
        internal static string FindSelectionProblem(
            SkinnedMeshRenderer renderer,
            Transform avatarRoot
        )
        {
            if (!ElbowFixerUtility.BelongsToAvatar(renderer.transform, avatarRoot))
            {
                return "MeshOutsideAvatar";
            }

            if (ElbowFixerUtility.IsEditorOnly(renderer.transform, avatarRoot))
            {
                return "MeshEditorOnly";
            }

            return renderer.TryGetComponent<Cloth>(out _) ? "ClothNotSupported" : null;
        }
    }
}
