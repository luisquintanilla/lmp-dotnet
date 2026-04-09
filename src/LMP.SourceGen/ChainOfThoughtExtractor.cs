using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace LMP.SourceGen;

/// <summary>
/// Discovers <c>ChainOfThought&lt;TIn, TOut&gt;</c> generic type usages in source code,
/// resolves the <c>TOut</c> type symbol, and extracts a <see cref="ChainOfThoughtModel"/>
/// for extended output type emission.
/// </summary>
internal static class ChainOfThoughtExtractor
{
    /// <summary>
    /// Syntax predicate: returns true for <c>GenericNameSyntax</c> nodes
    /// named <c>ChainOfThought</c> with exactly 2 type arguments.
    /// </summary>
    public static bool IsCandidate(SyntaxNode node, CancellationToken ct)
    {
        return node is GenericNameSyntax gns &&
               gns.Identifier.Text == "ChainOfThought" &&
               gns.TypeArgumentList.Arguments.Count == 2;
    }

    /// <summary>
    /// Semantic transform: resolves the <c>ChainOfThought&lt;TIn, TOut&gt;</c> type,
    /// validates TOut is an <c>[LmpSignature]</c> partial record, and extracts
    /// a <see cref="ChainOfThoughtModel"/> for emission.
    /// Returns null if the node is not a valid CoT usage.
    /// </summary>
    public static ChainOfThoughtModel? Extract(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (ctx.Node is not GenericNameSyntax)
            return null;

        var namedType = ResolveNamedType(ctx, ct);
        if (namedType is null || !namedType.IsGenericType || namedType.TypeArguments.Length != 2)
            return null;

        // Verify the type is or derives from ChainOfThought (which derives from Predictor)
        if (!IsChainOfThoughtType(namedType))
            return null;

        // TOut is the second type argument
        var outputType = namedType.TypeArguments[1] as INamedTypeSymbol;
        if (outputType is null)
            return null;

        // Validate TOut has [LmpSignature] and is a partial record
        var lmpAttr = outputType.GetAttributes().FirstOrDefault(a =>
            a.AttributeClass?.ToDisplayString() == "LMP.LmpSignatureAttribute");
        if (lmpAttr is null)
            return null;

        if (!IsPartialRecord(outputType))
            return null;

        // Extract output fields
        var outputFields = ModelExtractor.ExtractOutputFields(outputType, ct, out bool hasNonSerializable);
        if (hasNonSerializable)
            return null;

        var ns = outputType.ContainingNamespace.IsGlobalNamespace
            ? ""
            : outputType.ContainingNamespace.ToDisplayString();

        return new ChainOfThoughtModel(
            Namespace: ns,
            OutputTypeName: outputType.Name,
            OutputFields: outputFields);
    }

    private static INamedTypeSymbol? ResolveNamedType(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        var typeInfo = ctx.SemanticModel.GetTypeInfo(ctx.Node, ct);
        if (typeInfo.Type is INamedTypeSymbol nt)
            return nt;

        if (ctx.Node.Parent is QualifiedNameSyntax qns)
        {
            typeInfo = ctx.SemanticModel.GetTypeInfo(qns, ct);
            if (typeInfo.Type is INamedTypeSymbol nt2)
                return nt2;
        }

        var symbolInfo = ctx.SemanticModel.GetSymbolInfo(ctx.Node, ct);
        return symbolInfo.Symbol as INamedTypeSymbol ??
               symbolInfo.CandidateSymbols.OfType<INamedTypeSymbol>().FirstOrDefault();
    }

    /// <summary>
    /// Checks if a type is or derives from a type named <c>ChainOfThought</c>
    /// in the <c>LMP</c> namespace hierarchy.
    /// </summary>
    private static bool IsChainOfThoughtType(INamedTypeSymbol type)
    {
        var current = type;
        while (current is not null)
        {
            if (current.IsGenericType &&
                current.OriginalDefinition is { } orig &&
                orig.Name == "ChainOfThought" &&
                orig.TypeParameters.Length == 2 &&
                IsLmpNamespace(orig.ContainingNamespace))
            {
                return true;
            }
            current = current.BaseType;
        }
        return false;
    }

    private static bool IsLmpNamespace(INamespaceSymbol? ns)
    {
        if (ns is null) return false;
        var display = ns.ToDisplayString();
        return display == "LMP" || display.StartsWith("LMP.");
    }

    private static bool IsPartialRecord(INamedTypeSymbol typeSymbol)
    {
        if (!typeSymbol.IsRecord)
            return false;

        foreach (var syntaxRef in typeSymbol.DeclaringSyntaxReferences)
        {
            var syntax = syntaxRef.GetSyntax();
            if (syntax is RecordDeclarationSyntax rds &&
                rds.Modifiers.Any(m => m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PartialKeyword)))
            {
                return true;
            }
        }
        return false;
    }
}
