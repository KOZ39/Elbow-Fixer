using UnityEditor.PackageManager;

namespace KOZ39.ElbowFixer
{
    internal static class ElbowFixerPackageInfo
    {
        internal const string Name = "dev.koz39.elbow-fixer";
        internal const string DisplayName = "Elbow Fixer";
        internal const string LogPrefix = "[" + DisplayName + "]";

        private static readonly PackageInfo _info = PackageInfo.FindForAssembly(
            typeof(ElbowFixerPackageInfo).Assembly
        );

        internal static string Version => _info?.version ?? "unknown";
    }
}
