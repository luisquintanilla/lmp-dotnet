using System.Text.Json;

namespace LMP.Cli.Commands;

/// <summary>
/// Implements the <c>dotnet lmp inspect</c> command.
/// Reads a saved module state JSON file and pretty-prints its contents.
/// </summary>
internal static class InspectCommand
{
    public static async Task<int> ExecuteAsync(string[] args)
    {
        string? filePath = null;
        bool jsonOutput = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--file" when i + 1 < args.Length:
                    filePath = args[++i];
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

        if (string.IsNullOrEmpty(filePath))
        {
            await Console.Error.WriteLineAsync("ERROR [args] Missing required option: --file <path>");
            PrintHelp();
            return Program.ExitCodes.InvalidArguments;
        }

        return await InspectAsync(filePath, jsonOutput);
    }

    internal static async Task<int> InspectAsync(string filePath, bool jsonOutput)
    {
        if (!File.Exists(filePath))
        {
            await Console.Error.WriteLineAsync($"ERROR [artifact] File not found: {filePath}");
            return Program.ExitCodes.ArtifactError;
        }

        ModuleState state;
        try
        {
            var bytes = await File.ReadAllBytesAsync(filePath);
            state = JsonSerializer.Deserialize(bytes, ModuleStateSerializerContext.Default.ModuleState)
                ?? throw new JsonException("Deserialized to null.");
        }
        catch (JsonException ex)
        {
            await Console.Error.WriteLineAsync($"ERROR [artifact] Invalid module state JSON: {ex.Message}");
            return Program.ExitCodes.ArtifactError;
        }

        if (jsonOutput)
        {
            PrintJson(state);
        }
        else
        {
            PrintFormatted(state, filePath);
        }

        return Program.ExitCodes.Success;
    }

    private static void PrintFormatted(ModuleState state, string filePath)
    {
        Console.WriteLine($"Module State: {state.Module}");
        Console.WriteLine(new string('═', 50));
        Console.WriteLine($"  File           : {filePath}");
        Console.WriteLine($"  Schema version : {state.Version}");
        Console.WriteLine($"  Predictors     : {state.Predictors.Count}");
        Console.WriteLine();

        foreach (var (name, predictor) in state.Predictors)
        {
            Console.WriteLine($"  Predictor: {name}");
            Console.WriteLine($"  ────────────────────────────────");

            var instruction = predictor.Instructions;
            if (instruction.Length > 120)
                instruction = string.Concat(instruction.AsSpan(0, 117), "...");
            Console.WriteLine($"    Instructions : {instruction}");
            Console.WriteLine($"    Demos        : {predictor.Demos.Count}");

            for (int i = 0; i < predictor.Demos.Count; i++)
            {
                var demo = predictor.Demos[i];
                Console.WriteLine($"      Demo {i + 1}:");
                Console.WriteLine($"        Input  : {FormatDemoFields(demo.Input)}");
                Console.WriteLine($"        Output : {FormatDemoFields(demo.Output)}");
            }

            if (predictor.Config is { Count: > 0 })
            {
                Console.WriteLine($"    Config:");
                foreach (var (key, value) in predictor.Config)
                {
                    Console.WriteLine($"      {key} = {value}");
                }
            }

            Console.WriteLine();
        }
    }

    private static void PrintJson(ModuleState state)
    {
        var json = JsonSerializer.Serialize(
            state,
            ModuleStateSerializerContext.Default.ModuleState);
        Console.WriteLine(json);
    }

    internal static string FormatDemoFields(Dictionary<string, JsonElement> fields)
    {
        if (fields.Count == 0)
            return "{}";

        var parts = fields.Select(kv =>
        {
            var valueStr = kv.Value.ToString();
            if (valueStr.Length > 60)
                valueStr = string.Concat(valueStr.AsSpan(0, 57), "...");
            return $"{kv.Key}: {valueStr}";
        });

        var result = "{ " + string.Join(", ", parts) + " }";
        return result;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            Usage: dotnet lmp inspect --file <path> [--json]

            Pretty-prints the contents of a saved module state JSON file.

            Options:
              --file <path>   Required. Path to the saved module state JSON file.
              --json          Output raw JSON instead of formatted text.
              --help, -h      Show this help message.
            """);
    }
}
