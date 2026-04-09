using LMP.Optimizers;

namespace LMP.Tests;

public class TraceAnalyzerTests
{
    #region ComputePosteriors

    [Fact]
    public void ComputePosteriors_KnownData_CorrectMeans()
    {
        var trials = new List<TrialResult>
        {
            new(new Dictionary<string, int> { ["x"] = 0 }, 0.8f),
            new(new Dictionary<string, int> { ["x"] = 0 }, 0.9f),
            new(new Dictionary<string, int> { ["x"] = 1 }, 0.2f),
            new(new Dictionary<string, int> { ["x"] = 1 }, 0.3f),
        };

        var cardinalities = new Dictionary<string, int> { ["x"] = 2 };
        var posteriors = TraceAnalyzer.ComputePosteriors(trials, cardinalities);

        Assert.Equal(2, posteriors["x"].Count);
        Assert.Equal(0.85, posteriors["x"][0].Mean, 0.01);
        Assert.Equal(0.25, posteriors["x"][1].Mean, 0.01);
    }

    [Fact]
    public void ComputePosteriors_SingleTrial_ZeroStdError()
    {
        var trials = new List<TrialResult>
        {
            new(new Dictionary<string, int> { ["x"] = 0 }, 0.7f),
        };

        var cardinalities = new Dictionary<string, int> { ["x"] = 3 };
        var posteriors = TraceAnalyzer.ComputePosteriors(trials, cardinalities);

        Assert.Single(posteriors["x"]); // Only value 0 observed
        Assert.Equal(0.7, posteriors["x"][0].Mean, 0.01);
        Assert.Equal(0, posteriors["x"][0].StandardError, 0.01);
        Assert.Equal(1, posteriors["x"][0].Count);
    }

    [Fact]
    public void ComputePosteriors_UnobservedValues_NotInResult()
    {
        var trials = new List<TrialResult>
        {
            new(new Dictionary<string, int> { ["x"] = 0 }, 0.5f),
        };

        var cardinalities = new Dictionary<string, int> { ["x"] = 3 };
        var posteriors = TraceAnalyzer.ComputePosteriors(trials, cardinalities);

        Assert.True(posteriors["x"].ContainsKey(0));
        Assert.False(posteriors["x"].ContainsKey(1));
        Assert.False(posteriors["x"].ContainsKey(2));
    }

    [Fact]
    public void ComputePosteriors_MultipleParams_AllComputed()
    {
        var trials = new List<TrialResult>
        {
            new(new Dictionary<string, int> { ["x"] = 0, ["y"] = 1 }, 0.8f),
            new(new Dictionary<string, int> { ["x"] = 1, ["y"] = 0 }, 0.4f),
        };

        var cardinalities = new Dictionary<string, int> { ["x"] = 2, ["y"] = 2 };
        var posteriors = TraceAnalyzer.ComputePosteriors(trials, cardinalities);

        Assert.Equal(2, posteriors.Count);
        Assert.True(posteriors.ContainsKey("x"));
        Assert.True(posteriors.ContainsKey("y"));
    }

    [Fact]
    public void ComputePosteriors_EqualScores_ZeroStdError()
    {
        var trials = new List<TrialResult>
        {
            new(new Dictionary<string, int> { ["x"] = 0 }, 0.5f),
            new(new Dictionary<string, int> { ["x"] = 0 }, 0.5f),
            new(new Dictionary<string, int> { ["x"] = 0 }, 0.5f),
        };

        var cardinalities = new Dictionary<string, int> { ["x"] = 2 };
        var posteriors = TraceAnalyzer.ComputePosteriors(trials, cardinalities);

        Assert.Equal(0, posteriors["x"][0].StandardError, 0.001);
    }

