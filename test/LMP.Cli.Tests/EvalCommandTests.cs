using LMP.Cli;
using LMP.Cli.Commands;

namespace LMP.Tests;

public class EvalCommandTests
{
    #region Argument Parsing

    [Fact]
    public async Task NoArgs_ShowsHelp_ReturnsInvalidArguments()
    {
        var exitCode = await EvalCommand.ExecuteAsync([]);
        Assert.Equal(Program.ExitCodes.InvalidArguments, exitCode);
    }

    [Fact]
    public async Task HelpFlag_ReturnsSuccess()
    {
        var exitCode = await EvalCommand.ExecuteAsync(["--help"]);
        Assert.Equal(Program.ExitCodes.Success, exitCode);
    }

    [Fact]
    public async Task MissingProject_ReturnsInvalidArguments()
    {
        var exitCode = await EvalCommand.ExecuteAsync(["--dataset", "data.jsonl"]);
        Assert.Equal(Program.ExitCodes.InvalidArguments, exitCode);
    }

    [Fact]
    public async Task MissingDataset_ReturnsInvalidArguments()
    {
        var exitCode = await EvalCommand.ExecuteAsync(["--project", "test.csproj"]);
        Assert.Equal(Program.ExitCodes.InvalidArguments, exitCode);
    }

    [Fact]
    public async Task UnknownOption_ReturnsInvalidArguments()
    {
        var exitCode = await EvalCommand.ExecuteAsync(["--unknown"]);
        Assert.Equal(Program.ExitCodes.InvalidArguments, exitCode);
    }

    [Fact]
    public async Task InvalidConcurrency_ReturnsInvalidArguments()
    {
        var exitCode = await EvalCommand.ExecuteAsync(
            ["--project", "test.csproj", "--dataset", "data.jsonl", "--concurrency", "abc"]);
        Assert.Equal(Program.ExitCodes.InvalidArguments, exitCode);
    }

    [Fact]
    public async Task ZeroConcurrency_ReturnsInvalidArguments()
    {
        var exitCode = await EvalCommand.ExecuteAsync(
            ["--project", "test.csproj", "--dataset", "data.jsonl", "--concurrency", "0"]);
        Assert.Equal(Program.ExitCodes.InvalidArguments, exitCode);
    }

    #endregion

    #region Project Not Found

    [Fact]
    public async Task NonexistentProject_ReturnsProjectNotFound()
    {
        var exitCode = await EvalCommand.RunEvalAsync(
            project: "nonexistent.csproj",
            datasetPath: "data.jsonl",
            artifactPath: null,
            jsonOutput: false,
            maxConcurrency: 4,
            CancellationToken.None);
        Assert.Equal(Program.ExitCodes.ProjectNotFound, exitCode);
    }

    #endregion

    #region CLI Entry Point

    [Fact]
    public async Task Main_EvalWithHelp_ReturnsSuccess()
    {
        var exitCode = await Program.Main(["eval", "--help"]);
        Assert.Equal(Program.ExitCodes.Success, exitCode);
    }

    #endregion
}
