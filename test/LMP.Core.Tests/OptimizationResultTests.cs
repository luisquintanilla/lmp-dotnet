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
            Target = ModuleTarget.For(new EchoModule()),
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
            Target = ModuleTarget.For(module),
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
}
