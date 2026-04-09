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
    LocationInfo Location);

/// <summary>
/// Metadata for a single property on an <c>[LmpSignature]</c> output type.
/// </summary>
internal sealed record OutputFieldModel(
    string Name,
    string ClrTypeName,
    string FullyQualifiedTypeName,
    string? Description,
    bool IsRequired,
    LocationInfo Location);

/// <summary>
/// Metadata for a single field of a <c>TInput</c> type.
/// Descriptions follow priority: XML doc → [Description] on ctor param → [Description] on property.
/// </summary>
internal sealed record InputFieldModel(
    string Name,
    string ClrTypeName,
    string? Description);
