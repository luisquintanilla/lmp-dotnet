namespace LMP.SourceGen;

/// <summary>
/// Extracted metadata for a <c>ChainOfThought&lt;TIn, TOut&gt;</c> usage.
/// Contains the TOutput type information needed to emit the extended
/// <c>{TypeName}WithReasoning</c> record and its <c>JsonSerializerContext</c>.
/// </summary>
internal sealed record ChainOfThoughtModel(
    string Namespace,
    string OutputTypeName,
    EquatableArray<OutputFieldModel> OutputFields);
