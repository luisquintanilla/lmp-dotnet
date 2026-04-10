using LMP.Cli;
using LMP.Cli.Commands;

namespace LMP.Tests;

public class AutoOptimizeCommandTests
{
    #region Argument Parsing

    [Fact]
    public async Task NoArgs_ShowsHelp_ReturnsInvalidArguments()
    {
        var exitCode = await AutoOptimizeCommand.ExecuteAsync([]);
        Assert.Equal(Program.ExitCodes.InvalidArguments, exitCode);
    }

    [Fact]
    public async Task HelpFlag_ReturnsSuccess()
    {
        var exitCode = await AutoOptimizeCommand.ExecuteAsync(["--help"]);
        Assert.Equal(Program.ExitCodes.Success, exitCode);
    }

    [Fact]
    public async Task MissingProject_ReturnsInvalidArguments()
    {
        var exitCode = await AutoOptimizeCommand.ExecuteAsync(["--train", "data.jsonl"]);
        Assert.Equal(Program.ExitCodes.InvalidArguments, exitCode);
    }

    [Fact]
    public async Task MissingTrain_WithNonexistentProject_FailsOnBuild()
    {
        // --train is optional (can come from [AutoOptimize] attribute).
        // With nonexistent project, build fails before train is checked.
        var exitCode = await AutoOptimizeCommand.ExecuteAsync(["--project", "test.csproj"]);
        Assert.Equal(Program.ExitCodes.ProjectNotFound, exitCode);
    }

    [Fact]
    public async Task UnknownOption_ReturnsInvalidArguments()
    {
        var exitCode = await AutoOptimizeCommand.ExecuteAsync(["--unknown"]);
        Assert.Equal(Program.ExitCodes.InvalidArguments, exitCode);
    }

    [Fact]
    public async Task InvalidNumTrials_ReturnsInvalidArguments()
    {
        var exitCode = await AutoOptimizeCommand.ExecuteAsync(
            ["--project", "test.csproj", "--train", "data.jsonl", "--num-trials", "abc"]);
        Assert.Equal(Program.ExitCodes.InvalidArguments, exitCode);
    }

    [Fact]
    public async Task ZeroNumTrials_ReturnsInvalidArguments()
    {
        var exitCode = await AutoOptimizeCommand.ExecuteAsync(
            ["--project", "test.csproj", "--train", "data.jsonl", "--num-trials", "0"]);
        Assert.Equal(Program.ExitCodes.InvalidArguments, exitCode);
    }

    [Fact]
    public async Task InvalidMaxDemos_ReturnsInvalidArguments()
    {
        var exitCode = await AutoOptimizeCommand.ExecuteAsync(
            ["--project", "test.csproj", "--train", "data.jsonl", "--max-demos", "-1"]);
        Assert.Equal(Program.ExitCodes.InvalidArguments, exitCode);
    }

    [Fact]
    public async Task UnknownOptimizer_ReturnsInvalidArguments()
    {
        var exitCode = await AutoOptimizeCommand.ExecuteAsync(
            ["--project", "test.csproj", "--train", "data.jsonl", "--optimizer", "miprov2"]);
        Assert.Equal(Program.ExitCodes.InvalidArguments, exitCode);
    }

    [Fact]
    public async Task ForceFlag_IsAccepted()
    {
        // --force is accepted during parsing; with nonexistent project, build fails first
        var exitCode = await AutoOptimizeCommand.ExecuteAsync(
            ["--project", "nonexistent.csproj", "--train", "data.jsonl", "--force"]);
        Assert.Equal(Program.ExitCodes.ProjectNotFound, exitCode);
    }

    [Fact]
    public async Task ModelFlag_IsAccepted()
    {
        var exitCode = await AutoOptimizeCommand.ExecuteAsync(
            ["--project", "nonexistent.csproj", "--train", "data.jsonl", "--model", "gpt-4o-mini"]);
        Assert.Equal(Program.ExitCodes.ProjectNotFound, exitCode);
    }

    [Fact]
    public async Task OutputFlag_IsAccepted()
    {
        var exitCode = await AutoOptimizeCommand.ExecuteAsync(
            ["--project", "nonexistent.csproj", "--train", "data.jsonl", "--output", "MyGenerated"]);
        Assert.Equal(Program.ExitCodes.ProjectNotFound, exitCode);
    }

    #endregion

    #region Project Not Found

    [Fact]
    public async Task NonexistentProject_ReturnsProjectNotFound()
    {
        var exitCode = await AutoOptimizeCommand.RunAutoOptimizeAsync(
            project: "nonexistent.csproj",
            trainPath: "data.jsonl",
            devPath: null,
            model: null,
            outputDir: null,
            optimizerName: "random",
            numTrials: 4,
            maxDemos: 2,
            force: false,
            CancellationToken.None);
        Assert.Equal(Program.ExitCodes.ProjectNotFound, exitCode);
    }

    [Fact]
    public async Task DevFlagAccepted_PassesThroughToRun()
    {
        var exitCode = await AutoOptimizeCommand.ExecuteAsync(
            ["--project", "test.csproj", "--train", "data.jsonl", "--dev", "dev.jsonl"]);
        Assert.Equal(Program.ExitCodes.ProjectNotFound, exitCode);
    }

    #endregion

    #region CLI Entry Point

    [Fact]
    public async Task Main_AutoOptimizeWithHelp_ReturnsSuccess()
    {
        var exitCode = await Program.Main(["auto-optimize", "--help"]);
        Assert.Equal(Program.ExitCodes.Success, exitCode);
    }

    #endregion
}
