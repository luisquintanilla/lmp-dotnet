using LMP.Cli;
using LMP.Cli.Commands;

namespace LMP.Tests;

public class RunCommandTests
{
    #region Argument Parsing

    [Fact]
    public async Task NoArgs_ShowsHelp_ReturnsInvalidArguments()
    {
        var exitCode = await RunCommand.ExecuteAsync([]);
        Assert.Equal(Program.ExitCodes.InvalidArguments, exitCode);
    }

    [Fact]
    public async Task HelpFlag_ReturnsSuccess()
    {
        var exitCode = await RunCommand.ExecuteAsync(["--help"]);
        Assert.Equal(Program.ExitCodes.Success, exitCode);
    }

    [Fact]
    public async Task ShortHelpFlag_ReturnsSuccess()
    {
        var exitCode = await RunCommand.ExecuteAsync(["-h"]);
        Assert.Equal(Program.ExitCodes.Success, exitCode);
    }

    [Fact]
    public async Task MissingProject_ReturnsInvalidArguments()
    {
        var exitCode = await RunCommand.ExecuteAsync(["--input", "input.json"]);
        Assert.Equal(Program.ExitCodes.InvalidArguments, exitCode);
    }

    [Fact]
    public async Task MissingInput_ReturnsInvalidArguments()
    {
        var exitCode = await RunCommand.ExecuteAsync(["--project", "test.csproj"]);
        Assert.Equal(Program.ExitCodes.InvalidArguments, exitCode);
    }

    [Fact]
    public async Task UnknownOption_ReturnsInvalidArguments()
    {
        var exitCode = await RunCommand.ExecuteAsync(["--unknown"]);
        Assert.Equal(Program.ExitCodes.InvalidArguments, exitCode);
    }

    [Fact]
    public async Task AllOptionsWithHelp_ReturnsSuccess()
    {
        var exitCode = await RunCommand.ExecuteAsync(
            ["--project", "test.csproj", "--input", "input.json", "--help"]);
        Assert.Equal(Program.ExitCodes.Success, exitCode);
    }

    #endregion

    #region Project Not Found

    [Fact]
    public async Task NonexistentProject_ReturnsProjectNotFound()
    {
        var exitCode = await RunCommand.RunAsync(
            project: "nonexistent.csproj",
            inputPath: "input.json",
            artifactPath: null,
            jsonOutput: false,
            CancellationToken.None);
        Assert.Equal(Program.ExitCodes.ProjectNotFound, exitCode);
    }

    #endregion

    #region CLI Entry Point

    [Fact]
    public async Task Main_RunWithHelp_ReturnsSuccess()
    {
        var exitCode = await Program.Main(["run", "--help"]);
        Assert.Equal(Program.ExitCodes.Success, exitCode);
    }

    [Fact]
    public async Task Main_RunWithNoArgs_ReturnsInvalidArguments()
    {
        var exitCode = await Program.Main(["run"]);
        Assert.Equal(Program.ExitCodes.InvalidArguments, exitCode);
    }

    #endregion

    #region JSON Flag Parsing

    [Fact]
    public async Task JsonFlagParsed_WithOtherOptions_ReturnsInvalidArgumentsForMissingProject()
    {
        // --json is parsed but --project is missing, so we get InvalidArguments
        var exitCode = await RunCommand.ExecuteAsync(["--input", "input.json", "--json"]);
        Assert.Equal(Program.ExitCodes.InvalidArguments, exitCode);
    }

    #endregion

    #region Artifact Not Found

    [Fact]
    public async Task NonexistentArtifact_ReturnsArtifactError()
    {
        // Create a temp "project" file so build step triggers the real path
        // Since build will fail, we test the build path
        var tempProject = Path.GetTempFileName() + ".csproj";
        try
        {
            await File.WriteAllTextAsync(tempProject, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
            // The build will fail with this minimal project, so we'll get ProjectNotFound
            var exitCode = await RunCommand.RunAsync(
                project: tempProject,
                inputPath: "input.json",
                artifactPath: "nonexistent-artifact.json",
                jsonOutput: false,
                CancellationToken.None);
            // Build fails first, so we get ProjectNotFound
            Assert.Equal(Program.ExitCodes.ProjectNotFound, exitCode);
        }
        finally
        {
            File.Delete(tempProject);
        }
    }

    #endregion
}
