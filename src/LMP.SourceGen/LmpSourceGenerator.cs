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

        // Valid output type models for code generation
        var outputModels = allTargets.Where(static m => m.IsPartialRecord);

        // Emit PromptBuilder for models that have input type info resolved
        context.RegisterSourceOutput(
            outputModels.Where(static m => m.InputTypeName is not null),
            static (spc, model) =>
            {
                PromptBuilderEmitter.Emit(spc, model);
            });
    }
}
