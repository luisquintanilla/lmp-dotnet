using LMP.Cli.Commands;

namespace LMP.Cli;

/// <summary>
/// Entry point for the <c>dotnet lmp</c> CLI tool.
/// Dispatches to subcommands: inspect, optimize, eval.
/// </summary>
public static class Program
{
    /// <summary>
    /// Exit codes following the CLI specification.
    /// </summary>
    internal static class ExitCodes
    {
        public const int Success = 0;
        public const int UnknownError = 1;
        public const int InvalidArguments = 2;
        public const int ProjectNotFound = 3;
        public const int CompilationFailed = 4;
        public const int EvaluationFailed = 5;
        public const int ArtifactError = 6;
        public const int InputParseError = 7;
    }

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return ExitCodes.InvalidArguments;
        }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "inspect" => await InspectCommand.ExecuteAsync(args[1..]),
                "optimize" => await OptimizeCommand.ExecuteAsync(args[1..]),
                "eval" => await EvalCommand.ExecuteAsync(args[1..]),
                "--help" or "-h" => PrintUsageAndReturn(),
                _ => PrintUnknownCommand(args[0])
            };
        }
        catch (OperationCanceledException)
        {
            await Console.Error.WriteLineAsync("Operation cancelled.");
            return ExitCodes.UnknownError;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"ERROR: {ex.Message}");
            return ExitCodes.UnknownError;
        }
    }

    private static int PrintUsageAndReturn()
    {
        PrintUsage();
        return ExitCodes.Success;
    }

    private static int PrintUnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command: '{command}'");
        Console.Error.WriteLine();
        PrintUsage();
        return ExitCodes.InvalidArguments;
    }

    internal static void PrintUsage()
    {
        Console.WriteLine("""
            LMP — Language Model Program CLI

            Usage: dotnet lmp <command> [options]

            Commands:
              inspect     Pretty-print saved module parameters
              optimize    Optimize a module via IOptimizer.CompileAsync
              eval        Evaluate a module against a dataset

            Options:
              --help, -h  Show this help message

            Run 'dotnet lmp <command> --help' for command-specific help.
            """);
    }
}
