using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace LMP.SourceGen;

/// <summary>
/// Discovers <c>PredictAsync</c> call sites on <c>Predictor&lt;TIn, TOut&gt;</c> instances
/// and extracts <see cref="InterceptorCallSiteModel"/> metadata for interceptor emission.
/// Uses the Roslyn <c>GetInterceptableLocation</c> API to produce location data.
/// </summary>
internal static class InterceptorExtractor
{
    /// <summary>
    /// Syntax predicate: returns true for invocations of the form <c>x.PredictAsync(...)</c>.
    /// </summary>
    public static bool IsCandidate(SyntaxNode node, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return node is InvocationExpressionSyntax
        {
            Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "PredictAsync" }
        };
    }

    /// <summary>
    /// Semantic transform: resolves the invocation to a <c>Predictor&lt;TIn, TOut&gt;.PredictAsync</c>
    /// call and extracts the interceptable location data.
    /// Returns null if the call site cannot be intercepted.
    /// </summary>
    public static InterceptorCallSiteModel? Extract(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (ctx.Node is not InvocationExpressionSyntax invocation)
            return null;

        // Resolve the method being called
        var symbolInfo = ctx.SemanticModel.GetSymbolInfo(invocation, ct);
        if (symbolInfo.Symbol is not IMethodSymbol method)
            return null;

        // Verify it's PredictAsync on a type deriving from Predictor<,>
        if (method.Name != "PredictAsync")
            return null;

        var containingType = method.ContainingType;
        if (containingType is null || !PredictorPairExtractor.IsPredictorType(containingType))
            return null;

        // Walk to the Predictor<TIn, TOut> base to get the concrete type arguments
        var (inputType, outputType) = GetPredictorTypeArguments(containingType);
        if (inputType is null || outputType is null)
            return null;

        // Only intercept concrete (closed) types — skip open generic call sites
        if (inputType.TypeKind == TypeKind.TypeParameter || outputType.TypeKind == TypeKind.TypeParameter)
            return null;

        // Only intercept if TOutput has [LmpSignature] (meaning a PromptBuilder exists)
        if (!outputType.GetAttributes().Any(a =>
            a.AttributeClass?.ToDisplayString() == "LMP.LmpSignatureAttribute"))
            return null;

        // Get the interceptable location from Roslyn
        var location = ctx.SemanticModel.GetInterceptableLocation(invocation, ct);
        if (location is null)
            return null;

        var ns = outputType.ContainingNamespace.IsGlobalNamespace
            ? ""
            : outputType.ContainingNamespace.ToDisplayString();

        return new InterceptorCallSiteModel(
            InputTypeFQN: inputType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            OutputTypeFQN: outputType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            OutputTypeName: outputType.Name,
            Namespace: ns,
            LocationVersion: location.Version,
            LocationData: location.Data,
            DisplayLocation: location.GetDisplayLocation());
    }

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
