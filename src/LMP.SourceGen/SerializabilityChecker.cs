using Microsoft.CodeAnalysis;

namespace LMP.SourceGen;

/// <summary>
/// Checks whether a type is serializable by System.Text.Json.
/// Used by LMP002 to detect non-serializable output type properties at build time.
/// </summary>
internal static class SerializabilityChecker
{
    /// <summary>
    /// Returns <c>true</c> if the given type symbol represents a type that
    /// System.Text.Json cannot serialize/deserialize.
    /// </summary>
    public static bool IsNonSerializable(ITypeSymbol type)
    {
        // Unwrap nullable value types (e.g., int? → int)
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
            type = nullable.TypeArguments[0];

        // Check special types first (these are always OK)
        if (IsPrimitiveOrWellKnown(type))
            return false;

        // Array element check
        if (type is IArrayTypeSymbol arrayType)
            return IsNonSerializable(arrayType.ElementType);

        // Blocklist by fully qualified name
        var fqn = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        if (IsBlocklisted(fqn))
            return true;

        // Check if it's a delegate type
        if (type.TypeKind == TypeKind.Delegate)
            return true;

        // Check if it's a pointer or function pointer
        if (type.TypeKind == TypeKind.Pointer || type.TypeKind == TypeKind.FunctionPointer)
            return true;

        // Check if the base type chain includes System.Delegate or System.MulticastDelegate
        if (InheritsFrom(type, "System.Delegate"))
            return true;

        // Check if it's a Span-like ref struct
        if (type.IsRefLikeType)
            return true;

        // Check open generic delegate-like types (Action<>, Func<>, etc.)
        if (type is INamedTypeSymbol named && named.IsGenericType)
        {
            var unbound = named.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            if (IsBlocklistedGenericDefinition(unbound))
                return true;
        }

        return false;
    }

    private static bool IsPrimitiveOrWellKnown(ITypeSymbol type)
    {
        return type.SpecialType switch
        {
            SpecialType.System_Boolean => true,
            SpecialType.System_Byte => true,
            SpecialType.System_SByte => true,
            SpecialType.System_Int16 => true,
            SpecialType.System_UInt16 => true,
            SpecialType.System_Int32 => true,
            SpecialType.System_UInt32 => true,
            SpecialType.System_Int64 => true,
            SpecialType.System_UInt64 => true,
            SpecialType.System_Single => true,
            SpecialType.System_Double => true,
            SpecialType.System_Decimal => true,
            SpecialType.System_Char => true,
            SpecialType.System_String => true,
            SpecialType.System_Object => true,
            SpecialType.System_DateTime => true,
            _ => false,
        };
    }

    private static bool IsBlocklisted(string fullyQualifiedName)
    {
        return fullyQualifiedName switch
        {
            "global::System.IntPtr" or "nint" => true,
            "global::System.UIntPtr" or "nuint" => true,
            "global::System.IO.Stream" => true,
            "global::System.IO.MemoryStream" => true,
            "global::System.IO.FileStream" => true,
            "global::System.Threading.Tasks.Task" => true,
            "global::System.Threading.Tasks.ValueTask" => true,
            "global::System.Threading.CancellationToken" => true,
            "global::System.Type" => true,
            "global::System.Reflection.MethodInfo" => true,
            "global::System.Reflection.MemberInfo" => true,
            _ => false,
        };
    }

    private static bool IsBlocklistedGenericDefinition(string unboundFqn)
    {
        return unboundFqn switch
        {
            "global::System.Action<T>" => true,
            "global::System.Action<T1, T2>" => true,
            "global::System.Action<T1, T2, T3>" => true,
            "global::System.Action<T1, T2, T3, T4>" => true,
            "global::System.Func<TResult>" => true,
            "global::System.Func<T, TResult>" => true,
            "global::System.Func<T1, T2, TResult>" => true,
            "global::System.Func<T1, T2, T3, TResult>" => true,
            "global::System.Func<T1, T2, T3, T4, TResult>" => true,
            "global::System.Linq.Expressions.Expression<TDelegate>" => true,
            "global::System.Span<T>" => true,
            "global::System.ReadOnlySpan<T>" => true,
            "global::System.Memory<T>" => true,
            "global::System.ReadOnlyMemory<T>" => true,
            "global::System.Threading.Tasks.Task<TResult>" => true,
            "global::System.Threading.Tasks.ValueTask<TResult>" => true,
            _ => false,
        };
    }

    private static bool InheritsFrom(ITypeSymbol type, string baseTypeFqn)
    {
        var current = type.BaseType;
        while (current is not null)
        {
            if (current.ToDisplayString() == baseTypeFqn)
                return true;
            current = current.BaseType;
        }
        return false;
    }
}
