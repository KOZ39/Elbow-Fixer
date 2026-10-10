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

        private static void Resolve(BuildContext ctx)
        {
            var avatarRoot = ctx.AvatarRootObject;
            var (primaryFixer, fixers) = ElbowFixerUtility.FindFixers(avatarRoot);

            if (primaryFixer != null)
            {
                var state = ctx.GetState<ElbowFixerState>();

                // Build runs after the fixer is destroyed, but its errors can still link to the original by path.
                state.Fixer = ObjectRegistry.GetReference(primaryFixer);
                ReportErrors(() =>
                    state.Meshes = ElbowFixerBuilder.SelectTargetMeshes(avatarRoot, primaryFixer)
                );
            }

            foreach (var fixer in fixers)
            {
                Object.DestroyImmediate(fixer);
            }
        }

        private static void Build(BuildContext ctx)
        {
            var state = ctx.GetState<ElbowFixerState>();

            // Another tool may have deleted a mesh since Resolve.
            var meshes = state.Meshes?.Where(mesh => mesh != null).ToArray();

            if (meshes == null || meshes.Length == 0)
            {
                return;
            }

            ReportErrors(() =>
            {
                var report = ElbowFixerBuilder.Build(ctx.AvatarRootObject, state.Fixer, meshes);
                Debug.Log($"{ElbowFixerPackageInfo.LogPrefix} {report}", ctx.AvatarRootObject);
            });
        }

        private static void ReportErrors(System.Action action)
        {
            try
            {
                action();
            }
            catch (ElbowFixerException e)
            {
                // NDMF links ObjectReference arguments. Appended last, the context leaves the placeholders unchanged.
                ErrorReport.ReportError(
                    ElbowFixerLocalization.Localizer,
                    ErrorSeverity.Error,
                    e.Key,
                    e.Arguments.Append(e.Context).ToArray()
                );
            }
        }

        private class ElbowFixerState
        {
            public ObjectReference Fixer;
            public SkinnedMeshRenderer[] Meshes;
        }
    }
}
