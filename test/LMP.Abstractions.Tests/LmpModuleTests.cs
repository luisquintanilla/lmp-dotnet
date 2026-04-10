using System.Collections;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace LMP.Tests;

public class LmpModuleTests
{
    private sealed class TestModule : LmpModule
    {
        public override Task<object> ForwardAsync(
            object input, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(input);
        }
    }

    private sealed class ModuleWithPredictors : LmpModule
    {
        public override Task<object> ForwardAsync(
            object input, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(input);
        }

        public override IReadOnlyList<(string Name, IPredictor Predictor)> GetPredictors()
            => [];
    }

    /// <summary>
    /// A fake predictor for testing SaveAsync/LoadAsync round-trips.
    /// </summary>
    private sealed class FakePredictor : IPredictor
    {
        public string Name { get; set; } = "fake";
        public string Instructions { get; set; } = "";
        public List<(object Input, object Output)> TypedDemos { get; set; } = [];
        IList IPredictor.Demos => TypedDemos;
        public ChatOptions Config { get; set; } = new();

        public void AddDemo(object input, object output)
        {
            TypedDemos.Add((input, output));
        }

        public PredictorState GetState()
        {
            var demos = new List<DemoEntry>();
            foreach (var (inp, outp) in TypedDemos)
            {
                demos.Add(new DemoEntry
                {
                    Input = ToJsonDict(inp),
                    Output = ToJsonDict(outp)
                });
            }
            return new PredictorState
            {
                Instructions = Instructions,
                Demos = demos,
                Config = null
            };
        }

        public void LoadState(PredictorState state)
        {
            Instructions = state.Instructions;
            TypedDemos.Clear();
            foreach (var entry in state.Demos)
            {
                TypedDemos.Add((entry.Input, entry.Output));
            }
        }

        public IPredictor Clone()
        {
            var clone = new FakePredictor
            {
                Name = Name,
                Instructions = Instructions,
            };
            clone.TypedDemos = new List<(object, object)>(TypedDemos);
            return clone;
        }

