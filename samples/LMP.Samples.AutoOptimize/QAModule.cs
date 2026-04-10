using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

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

    /// <summary>
    /// Convention: CLI discovers this via reflection to create the IChatClient.
    /// Like EF Core's <c>IDesignTimeDbContextFactory&lt;T&gt;</c> — you own the client.
    /// Any provider (Azure, Ollama, Anthropic, etc.) works.
    /// </summary>
    public static IChatClient CreateClient()
    {
        var config = new ConfigurationBuilder()
            .AddUserSecrets<QAModule>()
            .Build();

        string endpoint = config["AzureOpenAI:Endpoint"]
            ?? throw new InvalidOperationException(
                "Run: dotnet user-secrets set AzureOpenAI:Endpoint https://YOUR.openai.azure.com/");

        string deployment = Environment.GetEnvironmentVariable("LMP_MODEL")
            ?? config["AzureOpenAI:Deployment"] ?? "gpt-4o-mini";

        return new AzureOpenAIClient(
                new Uri(endpoint), new DefaultAzureCredential())
            .GetChatClient(deployment)
            .AsIChatClient();
    }

    /// <summary>
    /// Convention: CLI discovers this via reflection for scoring during optimization.
    /// Optional — falls back to keyword overlap on string properties.
    /// </summary>
    public static float Score(QAOutput predicted, QAOutput expected)
    {
        if (string.IsNullOrEmpty(predicted.Answer) || string.IsNullOrEmpty(expected.Answer))
            return 0f;

        var expectedWords = expected.Answer
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.ToLowerInvariant())
            .ToHashSet();

        var predictedWords = predicted.Answer
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.ToLowerInvariant())
            .ToHashSet();

        return expectedWords.Count == 0 ? 0f
            : (float)expectedWords.Count(w => predictedWords.Contains(w)) / expectedWords.Count;
    }

    public override async Task<QAOutput> ForwardAsync(
        QAInput input, CancellationToken cancellationToken = default)
    {
        return await _qa.PredictAsync(input, trace: Trace, cancellationToken: cancellationToken);
    }
}
