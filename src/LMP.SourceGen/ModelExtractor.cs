using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace LMP.SourceGen;

/// <summary>
/// Extracts <see cref="OutputTypeModel"/> from <c>[LmpSignature]</c>-annotated types.
/// Also validates types for LMP003 (non-partial record).
/// </summary>
internal static class ModelExtractor
{
    private const string LmpSignatureAttributeFqn = "LMP.LmpSignatureAttribute";
    private const string DescriptionAttributeFqn = "System.ComponentModel.DescriptionAttribute";

    /// <summary>
    /// Main extraction entry point called from the incremental pipeline transform.
    /// Always returns a model if the attribute is present — use <see cref="OutputTypeModel.IsPartialRecord"/>
    /// to determine validity for code generation vs LMP003 diagnostic reporting.
    /// </summary>
    public static OutputTypeModel? Extract(
        GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (ctx.TargetSymbol is not INamedTypeSymbol typeSymbol)
            return null;

        var attr = FindLmpSignatureAttribute(ctx.Attributes);
        if (attr is null)
            return null;

        var targetNode = ctx.TargetNode;
        bool isRecord = targetNode is RecordDeclarationSyntax;
        bool isPartial = targetNode is TypeDeclarationSyntax tds &&
            tds.Modifiers.Any(m => m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PartialKeyword));
        bool isPartialRecord = isRecord && isPartial;

        string? typeKindDescription = null;
        if (!isPartialRecord)
        {
            typeKindDescription = !isRecord ? "a class" : "a non-partial record";
        }

        var instructions = attr.ConstructorArguments.FirstOrDefault().Value as string ?? "";

        var outputFields = isPartialRecord
            ? ExtractOutputFields(typeSymbol, ct)
            : default;

        var ns = typeSymbol.ContainingNamespace.IsGlobalNamespace
            ? ""
            : typeSymbol.ContainingNamespace.ToDisplayString();

        return new OutputTypeModel(
            Namespace: ns,
            TypeName: typeSymbol.Name,
            Instructions: instructions,
            InputFields: default, // populated when Predictor<TIn, TOut> is resolved
            OutputFields: outputFields,
            IsPartialRecord: isPartialRecord,
            TypeKindDescription: typeKindDescription,
            Location: LocationInfo.From(targetNode.GetLocation()));
    }

    /// <summary>
    /// Extracts output field metadata from an <c>[LmpSignature]</c> output type's properties.
    /// </summary>
    private static EquatableArray<OutputFieldModel> ExtractOutputFields(
        INamedTypeSymbol typeSymbol, CancellationToken ct)
    {
        var builder = ImmutableArray.CreateBuilder<OutputFieldModel>();

        foreach (var member in typeSymbol.GetMembers().OfType<IPropertySymbol>())
        {
            ct.ThrowIfCancellationRequested();

            if (member.IsStatic || member.DeclaredAccessibility != Accessibility.Public)
                continue;

            // Skip compiler-generated properties (e.g., EqualityContract)
            if (member.IsImplicitlyDeclared)
                continue;

            var description = GetDescriptionFromAttribute(member);
            var clrType = member.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            var fqnType = member.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var location = LocationInfo.From(
                member.Locations.FirstOrDefault() ?? Location.None);

            builder.Add(new OutputFieldModel(
                Name: member.Name,
                ClrTypeName: clrType,
                FullyQualifiedTypeName: fqnType,
                Description: description,
                IsRequired: member.IsRequired,
                Location: location));
        }

        return new EquatableArray<OutputFieldModel>(builder.ToImmutable());
    }

    /// <summary>
    /// Extracts input field metadata from a <c>TInput</c> type symbol.
    /// Reads descriptions with priority: XML doc param → [Description] on ctor param → [Description] on property.
    /// </summary>
    public static EquatableArray<InputFieldModel> ExtractInputFields(
        INamedTypeSymbol inputTypeSymbol, CancellationToken ct)
    {
        var builder = ImmutableArray.CreateBuilder<InputFieldModel>();
        var xmlParamDescriptions = ParseXmlDocParamDescriptions(inputTypeSymbol);

        // Find the primary constructor (for records, this is the implicit one)
        var primaryCtor = inputTypeSymbol.Constructors
            .FirstOrDefault(c => !c.IsImplicitlyDeclared && c.Parameters.Length > 0)
            ?? inputTypeSymbol.Constructors
                .FirstOrDefault(c => c.Parameters.Length > 0);

        foreach (var member in inputTypeSymbol.GetMembers().OfType<IPropertySymbol>())
        {
            ct.ThrowIfCancellationRequested();

            if (member.IsStatic || member.DeclaredAccessibility != Accessibility.Public)
                continue;

            if (member.IsImplicitlyDeclared)
                continue;

            // Priority 1: XML doc <param>
            string? description = null;
            if (xmlParamDescriptions.TryGetValue(member.Name, out var xmlDesc))
                description = xmlDesc;

            // Priority 2: [Description] on constructor parameter
            if (description is null && primaryCtor is not null)
            {
                var param = primaryCtor.Parameters
                    .FirstOrDefault(p => string.Equals(p.Name, member.Name, StringComparison.OrdinalIgnoreCase));
                if (param is not null)
                    description = GetDescriptionFromAttribute(param);
            }

            // Priority 3: [Description] on property
            if (description is null)
                description = GetDescriptionFromAttribute(member);

            var clrType = member.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

            builder.Add(new InputFieldModel(
                Name: member.Name,
                ClrTypeName: clrType,
                Description: description));
        }

        return new EquatableArray<InputFieldModel>(builder.ToImmutable());
    }

    /// <summary>
    /// Reads <c>[Description("...")]</c> from a symbol's attributes.
    /// </summary>
    internal static string? GetDescriptionFromAttribute(ISymbol symbol)
    {
        var descAttr = symbol.GetAttributes().FirstOrDefault(a =>
            a.AttributeClass?.ToDisplayString() == DescriptionAttributeFqn);
        return descAttr?.ConstructorArguments.FirstOrDefault().Value as string;
    }

    /// <summary>
    /// Parses XML doc comments for <c>&lt;param name="X"&gt;...&lt;/param&gt;</c> elements.
    /// </summary>
    private static System.Collections.Generic.Dictionary<string, string> ParseXmlDocParamDescriptions(
        INamedTypeSymbol typeSymbol)
    {
        var result = new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal);
        var xml = typeSymbol.GetDocumentationCommentXml();

        if (string.IsNullOrEmpty(xml))
            return result;

        try
        {
            var doc = XDocument.Parse(xml);
            var paramElements = doc.Descendants("param");
            foreach (var param in paramElements)
            {
                var name = param.Attribute("name")?.Value;
                var value = param.Value?.Trim();
                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(value))
                    result[name!] = value!;
            }
        }
        catch (System.Xml.XmlException)
        {
            // Malformed XML docs — silently skip
        }

        return result;
    }

    private static AttributeData? FindLmpSignatureAttribute(
        ImmutableArray<AttributeData> attributes)
    {
        foreach (var attr in attributes)
        {
            if (attr.AttributeClass?.ToDisplayString() == LmpSignatureAttributeFqn)
                return attr;
        }
        return null;
    }
}
