using System.Linq;
using nadena.dev.ndmf;
using UnityEngine;

[assembly: ExportsPlugin(typeof(global::KOZ39.ElbowFixer.ElbowFixerPlugin))]

namespace KOZ39.ElbowFixer
{
    internal class ElbowFixerPlugin : Plugin<ElbowFixerPlugin>
    {
        public override string DisplayName => ElbowFixerPackageInfo.DisplayName;
        public override string QualifiedName => ElbowFixerPackageInfo.Name;

        protected override void Configure()
        {
            // Before Modular Avatar moves objects, so the fixer used matches the inspector,
            // and before NDMF removes EditorOnly objects, so EditorOnly meshes can be reported.
            InPhase(BuildPhase.Resolving)
                .BeforePlugin("nadena.dev.modular-avatar")
                .Run($"{ElbowFixerPackageInfo.DisplayName} (Resolve)", Resolve)
                .BeforePass("nadena.dev.ndmf.system.RemoveEditorOnly");

            // Sleeves merged by Modular Avatar must already use the avatar's forearm bones,
            // and Avatar Optimizer must see the final meshes.
            InPhase(BuildPhase.Transforming)
                .AfterPlugin("nadena.dev.modular-avatar")
                .BeforePlugin("com.anatawa12.avatar-optimizer")
                .Run($"{ElbowFixerPackageInfo.DisplayName} (Build)", Build);
        }

        private static void Resolve(BuildContext context)
        {
            var avatarRoot = context.AvatarRootObject;
            var (primaryFixer, fixers) = ElbowFixerUtility.FindFixers(avatarRoot);

            if (primaryFixer != null)
            {
                var (meshes, skipped) = TargetMeshes.Select(avatarRoot, primaryFixer);
                context.GetState<ElbowFixerState>().Meshes = meshes;

                // A mesh the build cannot fix is left as is, so the upload goes on.
                foreach (var (mesh, problem) in skipped)
                {
                    Report(
                        ErrorSeverity.NonFatal,
                        "errors." + problem,
                        mesh.name,
                        ObjectRegistry.GetReference(mesh)
                    );
                }

                if (meshes.Length == 0)
                {
                    Report(
                        ErrorSeverity.NonFatal,
                        "errors.NoMeshes",
                        ObjectRegistry.GetReference(primaryFixer)
                    );
                }
            }

            foreach (var fixer in fixers)
            {
                Object.DestroyImmediate(fixer);
            }
        }

        private static void Build(BuildContext context)
        {
            // Another tool may have deleted a mesh since Resolve.
            var meshes = context
                .GetState<ElbowFixerState>()
                .Meshes?.Where(mesh => mesh != null)
                .ToArray();

            if (meshes == null || meshes.Length == 0)
            {
                return;
            }

            try
            {
                // A skipped mesh only warns; a problem with the avatar or the rig still stops the upload.
                var report = ElbowFixerBuilder.Build(
                    context.AvatarRootObject,
                    meshes,
                    e => Report(ErrorSeverity.NonFatal, e)
                );

                if (report != null)
                {
                    Debug.Log(
                        $"{ElbowFixerPackageInfo.LogPrefix} {report}",
                        context.AvatarRootObject
                    );
                }
            }
            catch (ElbowFixerException e)
            {
                Report(ErrorSeverity.Error, e);
            }
        }

        // NDMF links ObjectReference arguments; appended last, they leave the placeholders unchanged.
        private static void Report(ErrorSeverity severity, string key, params object[] arguments) =>
            ErrorReport.ReportError(ElbowFixerLocalization.Localizer, severity, key, arguments);

        private static void Report(ErrorSeverity severity, ElbowFixerException e) =>
            Report(severity, e.Key, e.Arguments.Append(e.Context).ToArray());

        private class ElbowFixerState
        {
            public SkinnedMeshRenderer[] Meshes;
        }
    }
}
