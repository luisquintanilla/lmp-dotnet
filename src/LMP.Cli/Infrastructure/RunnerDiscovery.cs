using System.Reflection;
using System.Runtime.Loader;

namespace LMP.Cli.Infrastructure;

/// <summary>
/// Discovers <see cref="ILmpRunner"/> implementations in a built assembly
/// using a collectible <see cref="AssemblyLoadContext"/>.
/// </summary>
internal static class RunnerDiscovery
{
    /// <summary>
    /// Result of runner discovery.
    /// </summary>
    /// <param name="Runner">The discovered runner instance, or null if not found.</param>
    /// <param name="Error">Error message if discovery failed.</param>
    internal sealed record DiscoveryResult(ILmpRunner? Runner, string? Error);

    /// <summary>
    /// Loads the specified assembly and discovers the first <see cref="ILmpRunner"/> implementation.
    /// Instantiates it via parameterless constructor.
    /// </summary>
    /// <param name="assemblyPath">Full path to the assembly DLL.</param>
    /// <returns>A <see cref="DiscoveryResult"/> with the runner or an error.</returns>
    public static DiscoveryResult Discover(string assemblyPath)
    {
        if (!File.Exists(assemblyPath))
            return new DiscoveryResult(null, $"Assembly not found: {assemblyPath}");

        try
        {
            var context = new LmpAssemblyLoadContext(assemblyPath);
            var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));

            var runnerType = assembly.GetTypes()
                .FirstOrDefault(t =>
                    !t.IsAbstract &&
                    !t.IsInterface &&
                    typeof(ILmpRunner).IsAssignableFrom(t));

            if (runnerType is null)
            {
                return new DiscoveryResult(null,
                    $"No ILmpRunner implementation found in '{Path.GetFileName(assemblyPath)}'. " +
                    "Ensure your project has a public class implementing ILmpRunner.");
            }

            var runner = (ILmpRunner?)Activator.CreateInstance(runnerType);
            if (runner is null)
            {
                return new DiscoveryResult(null,
                    $"Failed to instantiate '{runnerType.FullName}'. " +
                    "Ensure it has a public parameterless constructor.");
            }

            return new DiscoveryResult(runner, null);
        }
        catch (ReflectionTypeLoadException ex)
        {
            var loaderErrors = string.Join("; ",
                ex.LoaderExceptions?.Select(e => e?.Message ?? "unknown") ?? []);
            return new DiscoveryResult(null,
                $"Failed to load types from assembly: {loaderErrors}");
        }
        catch (Exception ex)
        {
            return new DiscoveryResult(null, $"Failed to load assembly: {ex.Message}");
        }
    }

    /// <summary>
    /// Custom AssemblyLoadContext that resolves dependencies from the same directory as the target assembly.
    /// </summary>
    private sealed class LmpAssemblyLoadContext : AssemblyLoadContext
    {
        private readonly string _basePath;

        public LmpAssemblyLoadContext(string assemblyPath)
            : base(isCollectible: false) // non-collectible for simplicity
        {
            _basePath = Path.GetDirectoryName(Path.GetFullPath(assemblyPath))!;
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // Try to resolve from the same directory as the target assembly
            var candidatePath = Path.Combine(_basePath, $"{assemblyName.Name}.dll");
            if (File.Exists(candidatePath))
                return LoadFromAssemblyPath(candidatePath);

            // Fall back to default resolution
            return null;
        }
    }
}