        private static Dictionary<string, JsonElement> ToJsonDict(object value)
        {
            var json = JsonSerializer.Serialize(value);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                var dict = new Dictionary<string, JsonElement>();
                foreach (var prop in doc.RootElement.EnumerateObject())
                    dict[prop.Name] = prop.Value.Clone();
                return dict;
            }
            return new Dictionary<string, JsonElement>
            {
                ["value"] = doc.RootElement.Clone()
            };
        }
    }

    /// <summary>
    /// A module with fake predictors that supports Save/Load round-trip testing.
    /// </summary>
    private sealed class SaveLoadModule : LmpModule
    {
        private readonly List<FakePredictor> _predictors;

        public SaveLoadModule(params FakePredictor[] predictors)
        {
            _predictors = [.. predictors];
        }

        public override Task<object> ForwardAsync(
            object input, CancellationToken cancellationToken = default)
            => Task.FromResult(input);

        public override IReadOnlyList<(string Name, IPredictor Predictor)> GetPredictors()
            => _predictors.Select(p => (p.Name, (IPredictor)p)).ToList();

        protected override LmpModule CloneCore()
        {
            var cloned = _predictors.Select(p => (FakePredictor)p.Clone()).ToArray();
            return new SaveLoadModule(cloned);
        }
    }

    #region Existing Tests

    [Fact]
    public void Subclass_Compiles_AndOverridesForwardAsync()
    {
        var module = new TestModule();

        Assert.NotNull(module);
    }

    [Fact]
    public async Task ForwardAsync_ExecutesSubclassLogic()
    {
        var module = new TestModule();

        var result = await module.ForwardAsync("test");

        Assert.Equal("test", result);
    }

    [Fact]
    public void Trace_DefaultsToNull()
    {
        var module = new TestModule();

        Assert.Null(module.Trace);
    }

    [Fact]
    public void Trace_CanBeSet()
    {
        var module = new TestModule();
        var trace = new Trace();

        module.Trace = trace;

        Assert.Same(trace, module.Trace);
    }

    [Fact]
    public void GetPredictors_DefaultReturnsEmpty()
    {
        var module = new TestModule();

        var predictors = module.GetPredictors();

        Assert.Empty(predictors);
    }

    [Fact]
    public void GetPredictors_CanBeOverridden()
    {
        var module = new ModuleWithPredictors();

        var predictors = module.GetPredictors();

        Assert.Empty(predictors);
    }

    #endregion

    #region Generic LmpModule<TInput, TOutput>

    private sealed class TypedModule : LmpModule<string, int>
    {
        public override Task<int> ForwardAsync(
            string input, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(input.Length);
        }
    }

    [Fact]
    public async Task GenericModule_TypedForwardAsync_ReturnsTypedResult()
    {
        var module = new TypedModule();

        int result = await module.ForwardAsync("hello");

        Assert.Equal(5, result);
    }

    [Fact]
    public async Task GenericModule_UntypedBridge_BoxesResult()
    {
        LmpModule module = new TypedModule();

        object result = await module.ForwardAsync("hello");

        Assert.Equal(5, result);
    }

    [Fact]
    public async Task GenericModule_UntypedBridge_CastsInput()
    {
        LmpModule module = new TypedModule();

        object result = await module.ForwardAsync((object)"test");

        Assert.Equal(4, result);
    }

    [Fact]
    public async Task GenericModule_UntypedBridge_ThrowsOnWrongInputType()
    {
        LmpModule module = new TypedModule();

        await Assert.ThrowsAsync<InvalidCastException>(
            () => module.ForwardAsync(42));
    }

    #endregion

    #region SaveAsync

    [Fact]
    public async Task SaveAsync_WritesJsonFile()
    {
        var pred = new FakePredictor { Name = "classify", Instructions = "Classify input" };
        var module = new SaveLoadModule(pred);

        var path = Path.Combine(Path.GetTempPath(), $"lmp_test_{Guid.NewGuid()}.json");
        try
        {
            await module.SaveStateAsync(path);

            Assert.True(File.Exists(path));
            var content = await File.ReadAllTextAsync(path);
            Assert.Contains("\"version\"", content);
            Assert.Contains("\"1.0\"", content);
            Assert.Contains("\"module\"", content);
            Assert.Contains("SaveLoadModule", content);
            Assert.Contains("\"predictors\"", content);
            Assert.Contains("classify", content);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task SaveAsync_ContainsVersionAndModuleName()
    {
        var module = new SaveLoadModule();
        var path = Path.Combine(Path.GetTempPath(), $"lmp_test_{Guid.NewGuid()}.json");
        try
        {
            await module.SaveStateAsync(path);

            var json = await File.ReadAllTextAsync(path);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Assert.Equal("1.0", root.GetProperty("version").GetString());
            Assert.Equal("SaveLoadModule", root.GetProperty("module").GetString());
            Assert.Equal(JsonValueKind.Object, root.GetProperty("predictors").ValueKind);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task SaveAsync_SerializesPredictorInstructions()
    {
        var pred = new FakePredictor
        {
            Name = "draft",
            Instructions = "Draft a helpful reply"
        };
        var module = new SaveLoadModule(pred);

        var path = Path.Combine(Path.GetTempPath(), $"lmp_test_{Guid.NewGuid()}.json");
        try
        {
            await module.SaveStateAsync(path);

            var json = await File.ReadAllTextAsync(path);
            using var doc = JsonDocument.Parse(json);
            var predictors = doc.RootElement.GetProperty("predictors");
            var draftPred = predictors.GetProperty("draft");
            Assert.Equal("Draft a helpful reply",
                draftPred.GetProperty("instructions").GetString());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task SaveAsync_SerializesDemos()
    {
        var pred = new FakePredictor
        {
            Name = "classify",
            Instructions = "Classify"
        };
        pred.AddDemo("input1", "output1");
        pred.AddDemo("input2", "output2");
        var module = new SaveLoadModule(pred);

        var path = Path.Combine(Path.GetTempPath(), $"lmp_test_{Guid.NewGuid()}.json");
        try
        {
            await module.SaveStateAsync(path);

            var json = await File.ReadAllTextAsync(path);
            using var doc = JsonDocument.Parse(json);
            var demos = doc.RootElement
                .GetProperty("predictors")
                .GetProperty("classify")
                .GetProperty("demos");
            Assert.Equal(2, demos.GetArrayLength());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task SaveAsync_MultiplePredictors_AllSerialized()
    {
        var pred1 = new FakePredictor { Name = "step1", Instructions = "Step 1" };
        var pred2 = new FakePredictor { Name = "step2", Instructions = "Step 2" };
        var module = new SaveLoadModule(pred1, pred2);

        var path = Path.Combine(Path.GetTempPath(), $"lmp_test_{Guid.NewGuid()}.json");
        try
        {
            await module.SaveStateAsync(path);

            var json = await File.ReadAllTextAsync(path);
            using var doc = JsonDocument.Parse(json);
            var predictors = doc.RootElement.GetProperty("predictors");
            Assert.True(predictors.TryGetProperty("step1", out _));
            Assert.True(predictors.TryGetProperty("step2", out _));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task SaveAsync_AtomicWrite_NoTempFileAfterSuccess()
    {
        var module = new SaveLoadModule();
        var path = Path.Combine(Path.GetTempPath(), $"lmp_test_{Guid.NewGuid()}.json");
        try
        {
            await module.SaveStateAsync(path);

            Assert.True(File.Exists(path));
            Assert.False(File.Exists(path + ".tmp"),
                "Temp file should be removed after successful atomic write");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
        }
    }

    [Fact]
    public async Task SaveAsync_NoPredictors_WritesEmptyPredictorsMap()
    {
        var module = new SaveLoadModule();
        var path = Path.Combine(Path.GetTempPath(), $"lmp_test_{Guid.NewGuid()}.json");
        try
        {
            await module.SaveStateAsync(path);

            var json = await File.ReadAllTextAsync(path);
            using var doc = JsonDocument.Parse(json);
            var predictors = doc.RootElement.GetProperty("predictors");
            Assert.Empty(predictors.EnumerateObject().ToList());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    #endregion

    #region LoadAsync

    [Fact]
    public async Task LoadAsync_RestoresInstructions()
    {
        var pred = new FakePredictor { Name = "classify", Instructions = "original" };
        var module = new SaveLoadModule(pred);

        var path = Path.Combine(Path.GetTempPath(), $"lmp_test_{Guid.NewGuid()}.json");
        try
        {
            // Save with modified instructions
            pred.Instructions = "optimized instruction";
            await module.SaveStateAsync(path);

            // Create fresh module and load
            var freshPred = new FakePredictor { Name = "classify", Instructions = "default" };
            var freshModule = new SaveLoadModule(freshPred);
            await freshModule.ApplyStateAsync(path);

            Assert.Equal("optimized instruction", freshPred.Instructions);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task LoadAsync_RestoresDemos()
    {
        var pred = new FakePredictor { Name = "classify", Instructions = "test" };
        pred.AddDemo("input1", "output1");
        pred.AddDemo("input2", "output2");
        var module = new SaveLoadModule(pred);

        var path = Path.Combine(Path.GetTempPath(), $"lmp_test_{Guid.NewGuid()}.json");
        try
        {
            await module.SaveStateAsync(path);

            var freshPred = new FakePredictor { Name = "classify", Instructions = "" };
            var freshModule = new SaveLoadModule(freshPred);
            await freshModule.ApplyStateAsync(path);

            Assert.Equal(2, freshPred.TypedDemos.Count);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task LoadAsync_UnknownPredictor_Ignored()
    {
        // Save module with "step1" predictor
        var pred = new FakePredictor { Name = "step1", Instructions = "first" };
        var module = new SaveLoadModule(pred);
        var path = Path.Combine(Path.GetTempPath(), $"lmp_test_{Guid.NewGuid()}.json");
        try
        {
            await module.SaveStateAsync(path);

            // Load into module with different predictor name
            var otherPred = new FakePredictor { Name = "step2", Instructions = "original" };
            var otherModule = new SaveLoadModule(otherPred);
            await otherModule.ApplyStateAsync(path);

            // step2 not in file, so it should remain unchanged
            Assert.Equal("original", otherPred.Instructions);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task LoadAsync_FileNotFound_Throws()
    {
        var module = new SaveLoadModule();
        var path = Path.Combine(Path.GetTempPath(), $"nonexistent_{Guid.NewGuid()}.json");

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => module.ApplyStateAsync(path));
    }

    [Fact]
    public async Task LoadAsync_ForwardCompatibility_UnknownPropertiesIgnored()
    {
        // Manually write JSON with extra unknown properties
        var json = """
        {
          "version": "1.0",
          "module": "TestModule",
          "futureField": "should be ignored",
          "predictors": {
            "classify": {
              "instructions": "test instruction",
              "demos": [],
              "extraProp": 42
            }
          }
        }
        """;

        var path = Path.Combine(Path.GetTempPath(), $"lmp_test_{Guid.NewGuid()}.json");
        try
        {
            await File.WriteAllTextAsync(path, json);

            var pred = new FakePredictor { Name = "classify", Instructions = "default" };
            var module = new SaveLoadModule(pred);
            await module.ApplyStateAsync(path);

            Assert.Equal("test instruction", pred.Instructions);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    #endregion

    #region SaveAsync/LoadAsync Round-Trip

    [Fact]
    public async Task RoundTrip_SaveThenLoad_PreservesState()
    {
        var pred = new FakePredictor
        {
            Name = "classify",
            Instructions = "Classify the ticket"
        };
        pred.AddDemo("billing question", "billing");
        pred.AddDemo("login issue", "account");
        var module = new SaveLoadModule(pred);

        var path = Path.Combine(Path.GetTempPath(), $"lmp_test_{Guid.NewGuid()}.json");
        try
        {
            await module.SaveStateAsync(path);

            var freshPred = new FakePredictor { Name = "classify", Instructions = "" };
            var freshModule = new SaveLoadModule(freshPred);
            await freshModule.ApplyStateAsync(path);

            Assert.Equal("Classify the ticket", freshPred.Instructions);
            Assert.Equal(2, freshPred.TypedDemos.Count);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task RoundTrip_MultiplePredictors_AllPreserved()
    {
        var pred1 = new FakePredictor { Name = "step1", Instructions = "Do step 1" };
        pred1.AddDemo("a", "b");
        var pred2 = new FakePredictor { Name = "step2", Instructions = "Do step 2" };
        pred2.AddDemo("c", "d");
        var module = new SaveLoadModule(pred1, pred2);

        var path = Path.Combine(Path.GetTempPath(), $"lmp_test_{Guid.NewGuid()}.json");
        try
        {
            await module.SaveStateAsync(path);

            var freshPred1 = new FakePredictor { Name = "step1", Instructions = "" };
            var freshPred2 = new FakePredictor { Name = "step2", Instructions = "" };
            var freshModule = new SaveLoadModule(freshPred1, freshPred2);
            await freshModule.ApplyStateAsync(path);

            Assert.Equal("Do step 1", freshPred1.Instructions);
            Assert.Equal("Do step 2", freshPred2.Instructions);
            Assert.Single(freshPred1.TypedDemos);
            Assert.Single(freshPred2.TypedDemos);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task RoundTrip_OverwriteExistingFile()
    {
        var pred = new FakePredictor { Name = "p", Instructions = "v1" };
        var module = new SaveLoadModule(pred);

        var path = Path.Combine(Path.GetTempPath(), $"lmp_test_{Guid.NewGuid()}.json");
        try
        {
            // First save
            await module.SaveStateAsync(path);

            // Update and save again
            pred.Instructions = "v2";
            await module.SaveStateAsync(path);

            var freshPred = new FakePredictor { Name = "p", Instructions = "" };
            var freshModule = new SaveLoadModule(freshPred);
            await freshModule.ApplyStateAsync(path);

            Assert.Equal("v2", freshPred.Instructions);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    #endregion

    #region Clone Tests

    [Fact]
    public void Clone_ThrowsNotSupported_WithoutSourceGen()
    {
        var module = new TestModule();

        Assert.Throws<NotSupportedException>(() => module.Clone<TestModule>());
    }

    [Fact]
    public void Clone_ThrowsNotSupported_MessageMentionsPartialClass()
    {
        var module = new TestModule();

        var ex = Assert.Throws<NotSupportedException>(() => module.Clone<TestModule>());

        Assert.Contains("TestModule", ex.Message);
        Assert.Contains("partial", ex.Message);
    }

    [Fact]
    public void Clone_WithOverriddenCloneCore_ReturnsClone()
    {
        var module = new CloneableModule();
        module.Trace = new Trace();

        var clone = module.Clone<CloneableModule>();

        Assert.NotSame(module, clone);
        Assert.Null(clone.Trace);
    }

    private sealed class CloneableModule : LmpModule
    {
        public override Task<object> ForwardAsync(
            object input, CancellationToken cancellationToken = default)
            => Task.FromResult(input);

        protected override LmpModule CloneCore()
            => (CloneableModule)MemberwiseClone();
    }

    #endregion
}
