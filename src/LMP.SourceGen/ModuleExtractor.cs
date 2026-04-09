using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace LMP.SourceGen;

/// <summary>
/// Discovers <c>LmpModule</c> subclasses and extracts their <c>Predictor&lt;,&gt;</c> fields
/// for <c>GetPredictors()</c> emission.
/// </summary>
internal static class ModuleExtractor
{
    /// <summary>
    /// Syntax predicate: returns true for class declarations that have a base list
    /// (potential <c>LmpModule</c> subclasses).
    /// </summary>
    public static bool IsCandidate(SyntaxNode node, CancellationToken ct)
    {
        return node is ClassDeclarationSyntax cds &&
               cds.BaseList is not null &&
               cds.Modifiers.Any(SyntaxKind.PartialKeyword);
    }

    /// <summary>
    /// Semantic transform: validates the class derives from <c>LmpModule</c>,
    /// extracts all <c>Predictor&lt;,&gt;</c> fields, and returns a <see cref="ModuleModel"/>.
    /// Returns null if the class is not an <c>LmpModule</c> subclass or has no predictor fields.
    /// </summary>
    public static ModuleModel? Extract(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (ctx.Node is not ClassDeclarationSyntax)
            return null;

        var typeSymbol = ctx.SemanticModel.GetDeclaredSymbol(ctx.Node, ct) as INamedTypeSymbol;
        if (typeSymbol is null)
            return null;

        if (!DerivesFromLmpModule(typeSymbol))
            return null;

        var predictorFields = new List<PredictorFieldModel>();

        foreach (var member in typeSymbol.GetMembers().OfType<IFieldSymbol>())
        {
            ct.ThrowIfCancellationRequested();

            if (member.Type is not INamedTypeSymbol fieldType)
                continue;

            if (!PredictorPairExtractor.IsPredictorType(fieldType))
                continue;

            var (inputType, outputType) = GetPredictorTypeArguments(fieldType);
            if (inputType is null || outputType is null)
                continue;

            predictorFields.Add(new PredictorFieldModel(
                FieldName: member.Name,
                InputTypeFQN: inputType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                OutputTypeFQN: outputType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
        }

        if (predictorFields.Count == 0)
            return null;

        var ns = typeSymbol.ContainingNamespace.IsGlobalNamespace
            ? ""
            : typeSymbol.ContainingNamespace.ToDisplayString();

        return new ModuleModel(
            Namespace: ns,
            TypeName: typeSymbol.Name,
            PredictorFields: new EquatableArray<PredictorFieldModel>(
                predictorFields.ToImmutableArray()));
    }

    /// <summary>
    /// Checks if <paramref name="type"/> derives from <c>LMP.LmpModule</c>.
    /// </summary>
    private static bool DerivesFromLmpModule(INamedTypeSymbol type)
    {
        var current = type.BaseType;
        while (current is not null)
        {
            if (current.ContainingNamespace?.ToDisplayString() == "LMP" &&
                current.Name == "LmpModule")
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
}
