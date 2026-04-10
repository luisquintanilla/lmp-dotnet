using System.Text.Json;
using Microsoft.Extensions.AI;
using Moq;

namespace LMP.Tests;

/// <summary>
/// Integration tests for <see cref="LmpModule.SaveAsync"/> and <see cref="LmpModule.LoadAsync"/>.
/// Verifies JSON artifact format, round-trip fidelity, and forward compatibility.
/// </summary>
public class SaveLoadTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IChatClient _client;

    public SaveLoadTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lmp-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _client = new Mock<IChatClient>().Object;
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string TempFile(string name = "module.json") => Path.Combine(_tempDir, name);

    #region SaveAsync Tests

    [Fact]
    public async Task SaveAsync_WritesValidJsonFile()
    {
        var module = new TestModule(_client);
        var path = TempFile();

        await module.SaveStateAsync(path);

        Assert.True(File.Exists(path));
        var json = await File.ReadAllTextAsync(path);
        Assert.False(string.IsNullOrWhiteSpace(json));
    }

    [Fact]
    public async Task SaveAsync_ContainsVersionField()
    {
        var module = new TestModule(_client);
        var path = TempFile();

        await module.SaveStateAsync(path);

        var doc = await ReadJsonDoc(path);
        Assert.True(doc.RootElement.TryGetProperty("version", out var version));
        Assert.Equal("1.0", version.GetString());
    }

    [Fact]
    public async Task SaveAsync_ContainsModuleName()
    {
        var module = new TestModule(_client);
        var path = TempFile();

        await module.SaveStateAsync(path);

        var doc = await ReadJsonDoc(path);
        Assert.True(doc.RootElement.TryGetProperty("module", out var moduleName));
        Assert.Equal("TestModule", moduleName.GetString());
    }

    [Fact]
    public async Task SaveAsync_ContainsPredictorsMap()
    {
        var module = new TestModule(_client);
        var path = TempFile();

        await module.SaveStateAsync(path);

        var doc = await ReadJsonDoc(path);
        Assert.True(doc.RootElement.TryGetProperty("predictors", out var predictors));
        Assert.Equal(JsonValueKind.Object, predictors.ValueKind);
        Assert.True(predictors.TryGetProperty("Classify", out _));
    }

    [Fact]
    public async Task SaveAsync_PredictorState_ContainsInstructions()
    {
        var module = new TestModule(_client);
        module.Classify.Instructions = "Classify tickets by category";
        var path = TempFile();

        await module.SaveStateAsync(path);

        var doc = await ReadJsonDoc(path);
        var classify = doc.RootElement.GetProperty("predictors").GetProperty("Classify");
        Assert.Equal("Classify tickets by category", classify.GetProperty("instructions").GetString());
    }

    [Fact]
    public async Task SaveAsync_PredictorState_ContainsEmptyDemosArray()
    {
        var module = new TestModule(_client);
        var path = TempFile();

        await module.SaveStateAsync(path);

        var doc = await ReadJsonDoc(path);
        var classify = doc.RootElement.GetProperty("predictors").GetProperty("Classify");
        var demos = classify.GetProperty("demos");
        Assert.Equal(JsonValueKind.Array, demos.ValueKind);
        Assert.Equal(0, demos.GetArrayLength());
    }

    [Fact]
    public async Task SaveAsync_PredictorState_ContainsDemosWithInputOutput()
    {
        var module = new TestModule(_client);
        module.Classify.Demos.Add(("billing issue", new CategoryOutput { Category = "billing", Urgency = 3 }));
        var path = TempFile();

        await module.SaveStateAsync(path);

        var doc = await ReadJsonDoc(path);
        var demos = doc.RootElement.GetProperty("predictors").GetProperty("Classify").GetProperty("demos");
        Assert.Equal(1, demos.GetArrayLength());

        var demo = demos[0];
        Assert.True(demo.TryGetProperty("input", out var input));
        Assert.True(demo.TryGetProperty("output", out var output));

        // Input is a string wrapped as { "value": "..." }
        Assert.True(input.TryGetProperty("value", out _));

        // Output has Category and Urgency
        Assert.True(output.TryGetProperty("Category", out _) || output.TryGetProperty("category", out _));
    }

    [Fact]
    public async Task SaveAsync_MultiplePredictors_AllSerialized()
    {
        var module = new TwoPredictorModule(_client);
        var path = TempFile();

        await module.SaveStateAsync(path);

        var doc = await ReadJsonDoc(path);
        var predictors = doc.RootElement.GetProperty("predictors");
        Assert.True(predictors.TryGetProperty("Classify", out _));
        Assert.True(predictors.TryGetProperty("DraftReply", out _));
    }

    [Fact]
    public async Task SaveAsync_AtomicWrite_NoTmpFileRemains()
    {
        var module = new TestModule(_client);
        var path = TempFile();

        await module.SaveStateAsync(path);

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task SaveAsync_OverwritesExistingFile()
    {
        var module = new TestModule(_client);
        var path = TempFile();

        module.Classify.Instructions = "first";
        await module.SaveStateAsync(path);

        module.Classify.Instructions = "second";
        await module.SaveStateAsync(path);

        var doc = await ReadJsonDoc(path);
        var instructions = doc.RootElement
            .GetProperty("predictors").GetProperty("Classify").GetProperty("instructions").GetString();
        Assert.Equal("second", instructions);
    }

    #endregion

    #region LoadAsync Tests

    [Fact]
    public async Task LoadAsync_RestoresInstructions()
    {
        var module = new TestModule(_client);
        module.Classify.Instructions = "Classify tickets by category";
        var path = TempFile();
        await module.SaveStateAsync(path);

        var fresh = new TestModule(_client);
        await fresh.ApplyStateAsync(path);

        Assert.Equal("Classify tickets by category", fresh.Classify.Instructions);
    }

    [Fact]
    public async Task LoadAsync_RestoresDemos()
    {
        var module = new TestModule(_client);
        module.Classify.Demos.Add(("billing issue", new CategoryOutput { Category = "billing", Urgency = 3 }));
        module.Classify.Demos.Add(("login problem", new CategoryOutput { Category = "account", Urgency = 4 }));
        var path = TempFile();
        await module.SaveStateAsync(path);

        var fresh = new TestModule(_client);
        await fresh.ApplyStateAsync(path);

        Assert.Equal(2, fresh.Classify.Demos.Count);
        Assert.Equal("billing issue", fresh.Classify.Demos[0].Input);
        Assert.Equal("billing", fresh.Classify.Demos[0].Output.Category);
        Assert.Equal(3, fresh.Classify.Demos[0].Output.Urgency);
        Assert.Equal("login problem", fresh.Classify.Demos[1].Input);
        Assert.Equal("account", fresh.Classify.Demos[1].Output.Category);
    }

    [Fact]
    public async Task LoadAsync_ClearsExistingDemos()
    {
        var module = new TestModule(_client);
        module.Classify.Demos.Add(("old demo", new CategoryOutput { Category = "old", Urgency = 1 }));
        var path = TempFile();

        // Save with no demos
        var saveModule = new TestModule(_client);
        await saveModule.SaveStateAsync(path);

        await module.ApplyStateAsync(path);

        Assert.Empty(module.Classify.Demos);
    }

    [Fact]
    public async Task LoadAsync_ThrowsOnInvalidFile()
    {
        var path = TempFile();
        await File.WriteAllTextAsync(path, "not json at all");

        var module = new TestModule(_client);

        await Assert.ThrowsAnyAsync<JsonException>(() => module.ApplyStateAsync(path));
    }

    [Fact]
    public async Task LoadAsync_ThrowsOnMissingFile()
    {
        var module = new TestModule(_client);

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => module.ApplyStateAsync(TempFile("nonexistent.json")));
    }

    [Fact]
    public async Task LoadAsync_IgnoresMissingPredictor()
    {
        // Save a module that has 2 predictors
        var twoPredictor = new TwoPredictorModule(_client);
        twoPredictor.Classify.Instructions = "classify";
        twoPredictor.DraftReply.Instructions = "draft";
        var path = TempFile();
        await twoPredictor.SaveStateAsync(path);

        // Load into a module that only has 1 predictor — "Classify" matches, "DraftReply" is ignored
        var singlePredictor = new TestModule(_client);
        await singlePredictor.ApplyStateAsync(path);

        Assert.Equal("classify", singlePredictor.Classify.Instructions);
    }

    [Fact]
    public async Task LoadAsync_PreservesUnmatchedPredictorState()
    {
        // Save a module with just Classify
        var module = new TestModule(_client);
        module.Classify.Instructions = "from file";
        var path = TempFile();
        await module.SaveStateAsync(path);

        // Load into a module with 2 predictors — DraftReply not in file, should keep defaults
        var twoPredictor = new TwoPredictorModule(_client);
        twoPredictor.DraftReply.Instructions = "original draft instructions";
        await twoPredictor.ApplyStateAsync(path);

        Assert.Equal("from file", twoPredictor.Classify.Instructions);
        Assert.Equal("original draft instructions", twoPredictor.DraftReply.Instructions);
    }

    #endregion

    #region Round-Trip Integration

    [Fact]
    public async Task RoundTrip_SaveLoadRestoresFullState()
    {
        var module = new TwoPredictorModule(_client);
        module.Classify.Instructions = "Classify support tickets";
        module.Classify.Demos.Add(("charged twice", new CategoryOutput { Category = "billing", Urgency = 3 }));
        module.Classify.Demos.Add(("can't login", new CategoryOutput { Category = "account", Urgency = 4 }));
        module.DraftReply.Instructions = "Draft a helpful reply";
        module.DraftReply.Demos.Add(
            (new CategoryOutput { Category = "billing", Urgency = 3 },
             new ReplyOutput { Reply = "I'm sorry about the double charge." }));

        var path = TempFile();
        await module.SaveStateAsync(path);

        var restored = new TwoPredictorModule(_client);
        await restored.ApplyStateAsync(path);

        // Verify Classify predictor
        Assert.Equal("Classify support tickets", restored.Classify.Instructions);
        Assert.Equal(2, restored.Classify.Demos.Count);
        Assert.Equal("charged twice", restored.Classify.Demos[0].Input);
        Assert.Equal("billing", restored.Classify.Demos[0].Output.Category);
        Assert.Equal(3, restored.Classify.Demos[0].Output.Urgency);
        Assert.Equal("can't login", restored.Classify.Demos[1].Input);
        Assert.Equal("account", restored.Classify.Demos[1].Output.Category);

        // Verify DraftReply predictor
        Assert.Equal("Draft a helpful reply", restored.DraftReply.Instructions);
        Assert.Single(restored.DraftReply.Demos);
        Assert.Equal("billing", restored.DraftReply.Demos[0].Input.Category);
        Assert.Equal("I'm sorry about the double charge.", restored.DraftReply.Demos[0].Output.Reply);
    }

    [Fact]
    public async Task RoundTrip_EmptyModule_SavesAndLoads()
    {
        var module = new TestModule(_client);
        var path = TempFile();

        await module.SaveStateAsync(path);

        var restored = new TestModule(_client);
        await restored.ApplyStateAsync(path);

        Assert.Empty(restored.Classify.Demos);
        Assert.Equal(string.Empty, restored.Classify.Instructions);
    }

    [Fact]
    public async Task RoundTrip_MultipleSavesOverwrite()
    {
        var module = new TestModule(_client);
        var path = TempFile();

        module.Classify.Instructions = "version 1";
        await module.SaveStateAsync(path);

        module.Classify.Instructions = "version 2";
        module.Classify.Demos.Add(("input", new CategoryOutput { Category = "cat", Urgency = 1 }));
        await module.SaveStateAsync(path);

        var restored = new TestModule(_client);
        await restored.ApplyStateAsync(path);

        Assert.Equal("version 2", restored.Classify.Instructions);
        Assert.Single(restored.Classify.Demos);
    }

    #endregion

    #region Forward Compatibility

    [Fact]
    public async Task ForwardCompatibility_UnknownPropertiesIgnored()
    {
        // Write JSON with unknown fields that a future version might add
        var json = """
        {
          "version": "1.0",
          "module": "TestModule",
          "provenance": { "hash": "abc123", "timestamp": "2026-01-01" },
          "predictors": {
            "Classify": {
              "instructions": "Classify things",
              "demos": [],
              "config": null,
              "optimizerMetadata": { "score": 0.95 }
            }
          }
        }
        """;

        var path = TempFile();
        await File.WriteAllTextAsync(path, json);

        var module = new TestModule(_client);
        await module.ApplyStateAsync(path);

        Assert.Equal("Classify things", module.Classify.Instructions);
    }

    [Fact]
    public async Task ForwardCompatibility_UnknownPredictorsIgnored()
    {
        // File has a predictor "Summarize" that doesn't exist in our module
        var json = """
        {
          "version": "1.0",
          "module": "TestModule",
          "predictors": {
            "Classify": {
              "instructions": "Classify things",
              "demos": []
            },
            "Summarize": {
              "instructions": "Summarize content",
              "demos": [{ "input": {"text": "long"}, "output": {"summary": "short"} }]
            }
          }
        }
        """;

        var path = TempFile();
        await File.WriteAllTextAsync(path, json);

        var module = new TestModule(_client);
        await module.ApplyStateAsync(path);

        Assert.Equal("Classify things", module.Classify.Instructions);
    }

    [Fact]
    public async Task ForwardCompatibility_NullConfigHandled()
    {
        var json = """
        {
          "version": "1.0",
          "module": "TestModule",
          "predictors": {
            "Classify": {
              "instructions": "Classify",
              "demos": [],
              "config": null
            }
          }
        }
        """;

        var path = TempFile();
        await File.WriteAllTextAsync(path, json);

        var module = new TestModule(_client);
        await module.ApplyStateAsync(path);

        Assert.Equal("Classify", module.Classify.Instructions);
    }

    [Fact]
    public async Task ForwardCompatibility_MissingConfigHandled()
    {
        var json = """
        {
          "version": "1.0",
          "module": "TestModule",
          "predictors": {
            "Classify": {
              "instructions": "Classify",
              "demos": []
            }
          }
        }
        """;

        var path = TempFile();
        await File.WriteAllTextAsync(path, json);

        var module = new TestModule(_client);
        await module.ApplyStateAsync(path);

        Assert.Equal("Classify", module.Classify.Instructions);
    }

    #endregion

    #region Schema Compliance

    [Fact]
    public async Task Schema_UsesCorrectCamelCasePropertyNames()
    {
        var module = new TestModule(_client);
        module.Classify.Instructions = "test";
        module.Classify.Demos.Add(("input", new CategoryOutput { Category = "cat", Urgency = 1 }));
        var path = TempFile();

        await module.SaveStateAsync(path);

        var json = await File.ReadAllTextAsync(path);

        // Verify camelCase naming per ModuleStateSerializerContext options
        Assert.Contains("\"version\"", json);
        Assert.Contains("\"module\"", json);
        Assert.Contains("\"predictors\"", json);
        Assert.Contains("\"instructions\"", json);
        Assert.Contains("\"demos\"", json);
        Assert.Contains("\"input\"", json);
        Assert.Contains("\"output\"", json);
    }

    [Fact]
    public async Task Schema_JsonIsIndented()
    {
        var module = new TestModule(_client);
        var path = TempFile();

        await module.SaveStateAsync(path);

        var json = await File.ReadAllTextAsync(path);
        Assert.Contains("\n", json);
        Assert.Contains("  ", json);
    }

    [Fact]
    public async Task Schema_NullConfigOmittedFromJson()
    {
        var module = new TestModule(_client);
        var path = TempFile();

        await module.SaveStateAsync(path);

        var doc = await ReadJsonDoc(path);
        var classify = doc.RootElement.GetProperty("predictors").GetProperty("Classify");

        // Config should be null (omitted due to WhenWritingNull)
        if (classify.TryGetProperty("config", out var config))
        {
            Assert.Equal(JsonValueKind.Null, config.ValueKind);
        }
        // or it may be omitted entirely — both are acceptable
    }

    #endregion

    #region Helpers

    private static async Task<JsonDocument> ReadJsonDoc(string path)
    {
        var json = await File.ReadAllBytesAsync(path);
        return JsonDocument.Parse(json);
    }

    #endregion

    #region Test Types

    public sealed class CategoryOutput
    {
        public string Category { get; set; } = string.Empty;
        public int Urgency { get; set; }
    }

    public sealed class ReplyOutput
    {
        public string Reply { get; set; } = string.Empty;
    }

    /// <summary>
    /// A simple test module with one predictor.
    /// </summary>
    private sealed class TestModule : LmpModule
    {
        public Predictor<string, CategoryOutput> Classify { get; }

        public TestModule(IChatClient client)
        {
            Classify = new Predictor<string, CategoryOutput>(client) { Name = "Classify" };
        }

        public override Task<object> ForwardAsync(object input, CancellationToken cancellationToken = default)
            => Task.FromResult(input);

        public override IReadOnlyList<(string Name, IPredictor Predictor)> GetPredictors()
            => [("Classify", Classify)];
    }

    /// <summary>
    /// A test module with two predictors (Classify + DraftReply) matching the spec example.
    /// </summary>
    private sealed class TwoPredictorModule : LmpModule
    {
        public Predictor<string, CategoryOutput> Classify { get; }
        public Predictor<CategoryOutput, ReplyOutput> DraftReply { get; }

        public TwoPredictorModule(IChatClient client)
        {
            Classify = new Predictor<string, CategoryOutput>(client) { Name = "Classify" };
            DraftReply = new Predictor<CategoryOutput, ReplyOutput>(client) { Name = "DraftReply" };
        }

        public override Task<object> ForwardAsync(object input, CancellationToken cancellationToken = default)
            => Task.FromResult(input);

        public override IReadOnlyList<(string Name, IPredictor Predictor)> GetPredictors()
            => [("Classify", Classify), ("DraftReply", DraftReply)];
    }

    #endregion
}
