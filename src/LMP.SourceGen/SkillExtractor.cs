using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace LMP.SourceGen;

/// <summary>
/// Discovers <c>[Skill]</c>-annotated methods on <c>LmpModule</c> subclasses.
/// Feeds Pipeline 8 (<c>GetSkills()</c> emission).
/// </summary>
internal static class SkillExtractor
{
    private const string SkillAttributeName = "SkillAttribute";
    private const string SkillAttributeNamespace = "LMP";

    /// <summary>
    /// Syntax predicate: same as <see cref="ModuleExtractor.IsCandidate"/> — partial class
    /// with a base list (potential <c>LmpModule</c> subclass).
    /// </summary>
    public static bool IsCandidate(SyntaxNode node, CancellationToken ct)
        => ModuleExtractor.IsCandidate(node, ct);

    /// <summary>
    /// Semantic transform: validates the class derives from <c>LmpModule</c>,
    /// extracts all <c>[Skill]</c>-annotated methods (public and non-public),
    /// and returns a <see cref="SkillModuleModel"/>.
    /// Returns null if the class is not an <c>LmpModule</c> subclass or has no
    /// <c>[Skill]</c> methods.
    /// </summary>
    public static SkillModuleModel? Extract(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (ctx.Node is not ClassDeclarationSyntax)
            return null;

        var typeSymbol = ctx.SemanticModel.GetDeclaredSymbol(ctx.Node, ct) as INamedTypeSymbol;
        if (typeSymbol is null || !DerivesFromLmpModule(typeSymbol))
            return null;

        var skillMethods = new List<SkillMethodModel>();

        foreach (var member in typeSymbol.GetMembers().OfType<IMethodSymbol>())
        {
            ct.ThrowIfCancellationRequested();
            TryAddSkillMethod(member, skillMethods);
        }

        if (skillMethods.Count == 0)
            return null;

        var ns = typeSymbol.ContainingNamespace.IsGlobalNamespace
            ? ""
            : typeSymbol.ContainingNamespace.ToDisplayString();

        return new SkillModuleModel(
            Namespace: ns,
            TypeName: typeSymbol.Name,
            SkillMethods: new EquatableArray<SkillMethodModel>(skillMethods.ToImmutableArray()));
    }

    private static void TryAddSkillMethod(IMethodSymbol method, List<SkillMethodModel> skillMethods)
    {
        var attr = method.GetAttributes().FirstOrDefault(a =>
            a.AttributeClass?.Name == SkillAttributeName &&
            a.AttributeClass.ContainingNamespace?.ToDisplayString() == SkillAttributeNamespace);

        if (attr is null)
            return;

        // Extract [Skill] named arguments
        string? nameOverride = attr.NamedArguments
            .FirstOrDefault(kv => kv.Key == "Name").Value.Value as string;
        string? description = attr.NamedArguments
            .FirstOrDefault(kv => kv.Key == "Description").Value.Value as string;

        var location = method.Locations.Length > 0 ? method.Locations[0] : null;
        var locationInfo = location is not null
            ? LocationInfo.From(location)
            : new LocationInfo(null, default, default);

        skillMethods.Add(new SkillMethodModel(
            MethodName: method.Name,
            NameOverride: nameOverride,
            Description: description,
            IsPublic: method.DeclaredAccessibility == Accessibility.Public,
            Location: locationInfo));
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
