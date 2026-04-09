using System;
using Microsoft.CodeAnalysis;

namespace LMP.SourceGen;

/// <summary>
/// Placeholder for the LMP source generator.
/// </summary>
[Generator]
public sealed class LmpSourceGenerator : IIncrementalGenerator
{
    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Phase 2 will implement the generator pipeline.
    }
}
