using LMP.Cli;
using LMP.Cli.Commands;

namespace LMP.Tests;

public class OptimizeCommandTests
{
    #region Argument Parsing

    [Fact]
    public async Task NoArgs_ShowsHelp_ReturnsInvalidArguments()
    {
        var exitCode = await OptimizeCommand.ExecuteAsync([]);
        Assert.Equal(Program.ExitCodes.InvalidArguments, exitCode);
    }

    [Fact]
    public async Task HelpFlag_ReturnsSuccess()
    {
        var exitCode = await OptimizeCommand.ExecuteAsync(["--help"]);
        Assert.Equal(Program.ExitCodes.Success, exitCode);
    }

    [Fact]
    public async Task MissingProject_ReturnsInvalidArguments()
    {
        var exitCode = await OptimizeCommand.ExecuteAsync(["--train", "data.jsonl"]);
        Assert.Equal(Program.ExitCodes.InvalidArguments, exitCode);
    }

    [Fact]
    public async Task MissingTrain_ReturnsInvalidArguments()
    {
        var exitCode = await OptimizeCommand.ExecuteAsync(["--project", "test.csproj"]);
        Assert.Equal(Program.ExitCodes.InvalidArguments, exitCode);
    }

    [Fact]
    public async Task UnknownOption_ReturnsInvalidArguments()
    {
        var exitCode = await OptimizeCommand.ExecuteAsync(["--unknown"]);
        Assert.Equal(Program.ExitCodes.InvalidArguments, exitCode);
    }

    [Fact]
    public async Task InvalidNumTrials_ReturnsInvalidArguments()
    {
        var exitCode = await OptimizeCommand.ExecuteAsync(
            ["--project", "test.csproj", "--train", "data.jsonl", "--num-trials", "abc"]);
        Assert.Equal(Program.ExitCodes.InvalidArguments, exitCode);
    }

    [Fact]
    public async Task ZeroNumTrials_ReturnsInvalidArguments()
    {
        var exitCode = await OptimizeCommand.ExecuteAsync(
            ["--project", "test.csproj", "--train", "data.jsonl", "--num-trials", "0"]);
        Assert.Equal(Program.ExitCodes.InvalidArguments, exitCode);
    }

    [Fact]
    public async Task InvalidMaxDemos_ReturnsInvalidArguments()
    {
        var exitCode = await OptimizeCommand.ExecuteAsync(
            ["--project", "test.csproj", "--train", "data.jsonl", "--max-demos", "-1"]);
        Assert.Equal(Program.ExitCodes.InvalidArguments, exitCode);
    }

    #endregion

    #region Project Not Found

    [Fact]
    public async Task NonexistentProject_ReturnsProjectNotFound()
    {
        var exitCode = await OptimizeCommand.RunOptimizeAsync(
            project: "nonexistent.csproj",
            trainPath: "data.jsonl",
            outputPath: "output.json",
            optimizerName: "random",
            numTrials: 4,
            maxDemos: 2,
            CancellationToken.None);
        Assert.Equal(Program.ExitCodes.ProjectNotFound, exitCode);
    }

    #endregion

    #region CLI Entry Point

    [Fact]
    public async Task Main_OptimizeWithHelp_ReturnsSuccess()
    {
        var exitCode = await Program.Main(["optimize", "--help"]);
        Assert.Equal(Program.ExitCodes.Success, exitCode);
    }

    #endregion
}
