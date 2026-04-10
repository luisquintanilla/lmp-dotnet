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
    /// extracts all <c>Predictor&lt;,&gt;</c> fields and <c>[Predict]</c> partial methods,
    /// and returns a <see cref="ModuleModel"/>.
    /// Returns null if the class is not an <c>LmpModule</c> subclass or has no
    /// predictor fields or [Predict] methods.
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
        var predictMethods = new List<PredictMethodModel>();
        var seen = new HashSet<string>();

        // Walk fields (e.g., private readonly Predictor<TIn,TOut> _classify)
        foreach (var member in typeSymbol.GetMembers().OfType<IFieldSymbol>())
        {
            ct.ThrowIfCancellationRequested();
            TryAddPredictor(member.Name, member.Type, predictorFields, seen,
                isProperty: false, isReadOnly: member.IsReadOnly);
        }

        // Walk properties (e.g., public Predictor<TIn,TOut> Classify { get; })
        // Skip properties whose backing fields were already captured.
        foreach (var member in typeSymbol.GetMembers().OfType<IPropertySymbol>())
        {
            ct.ThrowIfCancellationRequested();
            if (member.IsIndexer)
                continue;
            var propIsReadOnly = member.SetMethod is null || member.SetMethod.IsInitOnly;
            TryAddPredictor(member.Name, member.Type, predictorFields, seen,
                isProperty: true, isReadOnly: propIsReadOnly);
        }

        // Walk methods for [Predict]-decorated partial methods
        foreach (var member in typeSymbol.GetMembers().OfType<IMethodSymbol>())
        {
            ct.ThrowIfCancellationRequested();
            TryAddPredictMethod(member, predictMethods);
        }

        if (predictorFields.Count == 0 && predictMethods.Count == 0)
            return null;

        var ns = typeSymbol.ContainingNamespace.IsGlobalNamespace
            ? ""
            : typeSymbol.ContainingNamespace.ToDisplayString();

        var hasAutoOptimize = typeSymbol.GetAttributes().Any(a =>
            a.AttributeClass?.Name == "AutoOptimizeAttribute" &&
            a.AttributeClass.ContainingNamespace?.ToDisplayString() == "LMP");

        return new ModuleModel(
            Namespace: ns,
            TypeName: typeSymbol.Name,
            PredictorFields: new EquatableArray<PredictorFieldModel>(
                predictorFields.ToImmutableArray()),
            PredictMethods: new EquatableArray<PredictMethodModel>(
                predictMethods.ToImmutableArray()),
            HasAutoOptimize: hasAutoOptimize);
    }

    private static void TryAddPredictor(
        string memberName,
        ITypeSymbol memberType,
        List<PredictorFieldModel> predictorFields,
        HashSet<string> seen,
        bool isProperty,
        bool isReadOnly)
    {
        if (memberType is not INamedTypeSymbol namedType)
            return;

        if (!PredictorPairExtractor.IsPredictorType(namedType))
            return;

        var (inputType, outputType) = GetPredictorTypeArguments(namedType);
        if (inputType is null || outputType is null)
            return;

        if (!seen.Add(memberName))
            return;

        var fieldTypeFQN = namedType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var canAssignDirectly = !isReadOnly;
        string? unsafeAccessorFieldName = canAssignDirectly
            ? null
            : isProperty
                ? "<" + memberName + ">k__BackingField"
                : memberName;

        predictorFields.Add(new PredictorFieldModel(
            FieldName: memberName,
            InputTypeFQN: inputType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            OutputTypeFQN: outputType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            FieldTypeFQN: fieldTypeFQN,
            CanAssignDirectly: canAssignDirectly,
            UnsafeAccessorFieldName: unsafeAccessorFieldName));
    }

    /// <summary>
    /// Checks if a method has <c>[Predict]</c> attribute and is a valid partial method,
    /// then adds it to the predict methods list.
    /// </summary>
    private static void TryAddPredictMethod(
        IMethodSymbol method,
        List<PredictMethodModel> predictMethods)
    {
        // Must have [Predict] attribute
        if (!method.GetAttributes().Any(a =>
            a.AttributeClass?.Name == "PredictAttribute" &&
            a.AttributeClass.ContainingNamespace?.ToDisplayString() == "LMP"))
            return;

        // Must be partial
        if (!method.IsPartialDefinition)
            return;

        // Must return Task<T>
        if (method.ReturnType is not INamedTypeSymbol returnType ||
            !returnType.IsGenericType ||
            returnType.OriginalDefinition.ToDisplayString() != "System.Threading.Tasks.Task<TResult>")
            return;

        // Extract TOutput from Task<TOutput>
        var outputType = returnType.TypeArguments[0] as INamedTypeSymbol;
        if (outputType is null)
            return;

        // Must have exactly one parameter (TInput)
        if (method.Parameters.Length != 1)
            return;

        var inputType = method.Parameters[0].Type as INamedTypeSymbol;
        if (inputType is null)
            return;

        predictMethods.Add(new PredictMethodModel(
            MethodName: method.Name,
            InputTypeFQN: inputType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            OutputTypeFQN: outputType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            InputParameterName: method.Parameters[0].Name));
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
