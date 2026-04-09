using System.Text.Json;
using LMP.Cli;
using LMP.Cli.Commands;

namespace LMP.Tests;

public class InspectCommandTests : IDisposable
{
    private readonly string _tempDir;

    public InspectCommandTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lmp-cli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string CreateStateFile(ModuleState state)
    {
        var path = Path.Combine(_tempDir, "test-state.json");
        var json = JsonSerializer.Serialize(state, ModuleStateSerializerContext.Default.ModuleState);
        File.WriteAllText(path, json);
        return path;
    }

    private static ModuleState CreateTestState(
        string moduleName = "TestModule",
        int predictorCount = 1,
        int demoCount = 0)
    {
        var predictors = new Dictionary<string, PredictorState>();
        for (int i = 0; i < predictorCount; i++)
        {
            var demos = new List<DemoEntry>();
            for (int j = 0; j < demoCount; j++)
            {
                demos.Add(new DemoEntry
                {
                    Input = new Dictionary<string, JsonElement>
                    {
                        ["text"] = JsonSerializer.SerializeToElement($"input_{j}")
                    },
                    Output = new Dictionary<string, JsonElement>
                    {
                        ["label"] = JsonSerializer.SerializeToElement($"output_{j}")
                    }
                });
            }

            predictors[$"predictor_{i}"] = new PredictorState
            {
                Instructions = $"Classify the input for predictor {i}",
                Demos = demos,
                Config = null
            };
        }

        return new ModuleState
        {
            Version = "1.0",
            Module = moduleName,
            Predictors = predictors
        };
    }

    #region Argument Parsing

    [Fact]
    public async Task NoArgs_ShowsHelp_ReturnsInvalidArguments()
    {
        var exitCode = await InspectCommand.ExecuteAsync([]);
        Assert.Equal(Program.ExitCodes.InvalidArguments, exitCode);
    }

    [Fact]
    public async Task HelpFlag_ReturnsSuccess()
    {
        var exitCode = await InspectCommand.ExecuteAsync(["--help"]);
        Assert.Equal(Program.ExitCodes.Success, exitCode);
    }

    [Fact]
    public async Task UnknownOption_ReturnsInvalidArguments()
    {
        var exitCode = await InspectCommand.ExecuteAsync(["--unknown"]);
        Assert.Equal(Program.ExitCodes.InvalidArguments, exitCode);
    }

    [Fact]
    public async Task MissingFileValue_ReturnsInvalidArguments()
    {
        var exitCode = await InspectCommand.ExecuteAsync(["--file"]);
        Assert.Equal(Program.ExitCodes.InvalidArguments, exitCode);
    }

    #endregion

    #region File Not Found

    [Fact]
    public async Task NonexistentFile_ReturnsArtifactError()
    {
        var exitCode = await InspectCommand.InspectAsync(
            Path.Combine(_tempDir, "nonexistent.json"), jsonOutput: false);
        Assert.Equal(Program.ExitCodes.ArtifactError, exitCode);
    }

    #endregion

    #region Invalid JSON

    [Fact]
    public async Task InvalidJson_ReturnsArtifactError()
    {
        var path = Path.Combine(_tempDir, "bad.json");
        await File.WriteAllTextAsync(path, "not valid json {{{");

        var exitCode = await InspectCommand.InspectAsync(path, jsonOutput: false);
        Assert.Equal(Program.ExitCodes.ArtifactError, exitCode);
    }

    [Fact]
    public async Task EmptyFile_ReturnsArtifactError()
    {
        var path = Path.Combine(_tempDir, "empty.json");
        await File.WriteAllTextAsync(path, "");

        var exitCode = await InspectCommand.InspectAsync(path, jsonOutput: false);
        Assert.Equal(Program.ExitCodes.ArtifactError, exitCode);
    }

    #endregion

    #region Successful Inspection

    [Fact]
    public async Task ValidState_FormattedOutput_ReturnsSuccess()
    {
        var state = CreateTestState("MyModule", predictorCount: 2, demoCount: 1);
        var path = CreateStateFile(state);

        var exitCode = await InspectCommand.InspectAsync(path, jsonOutput: false);
        Assert.Equal(Program.ExitCodes.Success, exitCode);
    }

