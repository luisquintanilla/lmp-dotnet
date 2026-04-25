using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace LMP.SourceGen;

/// <summary>
/// Discovers <c>[Tool]</c>-annotated methods on <c>LmpModule</c> subclasses.
/// Feeds Pipeline 7 (<c>GetTools()</c> emission).
/// </summary>
internal static class ToolExtractor
{
    private const string ToolAttributeName = "ToolAttribute";
    private const string ToolAttributeNamespace = "LMP";

    /// <summary>
    /// Syntax predicate: same as <see cref="ModuleExtractor.IsCandidate"/> — partial class
    /// with a base list (potential <c>LmpModule</c> subclass).
    /// </summary>
    public static bool IsCandidate(SyntaxNode node, CancellationToken ct)
        => ModuleExtractor.IsCandidate(node, ct);

    /// <summary>
    /// Semantic transform: validates the class derives from <c>LmpModule</c>,
    /// extracts all <c>[Tool]</c>-annotated methods (public and non-public),
    /// and returns a <see cref="ToolModuleModel"/>.
    /// Returns null if the class is not an <c>LmpModule</c> subclass or has no
    /// <c>[Tool]</c> methods.
    /// </summary>
    public static ToolModuleModel? Extract(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (ctx.Node is not ClassDeclarationSyntax)
            return null;

        var typeSymbol = ctx.SemanticModel.GetDeclaredSymbol(ctx.Node, ct) as INamedTypeSymbol;
        if (typeSymbol is null || !DerivesFromLmpModule(typeSymbol))
            return null;

        var toolMethods = new List<ToolMethodModel>();

        foreach (var member in typeSymbol.GetMembers().OfType<IMethodSymbol>())
        {
            ct.ThrowIfCancellationRequested();
            TryAddToolMethod(member, toolMethods);
        }

        if (toolMethods.Count == 0)
            return null;

        var ns = typeSymbol.ContainingNamespace.IsGlobalNamespace
            ? ""
            : typeSymbol.ContainingNamespace.ToDisplayString();

        return new ToolModuleModel(
            Namespace: ns,
            TypeName: typeSymbol.Name,
            ToolMethods: new EquatableArray<ToolMethodModel>(toolMethods.ToImmutableArray()));
    }

    private static void TryAddToolMethod(IMethodSymbol method, List<ToolMethodModel> toolMethods)
    {
        var attr = method.GetAttributes().FirstOrDefault(a =>
            a.AttributeClass?.Name == ToolAttributeName &&
            a.AttributeClass.ContainingNamespace?.ToDisplayString() == ToolAttributeNamespace);

        if (attr is null)
            return;

        string? nameOverride = attr.NamedArguments
            .FirstOrDefault(kv => kv.Key == "Name").Value.Value as string;
        string? description = attr.NamedArguments
            .FirstOrDefault(kv => kv.Key == "Description").Value.Value as string;

        var location = method.Locations.Length > 0 ? method.Locations[0] : null;
        var locationInfo = location is not null
            ? LocationInfo.From(location)
            : new LocationInfo(null, default, default);

        bool isAsync = method.IsAsync || IsTaskLike(method);

        toolMethods.Add(new ToolMethodModel(
            MethodName: method.Name,
            NameOverride: nameOverride,
            Description: description,
            IsPublic: method.DeclaredAccessibility == Accessibility.Public,
            IsAsync: isAsync,
            IsStatic: method.IsStatic,
            ParameterCount: method.Parameters.Length,
            Location: locationInfo));
    }

    private static bool IsTaskLike(IMethodSymbol method)
    {
        var typeName = method.ReturnType.Name;
        return typeName is "Task" or "ValueTask";
    }

    private static bool DerivesFromLmpModule(INamedTypeSymbol type)
    {
        var current = type.BaseType;
        while (current is not null)
        {
            if (current.ContainingNamespace?.ToDisplayString() == "LMP" &&
                current.Name == "LmpModule")
                return true;
            current = current.BaseType;
        }
        return false;
    }
}
