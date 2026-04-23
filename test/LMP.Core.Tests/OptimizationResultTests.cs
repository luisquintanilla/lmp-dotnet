namespace LMP.Tests;

public class OptimizationResultTests
{
    private sealed class EchoModule : LmpModule
    {
        public override Task<object> ForwardAsync(object input, CancellationToken ct = default)
            => Task.FromResult(input);

        protected override LmpModule CloneCore() => new EchoModule();
    }

    private static OptimizationResult MakeResult(float baseline = 0.5f, float optimized = 0.7f)
        => new()
        {
            Target = new EchoModule(),
            BaselineScore = baseline,
            OptimizedScore = optimized,
            Trials = []
        };

    [Fact]
    public void Scores_AreRecordedCorrectly()
    {
        var result = MakeResult(baseline: 0.3f, optimized: 0.8f);
        Assert.Equal(0.3f, result.BaselineScore);
        Assert.Equal(0.8f, result.OptimizedScore);
    }

    [Fact]
    public void Target_IsAccessible()
    {
        var result = MakeResult();
        Assert.NotNull(result.Target);
    }

    [Fact]
    public async Task WriteArtifactAsync_NullOptions_ReturnsNull()
    {
        var result = MakeResult();
        var path = await result.WriteArtifactAsync(null);
        Assert.Null(path);
    }

    [Fact]
    public async Task WriteArtifactAsync_OptionsWithNullOutputDir_ReturnsNull()
    {
        var result = MakeResult();
        var options = CompileOptions.RuntimeOnly;
        var path = await result.WriteArtifactAsync(options);
        Assert.Null(path);
    }

    [Fact]
    public async Task WriteArtifactAsync_WithOutputDir_WritesFileAndReturnsPath()
    {
        var module = new EchoModule();
        var result = new OptimizationResult
        {
            Target = module,
            BaselineScore = 0.5f,
            OptimizedScore = 0.8f,
            Trials = []
        };

        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var options = new CompileOptions { OutputDir = dir };
            var path = await result.WriteArtifactAsync(options);

            Assert.NotNull(path);
            Assert.True(File.Exists(path), $"Expected file at {path}");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task WriteArtifactAsync_ChatClientTarget_WithClassName_WritesFactory()
    {
        var spy = new SpyChatClient("reply");
        var target = spy.AsOptimizationTarget(b => b.WithSystemPrompt("Be helpful.").WithTemperature(0.8f));
        var result = new OptimizationResult
        {
            Target = target,
            BaselineScore = 0.4f,
            OptimizedScore = 0.6f,
            Trials = []
        };

        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var options = new CompileOptions
            {
                OutputDir = dir,
                ArtifactClassName = "MyOptimizedClient",
                ArtifactNamespace = "Test.Ns"
            };
            var path = await result.WriteArtifactAsync(options);

            Assert.NotNull(path);
            Assert.True(File.Exists(path!));
            var code = await File.ReadAllTextAsync(path!);
            Assert.Contains("public static class MyOptimizedClient", code);
            Assert.Contains("namespace Test.Ns;", code);
            Assert.Contains("Build(IChatClient baseClient", code);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task WriteArtifactAsync_ChatClientTarget_NoClassName_Throws()
    {
        var spy = new SpyChatClient("reply");
        var target = spy.AsOptimizationTarget(b => b.WithSystemPrompt("Hello"));
        var result = new OptimizationResult
        {
            Target = target,
            BaselineScore = 0.4f,
            OptimizedScore = 0.6f,
            Trials = []
        };

        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var options = new CompileOptions { OutputDir = dir }; // No ArtifactClassName
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => result.WriteArtifactAsync(options));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
