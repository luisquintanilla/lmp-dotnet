using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using LMP;
using LMP.Aspire.Hosting;
using LMP.Optimizers;

// Extensions in Aspire.Hosting namespace for discoverability.
namespace Aspire.Hosting;

/// <summary>
/// Extension methods for adding LMP optimizer resources to the Aspire app model.
/// </summary>
public static class LmpOptimizerResourceBuilderExtensions
{
    /// <summary>
    /// Adds an LMP optimizer resource for the specified module type.
    /// The resource appears in the Aspire dashboard and emits OpenTelemetry
    /// traces and metrics during optimization runs.
    /// </summary>
    /// <typeparam name="TModule">The <see cref="LmpModule"/> type to optimize.</typeparam>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The resource name shown in the Aspire dashboard.</param>
    /// <returns>A resource builder for further configuration.</returns>
    /// <example>
    /// <code>
    /// var builder = DistributedApplication.CreateBuilder(args);
    /// builder.AddLmpOptimizer&lt;TicketTriageModule&gt;("triage-optimizer")
    ///     .WithTrainData("data/train.jsonl")
    ///     .WithDevData("data/dev.jsonl");
    /// </code>
    /// </example>
    public static IResourceBuilder<LmpOptimizerResource> AddLmpOptimizer<TModule>(
        this IDistributedApplicationBuilder builder,
        string name)
        where TModule : LmpModule
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var resource = new LmpOptimizerResource(name, typeof(TModule));
        return builder.AddResource(resource);
    }

    /// <summary>
    /// Sets the path to the training data JSONL file for optimization.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="path">Path to the training data file.</param>
    /// <returns>The resource builder for chaining.</returns>
    public static IResourceBuilder<LmpOptimizerResource> WithTrainData(
        this IResourceBuilder<LmpOptimizerResource> builder,
        string path)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        builder.Resource.TrainDataPath = path;
        return builder;
    }

    /// <summary>
    /// Sets the path to the development/validation data JSONL file for evaluation.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="path">Path to the development data file.</param>
    /// <returns>The resource builder for chaining.</returns>
    public static IResourceBuilder<LmpOptimizerResource> WithDevData(
        this IResourceBuilder<LmpOptimizerResource> builder,
        string path)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        builder.Resource.DevDataPath = path;
        return builder;
    }

    /// <summary>
    /// Specifies the optimizer type to use for this optimization resource.
    /// </summary>
    /// <typeparam name="TOptimizer">
    /// The <see cref="IOptimizer"/> implementation (e.g., <see cref="BootstrapFewShot"/>,
    /// <see cref="BootstrapRandomSearch"/>, <see cref="MIPROv2"/>).
    /// </typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <returns>The resource builder for chaining.</returns>
    public static IResourceBuilder<LmpOptimizerResource> WithOptimizer<TOptimizer>(
        this IResourceBuilder<LmpOptimizerResource> builder)
        where TOptimizer : IOptimizer
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Resource.OptimizerType = typeof(TOptimizer);
        return builder;
    }

    /// <summary>
    /// Sets the output path for the optimized artifact JSON file.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="path">Path where the optimized parameters will be saved.</param>
    /// <returns>The resource builder for chaining.</returns>
    public static IResourceBuilder<LmpOptimizerResource> WithOutputPath(
        this IResourceBuilder<LmpOptimizerResource> builder,
        string path)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        builder.Resource.OutputPath = path;
        return builder;
    }

    /// <summary>
    /// Sets the maximum concurrency for parallel evaluation during optimization.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="maxConcurrency">Maximum number of concurrent evaluations.</param>
    /// <returns>The resource builder for chaining.</returns>
    public static IResourceBuilder<LmpOptimizerResource> WithMaxConcurrency(
        this IResourceBuilder<LmpOptimizerResource> builder,
        int maxConcurrency)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrency, 1);

        builder.Resource.MaxConcurrency = maxConcurrency;
        return builder;
    }
}