    [Fact]
    public void ComputePosteriors_NullArgs_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            TraceAnalyzer.ComputePosteriors(null!, new Dictionary<string, int>()));
        Assert.Throws<ArgumentNullException>(() =>
            TraceAnalyzer.ComputePosteriors([], null!));
    }

    #endregion

    #region DetectInteractions

    [Fact]
    public void DetectInteractions_IndependentParams_LowInteraction()
    {
        // x=0 always scores 0.8, x=1 always scores 0.2
        // y doesn't matter — no interaction expected
        var trials = new List<TrialResult>
        {
            new(new Dictionary<string, int> { ["x"] = 0, ["y"] = 0 }, 0.8f),
            new(new Dictionary<string, int> { ["x"] = 0, ["y"] = 1 }, 0.8f),
            new(new Dictionary<string, int> { ["x"] = 1, ["y"] = 0 }, 0.2f),
            new(new Dictionary<string, int> { ["x"] = 1, ["y"] = 1 }, 0.2f),
        };

        var interactions = TraceAnalyzer.DetectInteractions(trials);

        Assert.Single(interactions);
        Assert.True(interactions[("x", "y")] < 0.01,
            $"Independent params should have low interaction, got {interactions[("x", "y")]}");
    }

    [Fact]
    public void DetectInteractions_SynergyExists_HigherInteraction()
    {
        // Synergy: x=0,y=0 great; x=0,y=1 terrible; x=1,y=0 terrible; x=1,y=1 great
        var trials = new List<TrialResult>
        {
            new(new Dictionary<string, int> { ["x"] = 0, ["y"] = 0 }, 0.9f),
            new(new Dictionary<string, int> { ["x"] = 0, ["y"] = 1 }, 0.1f),
            new(new Dictionary<string, int> { ["x"] = 1, ["y"] = 0 }, 0.1f),
            new(new Dictionary<string, int> { ["x"] = 1, ["y"] = 1 }, 0.9f),
        };

        var interactions = TraceAnalyzer.DetectInteractions(trials);

        Assert.True(interactions[("x", "y")] > 0.1,
            $"Synergistic params should have high interaction, got {interactions[("x", "y")]}");
    }

    [Fact]
    public void DetectInteractions_TooFewTrials_ReturnsEmpty()
    {
        var trials = new List<TrialResult>
        {
            new(new Dictionary<string, int> { ["x"] = 0 }, 0.5f),
        };

        var interactions = TraceAnalyzer.DetectInteractions(trials);
        Assert.Empty(interactions);
    }

    [Fact]
    public void DetectInteractions_NullTrials_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            TraceAnalyzer.DetectInteractions(null!));
    }

    #endregion

    #region WarmStart

    [Fact]
    public void WarmStart_TransfersKnowledge_SamplerHasTrials()
    {
        var posteriors = new Dictionary<string, Dictionary<int, ParameterPosterior>>
        {
            ["x"] = new()
            {
                [0] = new ParameterPosterior(0.8, 0.02, 5),
                [1] = new ParameterPosterior(0.3, 0.05, 5),
            }
        };

        var sampler = new CategoricalTpeSampler(
            new Dictionary<string, int> { ["x"] = 2 }, seed: 42);

        Assert.Equal(0, sampler.TrialCount);

        TraceAnalyzer.WarmStart(sampler, posteriors, numSyntheticTrials: 3);

        // 2 values × 3 synthetic trials = 6 trials
        Assert.Equal(6, sampler.TrialCount);
    }

    [Fact]
    public void WarmStart_BiasesSampler_TowardGoodValues()
    {
        var posteriors = new Dictionary<string, Dictionary<int, ParameterPosterior>>
        {
            ["x"] = new()
            {
                [0] = new ParameterPosterior(0.9, 0.01, 10),
                [1] = new ParameterPosterior(0.1, 0.01, 10),
                [2] = new ParameterPosterior(0.1, 0.01, 10),
            }
        };

        var sampler = new CategoricalTpeSampler(
            new Dictionary<string, int> { ["x"] = 3 }, seed: 42);

        TraceAnalyzer.WarmStart(sampler, posteriors, numSyntheticTrials: 5);

        // After warm-start, category 0 should be strongly favored
        int[] counts = new int[3];
        for (int i = 0; i < 50; i++)
            counts[sampler.Propose()["x"]]++;

        Assert.True(counts[0] > counts[1],
            $"Warm-started sampler should favor category 0 (count={counts[0]}) over 1 (count={counts[1]})");
    }

    [Fact]
    public void WarmStart_NullArgs_Throws()
    {
        var posteriors = new Dictionary<string, Dictionary<int, ParameterPosterior>>();
        var sampler = new CategoricalTpeSampler(new Dictionary<string, int> { ["x"] = 2 });

        Assert.Throws<ArgumentNullException>(() =>
            TraceAnalyzer.WarmStart(null!, posteriors));
        Assert.Throws<ArgumentNullException>(() =>
            TraceAnalyzer.WarmStart(sampler, null!));
    }

    [Fact]
    public void WarmStart_InvalidTrialCount_Throws()
    {
        var posteriors = new Dictionary<string, Dictionary<int, ParameterPosterior>>();
        var sampler = new CategoricalTpeSampler(new Dictionary<string, int> { ["x"] = 2 });

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TraceAnalyzer.WarmStart(sampler, posteriors, numSyntheticTrials: 0));
    }

    #endregion
}