    [Fact]
    public async Task ValidState_JsonOutput_ReturnsSuccess()
    {
        var state = CreateTestState("MyModule", predictorCount: 1, demoCount: 2);
        var path = CreateStateFile(state);

        var exitCode = await InspectCommand.InspectAsync(path, jsonOutput: true);
        Assert.Equal(Program.ExitCodes.Success, exitCode);
    }

    [Fact]
    public async Task ValidState_NoDemos_ReturnsSuccess()
    {
        var state = CreateTestState("EmptyModule", predictorCount: 3, demoCount: 0);
        var path = CreateStateFile(state);

        var exitCode = await InspectCommand.InspectAsync(path, jsonOutput: false);
        Assert.Equal(Program.ExitCodes.Success, exitCode);
    }

    [Fact]
    public async Task ValidState_WithConfig_ReturnsSuccess()
    {
        var state = new ModuleState
        {
            Version = "1.0",
            Module = "ConfigModule",
            Predictors = new Dictionary<string, PredictorState>
            {
                ["pred"] = new PredictorState
                {
                    Instructions = "Do the thing",
                    Demos = [],
                    Config = new Dictionary<string, JsonElement>
                    {
                        ["temperature"] = JsonSerializer.SerializeToElement(0.7),
                        ["maxTokens"] = JsonSerializer.SerializeToElement(256)
                    }
                }
            }
        };
        var path = CreateStateFile(state);

        var exitCode = await InspectCommand.InspectAsync(path, jsonOutput: false);
        Assert.Equal(Program.ExitCodes.Success, exitCode);
    }

    #endregion

    #region FormatDemoFields

    [Fact]
    public void FormatDemoFields_EmptyDict_ReturnsBraces()
    {
        var result = InspectCommand.FormatDemoFields([]);
        Assert.Equal("{}", result);
    }

    [Fact]
    public void FormatDemoFields_SingleField_FormatsCorrectly()
    {
        var fields = new Dictionary<string, JsonElement>
        {
            ["name"] = JsonSerializer.SerializeToElement("Alice")
        };

        var result = InspectCommand.FormatDemoFields(fields);
        Assert.Contains("name:", result);
        Assert.Contains("Alice", result);
    }

    [Fact]
    public void FormatDemoFields_LongValue_Truncated()
    {
        var longValue = new string('x', 100);
        var fields = new Dictionary<string, JsonElement>
        {
            ["data"] = JsonSerializer.SerializeToElement(longValue)
        };

        var result = InspectCommand.FormatDemoFields(fields);
        Assert.Contains("...", result);
    }

    #endregion

    #region Full CLI Entry Point

    [Fact]
    public async Task Main_InspectWithValidFile_ReturnsSuccess()
    {
        var state = CreateTestState();
        var path = CreateStateFile(state);

        var exitCode = await Program.Main(["inspect", "--file", path]);
        Assert.Equal(Program.ExitCodes.Success, exitCode);
    }

    [Fact]
    public async Task Main_NoArgs_ReturnsInvalidArguments()
    {
        var exitCode = await Program.Main([]);
        Assert.Equal(Program.ExitCodes.InvalidArguments, exitCode);
    }

    [Fact]
    public async Task Main_UnknownCommand_ReturnsInvalidArguments()
    {
        var exitCode = await Program.Main(["notacommand"]);
        Assert.Equal(Program.ExitCodes.InvalidArguments, exitCode);
    }

    [Fact]
    public async Task Main_Help_ReturnsSuccess()
    {
        var exitCode = await Program.Main(["--help"]);
        Assert.Equal(Program.ExitCodes.Success, exitCode);
    }

    [Fact]
    public async Task Main_Version_ReturnsSuccess()
    {
        var exitCode = await Program.Main(["--version"]);
        Assert.Equal(Program.ExitCodes.Success, exitCode);
    }

    #endregion
}
