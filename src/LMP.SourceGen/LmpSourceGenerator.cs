using System.Collections.Generic;
using System.Linq;
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
        // Pipeline 1: [LmpSignature]-annotated types → model extraction + diagnostics
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

        // For valid partial records, report LMP001 (missing description) and LMP002 (non-serializable)
        var partialRecords = allTargets.Where(static m => m.IsPartialRecord);

        context.RegisterSourceOutput(
            partialRecords,
            static (spc, model) =>
            {
                ReportFieldDiagnostics(spc, model);
            });

        // Valid output type models without LMP002 errors — used for JsonContext emission
        var outputModels = partialRecords.Where(static m => !m.HasNonSerializableProperty);

        context.RegisterSourceOutput(
            outputModels,
            static (spc, model) =>
            {
                JsonContextEmitter.Emit(spc, model);
            });

        // Pipeline 2: Predictor<TIn, TOut> usages → PromptBuilder emission
        // Scans for GenericNameSyntax nodes matching Predictor<,>, resolves the
        // type pair, and emits a PromptBuilder for each valid (TInput, TOutput) pairing
        // where TOutput has [LmpSignature].
        var predictorPairs = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) =>
                    node is GenericNameSyntax gns &&
                    gns.Identifier.Text == "Predictor" &&
                    gns.TypeArgumentList.Arguments.Count == 2,
                transform: static (ctx, ct) => PredictorPairExtractor.Extract(ctx, ct))
            .Where(static m => m is not null)
            .Select(static (m, _) => m!)
            .Collect()
            .SelectMany(static (pairs, _) => DeduplicateByOutputType(pairs));

        context.RegisterSourceOutput(predictorPairs, static (spc, model) =>
            PromptBuilderEmitter.Emit(spc, model));

        // Pipeline 3: LmpModule subclasses → GetPredictors() emission
        // Scans for partial class declarations that derive from LmpModule,
        // extracts Predictor<,> fields, and emits the GetPredictors() override.
        var moduleModels = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, ct) => ModuleExtractor.IsCandidate(node, ct),
                transform: static (ctx, ct) => ModuleExtractor.Extract(ctx, ct))
            .Where(static m => m is not null)
            .Select(static (m, _) => m!);

        context.RegisterSourceOutput(moduleModels, static (spc, model) =>
        {
            ModuleEmitter.Emit(spc, model);
            ModuleJsonContextEmitter.Emit(spc, model);
        });
    }

    /// <summary>
    /// Reports LMP001 and LMP002 diagnostics for output field issues.
    /// </summary>
    private static void ReportFieldDiagnostics(SourceProductionContext spc, OutputTypeModel model)
    {
        foreach (var field in model.OutputFields)
        {
            // LMP001: Missing [Description] attribute
            if (string.IsNullOrEmpty(field.Description))
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    LmpDiagnostics.MissingDescription,
                    field.Location.ToLocation(),
                    field.Name,
                    model.TypeName));
            }

            // LMP002: Non-serializable property type
            if (field.IsNonSerializable)
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    LmpDiagnostics.NonSerializableOutput,
                    field.Location.ToLocation(),
                    field.Name,
                    model.TypeName));
            }
        }
    }

    private static IEnumerable<PromptBuilderModel> DeduplicateByOutputType(
        System.Collections.Immutable.ImmutableArray<PromptBuilderModel> pairs)
    {
        var seen = new HashSet<string>();
        foreach (var pair in pairs
            .OrderBy(p => p.OutputTypeFullyQualified)
            .ThenBy(p => p.InputTypeFullyQualified))
        {
            if (seen.Add(pair.OutputTypeFullyQualified))
                yield return pair;
        }
    }
}
