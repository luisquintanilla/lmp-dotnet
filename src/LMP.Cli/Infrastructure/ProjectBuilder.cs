using System.Diagnostics;

namespace LMP.Cli.Infrastructure;

/// <summary>
/// Builds a .NET project by shelling out to <c>dotnet build</c>
/// and locates the output assembly.
/// </summary>
internal static class ProjectBuilder
{
    /// <summary>
    /// Result of building a .NET project.
    /// </summary>
    /// <param name="Success">Whether the build succeeded.</param>
    /// <param name="OutputAssembly">Full path to the output DLL.</param>
    /// <param name="DiagnosticOutput">Build stdout + stderr for error reporting.</param>
    internal sealed record BuildResult(bool Success, string OutputAssembly, string DiagnosticOutput);

    /// <summary>
    /// Builds the specified project and returns the path to the output assembly.
    /// </summary>
    /// <param name="projectPath">Path to the .csproj file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="BuildResult"/> with the output assembly path or diagnostic output.</returns>
    public static async Task<BuildResult> BuildAsync(string projectPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(projectPath))
            return new BuildResult(false, string.Empty, $"Project file not found: {projectPath}");

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"build \"{projectPath}\" --nologo -v quiet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start dotnet build process.");

        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
            return new BuildResult(false, string.Empty, $"{stdout}\n{stderr}".Trim());

        // Locate the output DLL by convention: look for <ProjectName>.dll in bin/
        var projectDir = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        var projectName = Path.GetFileNameWithoutExtension(projectPath);
        var outputDll = FindOutputAssembly(projectDir, projectName);

        return outputDll is not null
            ? new BuildResult(true, outputDll, string.Empty)
            : new BuildResult(false, string.Empty, $"Build succeeded but could not find output assembly for '{projectName}'.");
    }

    private static string? FindOutputAssembly(string projectDir, string projectName)
    {
        // Look in standard output paths: bin/Debug/net10.0/ or bin/Release/net10.0/
        var candidates = new[]
        {
            Path.Combine(projectDir, "bin", "Debug", "net10.0", $"{projectName}.dll"),
            Path.Combine(projectDir, "bin", "Release", "net10.0", $"{projectName}.dll"),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        // Fallback: search recursively in bin/ for the DLL
        var binDir = Path.Combine(projectDir, "bin");
        if (Directory.Exists(binDir))
        {
            var found = Directory.GetFiles(binDir, $"{projectName}.dll", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (found is not null)
                return found;
        }

        return null;
    }
}
