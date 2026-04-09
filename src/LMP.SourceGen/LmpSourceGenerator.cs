using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace LMP.SourceGen;

/// <summary>
/// LMP incremental source generator. Discovers <c>[LmpSignature]</c> output types
/// and <c>LmpModule</c> subclasses at compile time to emit prompt builders,
/// JSON contexts, and predictor discovery methods.
/// </summary>
[Generator]
public sealed class LmpSourceGenerator : IIncrementalGenerator
{
    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Pipeline 1: [LmpSignature]-annotated types → model extraction + LMP003 diagnostics
        var allTargets = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                "LMP.LmpSignatureAttribute",
                predicate: static (node, _) => node is TypeDeclarationSyntax,
                transform: static (ctx, ct) => ModelExtractor.Extract(ctx, ct))
            .Where(static m => m is not null)
            .Select(static (m, _) => m!);

        // Report LMP003 diagnostics for non-partial-record types
        context.RegisterSourceOutput(
            allTargets.Where(static m => !m.IsPartialRecord),
            static (spc, model) =>
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    LmpDiagnostics.NonPartialRecord,
                    model.Location.ToLocation(),
                    model.TypeName,
                    model.TypeKindDescription));
            });

        // Valid output type models for code generation (Phase 2.3+)
        var outputModels = allTargets.Where(static m => m.IsPartialRecord);

        // Code emission (PromptBuilder, JsonContext) will be registered here in Phase 2.3+
        _ = outputModels;
    }
}
