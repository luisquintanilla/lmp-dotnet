using Microsoft.Extensions.AI;

namespace LMP.Samples.AutoOptimize;

/// <summary>
/// Simple Q&amp;A module demonstrating [AutoOptimize].
/// When a Generated/QAModule.Optimized.g.cs exists, the source gen's
/// partial void ApplyOptimizedState() hook loads the optimized state
/// (instructions + demos) into each predictor on first use.
/// </summary>
[AutoOptimize(TrainSet = "data/train.jsonl", DevSet = "data/dev.jsonl")]
public partial class QAModule : LmpModule<QAInput, QAOutput>
{
    private readonly Predictor<QAInput, QAOutput> _qa;

    public QAModule(IChatClient client)
    {
        _qa = new Predictor<QAInput, QAOutput>(client) { Name = "qa" };
    }

    public override async Task<QAOutput> ForwardAsync(
        QAInput input, CancellationToken cancellationToken = default)
    {
        return await _qa.PredictAsync(input, trace: Trace, cancellationToken: cancellationToken);
    }
}
