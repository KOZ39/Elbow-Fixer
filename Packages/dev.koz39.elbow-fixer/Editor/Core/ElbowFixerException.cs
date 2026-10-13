using System.Linq;
using nadena.dev.ndmf;
using UnityEngine;

namespace KOZ39.ElbowFixer
{
    internal sealed class ElbowFixerException : System.InvalidOperationException
    {
        internal string Key { get; }
        internal object[] Arguments { get; }
        internal ObjectReference Context { get; }

        private ElbowFixerException(ObjectReference context, string key, object[] arguments)
            : base(key)
        {
            Key = "errors." + key;
            // NDMF would print a Unity object argument as its full hierarchy path.
            // A destroyed object still matches the type, but reading its name throws.
            Arguments = arguments
                .Select(argument =>
                    argument is Object obj ? (obj != null ? obj.name : "null") : argument
                )
                .ToArray();
            Context =
                context ?? ObjectRegistry.GetReference(arguments.OfType<Object>().FirstOrDefault());
        }

        internal static void Require(bool condition, string key, params object[] arguments) =>
            Require(condition, (ObjectReference)null, key, arguments);

        internal static void Require(
            bool condition,
            Object context,
            string key,
            params object[] arguments
        )
        {
            if (!condition)
            {
                throw new ElbowFixerException(ObjectRegistry.GetReference(context), key, arguments);
            }
        }

        internal static void Require(
            bool condition,
            ObjectReference context,
            string key,
            params object[] arguments
        )
        {
            if (!condition)
            {
                throw new ElbowFixerException(context, key, arguments);
            }
        }
    }
}
