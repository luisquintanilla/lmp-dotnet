using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace LMP.SourceGen;

/// <summary>
/// Extracts <see cref="PromptBuilderModel"/> from <c>Predictor&lt;TIn, TOut&gt;</c>
/// generic type usages. The generator emits PromptBuilders for each valid
/// (TInput, TOutput) pair where TOutput has <c>[LmpSignature]</c>.
/// </summary>
internal static class PredictorPairExtractor
{
    private const string LmpSignatureAttributeFqn = "LMP.LmpSignatureAttribute";

    /// <summary>
    /// Attempts to extract a <see cref="PromptBuilderModel"/> from a
    /// <c>GenericNameSyntax</c> node that may reference <c>Predictor&lt;TIn, TOut&gt;</c>.
    /// Returns null if the node doesn't represent a valid predictor usage with
    /// an <c>[LmpSignature]</c>-annotated output type.
    /// </summary>
    public static PromptBuilderModel? Extract(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (ctx.Node is not GenericNameSyntax)
            return null;

        var namedType = ResolveNamedType(ctx, ct);
        if (namedType is null || !namedType.IsGenericType || namedType.TypeArguments.Length != 2)
            return null;

        if (!IsPredictorType(namedType))
            return null;

        var (inputType, outputType) = GetPredictorTypeArguments(namedType);
        if (inputType is null || outputType is null)
            return null;

        // Check TOutput has [LmpSignature]
        var lmpAttr = outputType.GetAttributes().FirstOrDefault(a =>
            a.AttributeClass?.ToDisplayString() == LmpSignatureAttributeFqn);
        if (lmpAttr is null)
            return null;

        // Check TOutput is a partial record (code gen only applies to valid types)
        if (!IsPartialRecord(outputType))
            return null;

        var instructions = lmpAttr.ConstructorArguments.FirstOrDefault().Value as string ?? "";

        var inputFields = ModelExtractor.ExtractInputFields(inputType, ct);
        var outputFields = ModelExtractor.ExtractOutputFields(outputType, ct);

        var ns = outputType.ContainingNamespace.IsGlobalNamespace
            ? ""
            : outputType.ContainingNamespace.ToDisplayString();

        var inputTypeFqn = inputType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var outputTypeFqn = outputType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        // Use minimally qualified name relative to the output type's namespace
        var inputTypeName = inputType.ContainingNamespace.Equals(
            outputType.ContainingNamespace, SymbolEqualityComparer.Default)
            ? inputType.Name
            : inputType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

        return new PromptBuilderModel(
            Namespace: ns,
            OutputTypeName: outputType.Name,
            InputTypeName: inputTypeName,
            InputTypeFullyQualified: inputTypeFqn,
            OutputTypeFullyQualified: outputTypeFqn,
            Instructions: instructions,
            InputFields: inputFields,
            OutputFields: outputFields);
    }

    private static INamedTypeSymbol? ResolveNamedType(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        // Try GetTypeInfo first (works for type references in declarations)
        var typeInfo = ctx.SemanticModel.GetTypeInfo(ctx.Node, ct);
        if (typeInfo.Type is INamedTypeSymbol nt)
            return nt;

        // If the GenericNameSyntax is part of a QualifiedNameSyntax, try the parent
        if (ctx.Node.Parent is QualifiedNameSyntax qns)
        {
            typeInfo = ctx.SemanticModel.GetTypeInfo(qns, ct);
            if (typeInfo.Type is INamedTypeSymbol nt2)
                return nt2;
        }

        // Fallback: try GetSymbolInfo
        var symbolInfo = ctx.SemanticModel.GetSymbolInfo(ctx.Node, ct);
        return symbolInfo.Symbol as INamedTypeSymbol ??
               symbolInfo.CandidateSymbols.OfType<INamedTypeSymbol>().FirstOrDefault();
    }

    /// <summary>
    /// Checks if a type is or derives from <c>LMP.Predictor&lt;,&gt;</c>.
    /// </summary>
    internal static bool IsPredictorType(INamedTypeSymbol type)
    {
        var current = type;
        while (current is not null)
        {
            if (current.IsGenericType &&
                current.OriginalDefinition is { } orig &&
                orig.ContainingNamespace?.ToDisplayString() == "LMP" &&
                orig.Name == "Predictor" &&
                orig.TypeParameters.Length == 2)
            {
                return true;
            }
            current = current.BaseType;
        }
        return false;
    }

    /// <summary>
    /// Gets the TInput and TOutput from the Predictor base type arguments.
    /// Handles derived types by walking to the Predictor&lt;,&gt; base.
    /// </summary>
    private static (INamedTypeSymbol? Input, INamedTypeSymbol? Output) GetPredictorTypeArguments(
        INamedTypeSymbol type)
    {
        var current = type;
        while (current is not null)
        {
            if (current.IsGenericType &&
                current.OriginalDefinition is { } orig &&
                orig.ContainingNamespace?.ToDisplayString() == "LMP" &&
                orig.Name == "Predictor" &&
                orig.TypeParameters.Length == 2)
            {
                return (
                    current.TypeArguments[0] as INamedTypeSymbol,
                    current.TypeArguments[1] as INamedTypeSymbol);
            }
            current = current.BaseType;
        }
        return (null, null);
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
