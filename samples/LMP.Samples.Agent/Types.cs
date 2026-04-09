using System.ComponentModel;

namespace LMP.Samples.Agent;

/// <summary>
/// Input record for the research agent — the topic to investigate.
/// </summary>
/// <param name="Topic">The research topic to investigate.</param>
public record ResearchInput(
    [property: Description("The research topic to investigate")]
    string Topic);

/// <summary>
/// Output of the research agent — a structured report with summary,
/// key facts, and number of sources consulted.
/// </summary>
[LmpSignature("Research a topic and produce a summary report")]
public partial record ResearchReport
{
    /// <summary>A concise summary of the research findings.</summary>
    [Description("A concise summary of the research findings")]
    public required string Summary { get; init; }

    /// <summary>Key facts discovered during research.</summary>
    [Description("Key facts discovered during research")]
    public required string[] KeyFacts { get; init; }

    /// <summary>Number of sources consulted.</summary>
    [Description("Number of sources consulted")]
    public required int SourceCount { get; init; }
}
