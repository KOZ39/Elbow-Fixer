using System;
using static KOZ39.ElbowFixer.ElbowFixerLocalization;

namespace KOZ39.ElbowFixer
{
    internal sealed class ElbowFixerException : InvalidOperationException
    {
        internal ElbowFixerException(string key, params object[] arguments)
            : base(Text("errors." + key, arguments)) { }

        internal static void Require(bool condition, string key, params object[] arguments)
        {
            if (!condition)
            {
                throw new ElbowFixerException(key, arguments);
            }
        }
    }
}
