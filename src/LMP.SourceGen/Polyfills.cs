// Polyfill: enables C# record types on netstandard2.0.
// The compiler checks for this type by name, not by assembly.

#if NETSTANDARD2_0

namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}

#endif
