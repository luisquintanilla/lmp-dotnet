namespace LMP.SourceGen;

/// <summary>
/// Combined model for PromptBuilder emission. Contains metadata from both
/// the <c>[LmpSignature]</c> output type and the paired TInput type,
/// discovered from <c>Predictor&lt;TIn, TOut&gt;</c> declarations.
/// </summary>
internal sealed record PromptBuilderModel(
    string Namespace,
    string OutputTypeName,
    string InputTypeName,
    string InputTypeFullyQualified,
    string OutputTypeFullyQualified,
    string Instructions,
    EquatableArray<InputFieldModel> InputFields,
    EquatableArray<OutputFieldModel> OutputFields);
