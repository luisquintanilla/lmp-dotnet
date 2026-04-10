namespace LMP.Tests;

public class ISamplerDefaultMethodTests
{
    /// <summary>
    /// A minimal ISampler that only implements the required members (not the cost overload).
    /// Verifies the default interface method delegates to the non-cost Update.
    /// </summary>
    private sealed class SimpleSampler : ISampler
    {
        public int TrialCount { get; private set; }
        public Dictionary<string, int>? LastConfig { get; private set; }
        public float? LastScore { get; private set; }

        private readonly Dictionary<string, int> _cardinalities;
        private readonly Random _rng = new(42);

        public SimpleSampler(Dictionary<string, int> cardinalities)
        {
            _cardinalities = cardinalities;
        }

        public Dictionary<string, int> Propose()
        {
            var config = new Dictionary<string, int>();
            foreach (var (name, card) in _cardinalities)
                config[name] = _rng.Next(card);
            return config;
        }

        public void Update(Dictionary<string, int> config, float score)
        {
            LastConfig = config;
            LastScore = score;
            TrialCount++;
        }
    }

    [Fact]
    public void DefaultUpdateWithCost_DelegatesToScoreOnlyUpdate()
    {
        ISampler sampler = new SimpleSampler(new Dictionary<string, int> { ["x"] = 3 });
        var config = new Dictionary<string, int> { ["x"] = 1 };
        var cost = new TrialCost(1000, 600, 400, 250, 3);

        sampler.Update(config, 0.85f, cost);

        var simple = (SimpleSampler)sampler;
        Assert.Equal(1, simple.TrialCount);
        Assert.Equal(config, simple.LastConfig);
        Assert.Equal(0.85f, simple.LastScore);
    }

    [Fact]
    public void DefaultUpdateWithCost_MultipleCalls_AllDelegated()
    {
        ISampler sampler = new SimpleSampler(new Dictionary<string, int> { ["x"] = 5 });

        for (int i = 0; i < 10; i++)
        {
            var config = new Dictionary<string, int> { ["x"] = i % 5 };
            var cost = new TrialCost(100 * (i + 1), 60 * (i + 1), 40 * (i + 1), 25 * (i + 1), 1);
            sampler.Update(config, i * 0.1f, cost);
        }

        Assert.Equal(10, sampler.TrialCount);
    }

    [Fact]
    public void ScoreOnlyUpdate_StillWorks()
    {
        ISampler sampler = new SimpleSampler(new Dictionary<string, int> { ["x"] = 3 });
        var config = new Dictionary<string, int> { ["x"] = 2 };

        sampler.Update(config, 0.95f);

        Assert.Equal(1, sampler.TrialCount);
    }

    /// <summary>
    /// A sampler that overrides the cost-aware Update to use cost data.
    /// Verifies that custom implementations can access TrialCost.
    /// </summary>
    private sealed class CostTrackingSampler : ISampler
    {
        public int TrialCount { get; private set; }
        public TrialCost? LastCost { get; private set; }

        private readonly Dictionary<string, int> _cardinalities;

        public CostTrackingSampler(Dictionary<string, int> cardinalities)
        {
            _cardinalities = cardinalities;
        }

        public Dictionary<string, int> Propose()
        {
            var config = new Dictionary<string, int>();
            foreach (var (name, card) in _cardinalities)
                config[name] = 0;
            return config;
        }

        public void Update(Dictionary<string, int> config, float score)
        {
            TrialCount++;
        }

        public void Update(Dictionary<string, int> config, float score, TrialCost cost)
        {
            LastCost = cost;
            TrialCount++;
        }
    }

    [Fact]
    public void CustomCostAwareUpdate_ReceivesTrialCost()
    {
        ISampler sampler = new CostTrackingSampler(new Dictionary<string, int> { ["x"] = 3 });
        var config = new Dictionary<string, int> { ["x"] = 1 };
        var cost = new TrialCost(500, 300, 200, 100, 2);

        sampler.Update(config, 0.9f, cost);

        var tracking = (CostTrackingSampler)sampler;
        Assert.Equal(cost, tracking.LastCost);
        Assert.Equal(1, tracking.TrialCount);
    }

    [Fact]
    public void CustomCostAwareUpdate_DoesNotDelegateToScoreOnly()
    {
        var tracking = new CostTrackingSampler(new Dictionary<string, int> { ["x"] = 3 });
        ISampler sampler = tracking;
        var config = new Dictionary<string, int> { ["x"] = 1 };
        var cost = new TrialCost(500, 300, 200, 100, 2);

        // Call cost-aware Update then score-only Update — should be 2 calls total
        sampler.Update(config, 0.9f, cost);
        sampler.Update(config, 0.8f);

        Assert.Equal(2, tracking.TrialCount);
    }
}
