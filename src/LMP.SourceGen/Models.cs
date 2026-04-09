using System.Collections.Immutable;

namespace LMP.SourceGen;

/// <summary>
/// Extracted metadata for an <c>[LmpSignature]</c> output type.
/// Flows through the incremental generator pipeline — all fields must
/// have correct equality semantics for Roslyn's caching.
/// </summary>
/// <remarks>
/// Both valid and invalid (non-partial-record) types flow through the pipeline.
/// Use <see cref="IsPartialRecord"/> to distinguish: valid types get code generation,
/// invalid types get LMP003 diagnostics.
/// </remarks>
internal sealed record OutputTypeModel(
    string Namespace,
    string TypeName,
    string Instructions,
    EquatableArray<InputFieldModel> InputFields,
    EquatableArray<OutputFieldModel> OutputFields,
    bool IsPartialRecord,
    string? TypeKindDescription,
    LocationInfo Location,
    bool HasNonSerializableProperty = false)
{
    /// <summary>
    /// Simple name of the input type (e.g. "TicketInput").
    /// Null until Predictor&lt;TInput, TOutput&gt; discovery resolves the input type.
    /// </summary>
    public string? InputTypeName { get; init; }

    /// <summary>
    /// Fully qualified name of the input type (e.g. "global::Demo.TicketInput").
    /// Null until Predictor&lt;TInput, TOutput&gt; discovery resolves the input type.
    /// </summary>
    public string? InputTypeFullyQualifiedName { get; init; }
}

/// <summary>
/// Metadata for a single property on an <c>[LmpSignature]</c> output type.
/// </summary>
internal sealed record OutputFieldModel(
    string Name,
    string ClrTypeName,
    string FullyQualifiedTypeName,
    string? Description,
    bool IsRequired,
    bool IsNonSerializable,
    LocationInfo Location);

/// <summary>
/// Metadata for a single field of a <c>TInput</c> type.
/// Descriptions follow priority: XML doc → [Description] on ctor param → [Description] on property.
/// </summary>
internal sealed record InputFieldModel(
    string Name,
    string ClrTypeName,
    string? Description);

/// <summary>
/// Extracted metadata for an <c>LmpModule</c> subclass.
/// Flows through the incremental generator pipeline for <c>GetPredictors()</c> emission.
/// </summary>
internal sealed record ModuleModel(
    string Namespace,
    string TypeName,
    EquatableArray<PredictorFieldModel> PredictorFields);

/// <summary>
/// Metadata for a single <c>Predictor&lt;,&gt;</c> field on an <c>LmpModule</c> subclass.
/// </summary>
internal sealed record PredictorFieldModel(
    string FieldName,
    string InputTypeFQN,
    string OutputTypeFQN);
