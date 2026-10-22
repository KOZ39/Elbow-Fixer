using System.Globalization;
using System.Linq;
using nadena.dev.ndmf.localization;
using UnityEditor;
using UnityEngine;

namespace KOZ39.ElbowFixer
{
    internal static class ElbowFixerLocalization
    {
        private const string Folder =
            "Packages/" + ElbowFixerPackageInfo.Name + "/Editor/Localization";

        internal static readonly Localizer Localizer = new(
            "en-US",
            () =>
                AssetDatabase
                    .FindAssets("t:LocalizationAsset", new[] { Folder })
                    .Select(guid =>
                        AssetDatabase.LoadAssetAtPath<LocalizationAsset>(
                            AssetDatabase.GUIDToAssetPath(guid)
                        )
                    )
                    .Where(asset => asset != null)
                    .ToList()
        );

        internal static string Text(string key, params object[] arguments)
        {
            var text = Localizer.GetLocalizedString(key);

            return arguments.Length == 0
                ? text
                : string.Format(CultureInfo.CurrentCulture, text, arguments);
        }
    }
}
