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

    /// <summary>
    /// LMP030: Warning — <c>[Skill]</c> on a non-public method.
    /// The method will not appear in <c>GetSkills()</c> because it is not accessible.
    /// </summary>
    public static readonly DiagnosticDescriptor SkillMethodNotPublic = new(
        id: "LMP030",
        title: "[Skill] on non-public method",
        messageFormat: "[Skill] on '{0}' will not be included in GetSkills() — the method must be public",
        category: "LMP.Skills",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        helpLinkUri: "https://github.com/lmp-dotnet/lmp/blob/main/docs/diagnostics.md#lmp030");

    /// <summary>
    /// LMP020: Warning — <c>[Tool]</c> on a non-async method.
    /// Tool methods should be async (return <c>Task</c> or <c>Task&lt;T&gt;</c>).
    /// </summary>
    public static readonly DiagnosticDescriptor ToolMethodNotAsync = new(
        id: "LMP020",
        title: "[Tool] on non-async method",
        messageFormat: "Method '{0}' decorated with [Tool] should be async (return Task or Task<T>)",
        category: "LMP.Tools",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        helpLinkUri: "https://github.com/lmp-dotnet/lmp/blob/main/docs/diagnostics.md#lmp020");

    /// <summary>
    /// LMP021: Error — <c>[Tool]</c> on a static method.
    /// Tool methods must be instance methods so the source generator can emit <c>this.{MethodName}</c>.
    /// </summary>
    public static readonly DiagnosticDescriptor ToolMethodStatic = new(
        id: "LMP021",
        title: "[Tool] on static method",
        messageFormat: "Method '{0}' decorated with [Tool] must be an instance method",
        category: "LMP.Tools",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: "https://github.com/lmp-dotnet/lmp/blob/main/docs/diagnostics.md#lmp021");

    /// <summary>
    /// LMP023: Warning — duplicate tool name across two <c>[Tool]</c>-annotated methods.
    /// </summary>
    public static readonly DiagnosticDescriptor DuplicateToolName = new(
        id: "LMP023",
        title: "Duplicate tool name",
        messageFormat: "Duplicate tool name '{0}': method '{1}' shares a name with another [Tool]-annotated method",
        category: "LMP.Tools",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        helpLinkUri: "https://github.com/lmp-dotnet/lmp/blob/main/docs/diagnostics.md#lmp023");

    /// <summary>
    /// LMP025: Warning — <c>[Tool]</c> method has more than 10 parameters.
    /// Large parameter counts reduce LLM tool-call accuracy; consider splitting the method.
    /// </summary>
    public static readonly DiagnosticDescriptor ToolMethodTooManyParams = new(
        id: "LMP025",
        title: "[Tool] method has too many parameters",
        messageFormat: "Method '{0}' decorated with [Tool] has {1} parameters; consider reducing to 10 or fewer for LLM compatibility",
        category: "LMP.Tools",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        helpLinkUri: "https://github.com/lmp-dotnet/lmp/blob/main/docs/diagnostics.md#lmp025");
}
