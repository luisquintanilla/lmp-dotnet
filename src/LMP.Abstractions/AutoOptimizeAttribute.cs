namespace LMP;

/// <summary>
/// Marks an <see cref="LmpModule"/> subclass for build-time automatic optimization.
/// When present, the source generator emits a <c>partial void ApplyOptimizedState()</c>
/// call in the module's constructor. The <c>dotnet lmp auto-optimize</c> CLI tool
/// discovers these modules and writes <c>Generated/{Module}.Optimized.g.cs</c> files
/// containing the winning instructions, demos, and config as C# literals.
/// </summary>
/// <remarks>
/// <para>
/// The attribute serves two audiences:
/// <list type="bullet">
///   <item><b>Source generator:</b> Emits the <c>partial void ApplyOptimizedState()</c>
///   declaration and call site. If no <c>.g.cs</c> implementation exists, the compiler
///   removes both (standard C# partial void rules) — zero impact.</item>
///   <item><b>CLI tool:</b> Reads <see cref="TrainSet"/>, <see cref="DevSet"/>, and
///   <see cref="BudgetSeconds"/> to configure the optimization run.</item>
/// </list>
/// </para>
/// <para>
/// The metric function is specified at optimization time via CLI arguments, not the
/// attribute, because metrics are delegates that cannot be expressed in attribute syntax.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [AutoOptimize(
///     TrainSet = "Data/train.jsonl",
///     DevSet = "Data/dev.jsonl",
///     BudgetSeconds = 120)]
/// public partial class QAModule : LmpModule
/// {
///     private readonly Predictor&lt;QAInput, QAOutput&gt; _qa;
///
///     public QAModule(IChatClient client) : base(client)
///     {
///         _qa = new Predictor&lt;QAInput, QAOutput&gt;(client);
///     }
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class AutoOptimizeAttribute : Attribute
{
    /// <summary>
    /// Path to the training dataset (JSONL format). Relative to the project directory.
    /// Used by the CLI tool to load training examples and by staleness detection
    /// to compute the dataset hash.
    /// </summary>
    public string? TrainSet { get; init; }

    /// <summary>
    /// Path to the dev/validation dataset (JSONL format). Relative to the project directory.
    /// Optional — when omitted, the optimizer uses a held-out portion of <see cref="TrainSet"/>.
    /// </summary>
    public string? DevSet { get; init; }

    /// <summary>
    /// Time budget in seconds for the optimization search. Default is 120 seconds.
    /// The optimizer stops proposing new candidates after this time elapses.
    /// </summary>
    public int BudgetSeconds { get; init; } = 120;
}
