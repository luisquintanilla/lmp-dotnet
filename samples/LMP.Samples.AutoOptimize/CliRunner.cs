using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace LMP.Samples.AutoOptimize;

/// <summary>
/// CLI runner for the auto-optimize sample. Discovered by reflection when
/// <c>dotnet lmp auto-optimize</c> (or <c>dotnet build -p:LmpAutoOptimize=true</c>) runs.
/// </summary>
/// <remarks>
/// LLM configuration:
///   1. User secrets: <c>AzureOpenAI:Endpoint</c> and <c>AzureOpenAI:Deployment</c>
///   2. Override: <c>LMP_MODEL</c> env var (set by CLI <c>--model</c> flag or MSBuild <c>LmpModel</c>)
///
/// Setup:
///   dotnet user-secrets set "AzureOpenAI:Endpoint" "https://YOUR_RESOURCE.openai.azure.com/"
///   dotnet user-secrets set "AzureOpenAI:Deployment" "gpt-4.1-nano"
/// </remarks>
public class CliRunner : ILmpRunner
{
    public LmpModule CreateModule()
    {
        var config = new ConfigurationBuilder()
            .AddUserSecrets<CliRunner>()
            .Build();

        string endpoint = config["AzureOpenAI:Endpoint"]
            ?? throw new InvalidOperationException(
                "Set AzureOpenAI:Endpoint in user secrets: " +
                "dotnet user-secrets set \"AzureOpenAI:Endpoint\" \"https://YOUR_RESOURCE.openai.azure.com/\"");

        // LMP_MODEL env var (from CLI --model flag) overrides user secrets
        string deployment = Environment.GetEnvironmentVariable("LMP_MODEL")
            ?? config["AzureOpenAI:Deployment"]
            ?? throw new InvalidOperationException(
                "Set AzureOpenAI:Deployment in user secrets: " +
                "dotnet user-secrets set \"AzureOpenAI:Deployment\" \"gpt-4.1-nano\"");

        IChatClient client = new AzureOpenAIClient(
                new Uri(endpoint), new DefaultAzureCredential())
            .GetChatClient(deployment)
            .AsIChatClient();

        return new QAModule(client);
    }

    public Func<Example, object, float> CreateMetric()
    {
        return Metric.Create<QAOutput, QAOutput>((predicted, expected) =>
        {
            if (string.IsNullOrEmpty(predicted.Answer) || string.IsNullOrEmpty(expected.Answer))
                return 0f;

            // Keyword overlap metric
            var expectedWords = expected.Answer
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length > 3)
                .Select(w => w.ToLowerInvariant())
                .ToHashSet();

            if (expectedWords.Count == 0)
                return predicted.Answer.Length > 0 ? 0.5f : 0f;

            var predictedWords = predicted.Answer
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(w => w.ToLowerInvariant())
                .ToHashSet();

            var overlap = expectedWords.Count(w => predictedWords.Contains(w));
            return (float)overlap / expectedWords.Count;
        });
    }

    public IReadOnlyList<Example> LoadDataset(string path)
        => Example.LoadFromJsonl<QAInput, QAOutput>(path);
}
