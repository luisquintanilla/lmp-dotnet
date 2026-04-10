using Microsoft.CodeAnalysis;

namespace LMP.SourceGen;

/// <summary>
/// Roslyn diagnostic descriptors for LMP source generator diagnostics.
/// </summary>
internal static class LmpDiagnostics
{
    /// <summary>
    /// LMP001: Warning — Output type property missing <c>[Description]</c> attribute.
    /// </summary>
    public static readonly DiagnosticDescriptor MissingDescription = new(
        id: "LMP001",
        title: "Missing property description",
        messageFormat: "Property '{0}' on output type '{1}' is missing a [Description] attribute",
        category: "LMP.Authoring",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        helpLinkUri: "https://github.com/lmp-dotnet/lmp/blob/main/docs/diagnostics.md#lmp001");

    /// <summary>
    /// LMP002: Error — Output type property is not serializable by System.Text.Json.
    /// </summary>
    public static readonly DiagnosticDescriptor NonSerializableOutput = new(
        id: "LMP002",
        title: "Non-serializable output type",
        messageFormat: "Property '{0}' on output type '{1}' is not serializable by System.Text.Json",
        category: "LMP.Authoring",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: "https://github.com/lmp-dotnet/lmp/blob/main/docs/diagnostics.md#lmp002");

    /// <summary>
    /// LMP003: Error — <c>[LmpSignature]</c> applied to a type that is not a <c>partial record</c>.
    /// </summary>
    public static readonly DiagnosticDescriptor NonPartialRecord = new(
        id: "LMP003",
        title: "[LmpSignature] on non-partial record",
        messageFormat: "[LmpSignature] requires a partial record but '{0}' is {1}",
        category: "LMP.Authoring",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: "https://github.com/lmp-dotnet/lmp/blob/main/docs/diagnostics.md#lmp003");

    /// <summary>
    /// LMP004: Warning — <c>[AutoOptimize]</c> on a module with no predictor fields.
    /// The attribute will have no effect because there are no predictors to optimize.
    /// </summary>
    public static readonly DiagnosticDescriptor AutoOptimizeNoPredictors = new(
        id: "LMP004",
        title: "[AutoOptimize] on module with no predictors",
        messageFormat: "[AutoOptimize] on '{0}' has no effect — the module has no Predictor<,> fields or [Predict] methods",
        category: "LMP.AutoOptimize",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        helpLinkUri: "https://github.com/lmp-dotnet/lmp/blob/main/docs/diagnostics.md#lmp004");
}
