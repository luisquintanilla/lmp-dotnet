using System.Text.Json;
using LMP.Cli.Infrastructure;

namespace LMP.Cli.Commands;

/// <summary>
/// Implements the <c>dotnet lmp run</c> command.
/// Builds a user project, discovers an <see cref="ILmpRunner"/>,
/// deserializes a single JSON input, optionally loads saved module state,
/// executes <see cref="LmpModule.ForwardAsync"/>, and prints the result.
/// </summary>
internal static class RunCommand
{
    public static async Task<int> ExecuteAsync(string[] args)
    {
        string? project = null;
        string? input = null;
        string? artifact = null;
        bool jsonOutput = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--project" when i + 1 < args.Length:
                    project = args[++i];
                    break;
                case "--input" when i + 1 < args.Length:
                    input = args[++i];
                    break;
                case "--artifact" when i + 1 < args.Length:
                    artifact = args[++i];
                    break;
                case "--json":
                    jsonOutput = true;
                    break;
                case "--help" or "-h":
                    PrintHelp();
                    return Program.ExitCodes.Success;
                default:
                    await Console.Error.WriteLineAsync($"Unknown option: '{args[i]}'");
                    PrintHelp();
                    return Program.ExitCodes.InvalidArguments;
            }
        }

        if (string.IsNullOrEmpty(project))
        {
            await Console.Error.WriteLineAsync("ERROR [args] Missing required option: --project <path>");
            PrintHelp();
            return Program.ExitCodes.InvalidArguments;
        }

        if (string.IsNullOrEmpty(input))
        {
            await Console.Error.WriteLineAsync("ERROR [args] Missing required option: --input <path>");
            PrintHelp();
            return Program.ExitCodes.InvalidArguments;
        }

        return await RunAsync(project, input, artifact, jsonOutput, CancellationToken.None);
    }

    internal static async Task<int> RunAsync(
        string project,
        string inputPath,
        string? artifactPath,
        bool jsonOutput,
        CancellationToken cancellationToken)
    {
        // Step 1: Build the project
        await Console.Error.WriteLineAsync($"Building project: {project}");
        var buildResult = await ProjectBuilder.BuildAsync(project, cancellationToken);

        if (!buildResult.Success)
        {
            await Console.Error.WriteLineAsync($"ERROR [project] Build failed:\n{buildResult.DiagnosticOutput}");
            return Program.ExitCodes.ProjectNotFound;
        }

        await Console.Error.WriteLineAsync($"Build succeeded: {buildResult.OutputAssembly}");

        // Step 2: Discover ILmpRunner
        var discovery = RunnerDiscovery.Discover(buildResult.OutputAssembly);
        if (discovery.Runner is null)
        {
            await Console.Error.WriteLineAsync($"ERROR [discovery] {discovery.Error}");
            return Program.ExitCodes.ProjectNotFound;
        }

        // Step 3: Load input JSON
        if (!File.Exists(inputPath))
        {
            await Console.Error.WriteLineAsync($"ERROR [input] Input file not found: {inputPath}");
            return Program.ExitCodes.InputParseError;
        }

        object inputObject;
        try
        {
            var inputJson = await File.ReadAllTextAsync(inputPath, cancellationToken);
            inputObject = discovery.Runner.DeserializeInput(inputJson);
        }
        catch (NotSupportedException ex)
        {
            await Console.Error.WriteLineAsync($"ERROR [input] {ex.Message}");
            return Program.ExitCodes.InputParseError;
        }
        catch (JsonException ex)
        {
            await Console.Error.WriteLineAsync($"ERROR [input] Failed to parse input JSON: {ex.Message}");
            return Program.ExitCodes.InputParseError;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"ERROR [input] Failed to load input: {ex.Message}");
            return Program.ExitCodes.InputParseError;
        }

        // Step 4: Create module
        LmpModule module;
        try
        {
            module = discovery.Runner.CreateModule();
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"ERROR [discovery] Failed to create module: {ex.Message}");
            return Program.ExitCodes.ProjectNotFound;
        }

        // Step 5: Load artifact state if provided
        if (!string.IsNullOrEmpty(artifactPath))
        {
            if (!File.Exists(artifactPath))
            {
                await Console.Error.WriteLineAsync($"ERROR [artifact] Artifact file not found: {artifactPath}");
                return Program.ExitCodes.ArtifactError;
            }

            try
            {
                await module.LoadAsync(artifactPath, cancellationToken);
                await Console.Error.WriteLineAsync($"Using artifact: {artifactPath}");
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"ERROR [artifact] Failed to load artifact: {ex.Message}");
                return Program.ExitCodes.ArtifactError;
            }
        }

        // Step 6: Execute
        object result;
        try
        {
            result = await module.ForwardAsync(inputObject, cancellationToken);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"ERROR [run] Execution failed: {ex.Message}");
            return Program.ExitCodes.EvaluationFailed;
        }

        // Step 7: Print result
        try
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = !jsonOutput,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };
            var json = JsonSerializer.Serialize(result, result.GetType(), options);
            Console.WriteLine(json);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"ERROR [run] Failed to serialize output: {ex.Message}");
            return Program.ExitCodes.UnknownError;
        }

        return Program.ExitCodes.Success;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            Usage: dotnet lmp run --project <path> --input <path> [options]

            Builds a project, discovers an ILmpRunner implementation, runs
            the module on a single input, and prints the result as JSON.

            Options:
              --project <path>     Required. Path to the .csproj file.
              --input <path>       Required. Path to a JSON file with the input object.
              --artifact <path>    Optional. Path to saved module state to load before running.
              --json               Emit compact (single-line) JSON output.
              --help, -h           Show this help message.
            """);
    }
}
