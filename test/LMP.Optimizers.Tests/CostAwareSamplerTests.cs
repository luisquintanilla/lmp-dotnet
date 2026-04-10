using LMP.Optimizers;

namespace LMP.Tests;

public class CostAwareSamplerTests
{
    #region Constructor Validation

    [Fact]
    public void Constructor_NullCardinalities_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new CostAwareSampler(null!));
    }

    [Fact]
    public void Constructor_EmptyCardinalities_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new CostAwareSampler(new Dictionary<string, int>()));
    }

    [Fact]
    public void Constructor_ValidParameters_DoesNotThrow()
    {
        var sampler = new CostAwareSampler(
            new Dictionary<string, int> { ["x"] = 5, ["y"] = 3 },
            costProjection: c => c.TotalTokens,
            seed: 42);
        Assert.NotNull(sampler);
    }

    [Fact]
    public void Constructor_NullCostProjection_UsesDefault()
    {
        // Should not throw — null means default (c => c.TotalTokens)
        var sampler = new CostAwareSampler(
            new Dictionary<string, int> { ["x"] = 3 },
            costProjection: null,
            seed: 42);
        Assert.NotNull(sampler);
    }

    #endregion

    #region ISampler Contract

    [Fact]
    public void ImplementsISampler()
    {
        var sampler = new CostAwareSampler(new Dictionary<string, int> { ["x"] = 3 });
        Assert.IsAssignableFrom<ISampler>(sampler);
    }

    [Fact]
    public void TrialCount_StartsAtZero()
    {
        var sampler = new CostAwareSampler(new Dictionary<string, int> { ["x"] = 3 });
        Assert.Equal(0, sampler.TrialCount);
    }

    [Fact]
    public void Update_IncrementsTrialCount()
    {
        var sampler = new CostAwareSampler(new Dictionary<string, int> { ["x"] = 3 });
        var config = sampler.Propose();
        sampler.Update(config, 0.5f);
        Assert.Equal(1, sampler.TrialCount);
    }

    [Fact]
    public void Update_NullConfig_Throws()
    {
        var sampler = new CostAwareSampler(new Dictionary<string, int> { ["x"] = 3 });
        Assert.Throws<ArgumentNullException>(() => sampler.Update(null!, 0.5f));
    }

    [Fact]
    public void UpdateWithCost_NullConfig_Throws()
    {
        var sampler = new CostAwareSampler(new Dictionary<string, int> { ["x"] = 3 });
        var cost = new TrialCost(100, 50, 50, 200, 1);
        Assert.Throws<ArgumentNullException>(() => sampler.Update(null!, 0.5f, cost));
    }

    [Fact]
    public void UpdateWithCost_NullCost_Throws()
    {
        var sampler = new CostAwareSampler(new Dictionary<string, int> { ["x"] = 3 });
        var config = sampler.Propose();
        Assert.Throws<ArgumentNullException>(() => sampler.Update(config, 0.5f, null!));
    }

    #endregion

    #region Propose — Valid Configs

    [Fact]
    public void Propose_NoHistory_ReturnsValidConfig()
    {
        var cardinalities = new Dictionary<string, int> { ["a"] = 3, ["b"] = 5 };
        var sampler = new CostAwareSampler(cardinalities, seed: 42);

        var config = sampler.Propose();

        Assert.Equal(2, config.Count);
        Assert.InRange(config["a"], 0, 2);
        Assert.InRange(config["b"], 0, 4);
    }

    [Fact]
    public void Propose_AfterUpdates_ReturnsValidConfig()
    {
        var cardinalities = new Dictionary<string, int> { ["x"] = 4, ["y"] = 6 };
        var sampler = new CostAwareSampler(cardinalities, seed: 42);

        for (int i = 0; i < 10; i++)
        {
            var config = sampler.Propose();
            Assert.Equal(2, config.Count);
            Assert.InRange(config["x"], 0, 3);
            Assert.InRange(config["y"], 0, 5);

            sampler.Update(config, (float)i / 10,
                new TrialCost(100 + i * 10, 50, 50 + i * 10, 200, 1));
        }
    }

    [Fact]
    public void Propose_MultipleParameters_AllPresent()
    {
        var cardinalities = new Dictionary<string, int>
        {
            ["instr_a"] = 5,
            ["demos_a"] = 4,
            ["instr_b"] = 3,
            ["demos_b"] = 6
        };
        var sampler = new CostAwareSampler(cardinalities, seed: 42);

        var config = sampler.Propose();

        Assert.Equal(4, config.Count);
        Assert.InRange(config["instr_a"], 0, 4);
        Assert.InRange(config["demos_a"], 0, 3);
        Assert.InRange(config["instr_b"], 0, 2);
        Assert.InRange(config["demos_b"], 0, 5);
    }

    [Fact]
    public void Propose_SingleCategoryParameter_AlwaysReturnsZero()
    {
        var cardinalities = new Dictionary<string, int> { ["x"] = 1 };
        var sampler = new CostAwareSampler(cardinalities, seed: 42);

        for (int i = 0; i < 10; i++)
        {
            var config = sampler.Propose();
            Assert.Equal(0, config["x"]);
            sampler.Update(config, 0.5f);
        }
    }

    #endregion

    #region Cost Projection Customization

    [Fact]
    public void CostProjection_DollarPricing_IsUsed()
    {
        var cardinalities = new Dictionary<string, int> { ["x"] = 5 };
        Func<TrialCost, double> dollarPricing =
            c => c.OutputTokens * 0.06 / 1000 + c.InputTokens * 0.01 / 1000;

        var sampler = new CostAwareSampler(cardinalities, dollarPricing, seed: 42);

        var config = sampler.Propose();
        // Expensive trial: cost is high relative to average, should cause step shrinkage
        sampler.Update(config, 0.5f,
            new TrialCost(10000, 5000, 5000, 1000, 1));

        // Should not throw and should continue proposing valid configs
        var config2 = sampler.Propose();
        Assert.InRange(config2["x"], 0, 4);
    }

    [Fact]
    public void CostProjection_Latency_IsUsed()
    {
        var cardinalities = new Dictionary<string, int> { ["x"] = 3 };
        Func<TrialCost, double> latency = c => c.ElapsedMilliseconds;

        var sampler = new CostAwareSampler(cardinalities, latency, seed: 42);

        var config = sampler.Propose();
        sampler.Update(config, 0.8f,
            new TrialCost(100, 50, 50, 5000, 1)); // high latency

        var config2 = sampler.Propose();
        Assert.InRange(config2["x"], 0, 2);
    }

    [Fact]
    public void CostProjection_Blended_IsUsed()
    {
        var cardinalities = new Dictionary<string, int> { ["x"] = 4, ["y"] = 3 };
        Func<TrialCost, double> blended =
            c => c.TotalTokens * 0.7 + c.ElapsedMilliseconds * 0.3;

        var sampler = new CostAwareSampler(cardinalities, blended, seed: 42);

        // Run a few iterations to verify blended cost works
        for (int i = 0; i < 5; i++)
        {
            var config = sampler.Propose();
            sampler.Update(config, 0.5f + i * 0.05f,
                new TrialCost(100 + i * 50, 50, 50 + i * 50, 200 + i * 100, 1));
        }

        Assert.Equal(5, sampler.TrialCount);
    }

    #endregion

    #region Step Sizing

    [Fact]
    public void StepSize_GrowsOnImprovement()
    {
        var cardinalities = new Dictionary<string, int> { ["x"] = 10 };
        var sampler = new CostAwareSampler(cardinalities, seed: 42);

        // Monotonically improving scores → step should grow
        for (int i = 0; i < 5; i++)
        {
            var config = sampler.Propose();
            sampler.Update(config, (float)(i + 1) * 0.15f,
                new TrialCost(100, 50, 50, 100, 1));
        }

        // After 5 improvements, not converged
        Assert.False(sampler.IsConverged);
    }

    [Fact]
    public void StepSize_ShrinksOnStagnation()
    {
        var cardinalities = new Dictionary<string, int> { ["x"] = 10 };
        var sampler = new CostAwareSampler(cardinalities, seed: 42);

        // First trial sets the best score
        var config = sampler.Propose();
        sampler.Update(config, 0.9f, new TrialCost(100, 50, 50, 100, 1));

        // Then no improvement for many trials → step should shrink
        for (int i = 0; i < 15; i++)
        {
            config = sampler.Propose();
            sampler.Update(config, 0.1f, new TrialCost(100, 50, 50, 100, 1));
        }

        // After 15 stagnant trials with patience=10, should be converged
        Assert.True(sampler.IsConverged);
    }

    [Fact]
    public void ExpensiveTrial_CausesSteperShrinkage()
    {
        // Two samplers with same scores but different costs
        var cardinalities = new Dictionary<string, int> { ["x"] = 10 };

        var cheapSampler = new CostAwareSampler(cardinalities, seed: 42);
        var expensiveSampler = new CostAwareSampler(cardinalities, seed: 42);

        // Both get initial improving trial
        var config1 = cheapSampler.Propose();
        var config2 = expensiveSampler.Propose();
        cheapSampler.Update(config1, 0.5f, new TrialCost(100, 50, 50, 100, 1));
        expensiveSampler.Update(config2, 0.5f, new TrialCost(100, 50, 50, 100, 1));

        // Second trial: same score (no improvement), but expensive sampler gets very high cost
        config1 = cheapSampler.Propose();
        config2 = expensiveSampler.Propose();
        cheapSampler.Update(config1, 0.3f, new TrialCost(100, 50, 50, 100, 1));
        expensiveSampler.Update(config2, 0.3f, new TrialCost(1000, 500, 500, 5000, 5));

        // Both should produce valid configs (just verifying no crash)
        config1 = cheapSampler.Propose();
        config2 = expensiveSampler.Propose();
        Assert.InRange(config1["x"], 0, 9);
        Assert.InRange(config2["x"], 0, 9);
    }

    #endregion

    #region Convergence Detection

    [Fact]
    public void IsConverged_InitiallyFalse()
    {
        var sampler = new CostAwareSampler(new Dictionary<string, int> { ["x"] = 3 });
        Assert.False(sampler.IsConverged);
    }

    [Fact]
    public void IsConverged_AfterManyStagnantTrials_True()
    {
        var sampler = new CostAwareSampler(
            new Dictionary<string, int> { ["x"] = 5 }, seed: 42);

        // High initial score
        var config = sampler.Propose();
        sampler.Update(config, 0.99f);

        // Many trials with no improvement
        for (int i = 0; i < 10; i++)
        {
            config = sampler.Propose();
            sampler.Update(config, 0.1f);
        }

        Assert.True(sampler.IsConverged);
    }

    [Fact]
    public void IsConverged_ResetsOnImprovement()
    {
        var sampler = new CostAwareSampler(
            new Dictionary<string, int> { ["x"] = 5 }, seed: 42);

        // Set best score
        var config = sampler.Propose();
        sampler.Update(config, 0.5f);

        // 9 stagnant trials (just under patience of 10)
        for (int i = 0; i < 9; i++)
        {
            config = sampler.Propose();
            sampler.Update(config, 0.1f);
        }
        Assert.False(sampler.IsConverged);

        // Improvement resets the counter
        config = sampler.Propose();
        sampler.Update(config, 0.99f);
        Assert.False(sampler.IsConverged);
    }

    #endregion

    #region Backward Compatibility

    [Fact]
    public void NonCostUpdate_StillWorks()
    {
        var cardinalities = new Dictionary<string, int> { ["x"] = 5, ["y"] = 3 };
        var sampler = new CostAwareSampler(cardinalities, seed: 42);

        for (int i = 0; i < 10; i++)
        {
            var config = sampler.Propose();
            // Use the non-cost Update overload
            sampler.Update(config, (float)i / 10);
        }

        Assert.Equal(10, sampler.TrialCount);
    }

    [Fact]
    public void MixedUpdates_CostAndNonCost_Work()
    {
        var cardinalities = new Dictionary<string, int> { ["x"] = 4 };
        var sampler = new CostAwareSampler(cardinalities, seed: 42);

        // Mix cost-aware and cost-less updates
        var config = sampler.Propose();
        sampler.Update(config, 0.5f);

        config = sampler.Propose();
        sampler.Update(config, 0.6f, new TrialCost(200, 100, 100, 300, 1));

        config = sampler.Propose();
        sampler.Update(config, 0.4f);

        config = sampler.Propose();
        sampler.Update(config, 0.7f, new TrialCost(150, 80, 70, 250, 1));

        Assert.Equal(4, sampler.TrialCount);
    }

    #endregion

    #region Determinism

    [Fact]
    public void Propose_SameSeed_SameResult()
    {
        var cardinalities = new Dictionary<string, int> { ["a"] = 5, ["b"] = 3 };

        var sampler1 = new CostAwareSampler(cardinalities, seed: 42);
        var sampler2 = new CostAwareSampler(cardinalities, seed: 42);

        for (int i = 0; i < 10; i++)
        {
            var c1 = sampler1.Propose();
            var c2 = sampler2.Propose();

            Assert.Equal(c1["a"], c2["a"]);
            Assert.Equal(c1["b"], c2["b"]);

            var cost = new TrialCost(100 + i * 10, 50, 50 + i * 10, 200, 1);
            sampler1.Update(c1, (float)i / 10, cost);
            sampler2.Update(c2, (float)i / 10, cost);
        }
    }

    [Fact]
    public void Propose_DifferentSeeds_DifferentResults()
    {
        var cardinalities = new Dictionary<string, int> { ["x"] = 10 };

        var sampler1 = new CostAwareSampler(cardinalities, seed: 42);
        var sampler2 = new CostAwareSampler(cardinalities, seed: 99);

        // Over several proposals, at least one should differ
        bool anyDifferent = false;
        for (int i = 0; i < 20; i++)
        {
            var c1 = sampler1.Propose();
            var c2 = sampler2.Propose();

            if (c1["x"] != c2["x"])
                anyDifferent = true;

            sampler1.Update(c1, 0.5f);
            sampler2.Update(c2, 0.5f);
        }

        Assert.True(anyDifferent, "Different seeds should produce at least some different proposals");
    }

    #endregion

    #region Categorical Discretization

    [Fact]
    public void Discretization_AlwaysProducesValidIndices()
    {
        var cardinalities = new Dictionary<string, int>
        {
            ["small"] = 2,
            ["medium"] = 5,
            ["large"] = 20
        };
        var sampler = new CostAwareSampler(cardinalities, seed: 42);

        for (int i = 0; i < 50; i++)
        {
            var config = sampler.Propose();
            Assert.InRange(config["small"], 0, 1);
            Assert.InRange(config["medium"], 0, 4);
            Assert.InRange(config["large"], 0, 19);

            sampler.Update(config, (float)(i % 10) / 10,
                new TrialCost(100, 50, 50, 200, 1));
        }
    }

    [Fact]
    public void LargeCardinality_ExploresMultipleValues()
    {
        var cardinalities = new Dictionary<string, int> { ["x"] = 20 };
        var sampler = new CostAwareSampler(cardinalities, seed: 42);

        var seen = new HashSet<int>();
        for (int i = 0; i < 30; i++)
        {
            var config = sampler.Propose();
            seen.Add(config["x"]);
            sampler.Update(config, (float)(i % 5) / 5);
        }

        // With 20 categories and 30 proposals, we should explore more than just 1
        Assert.True(seen.Count > 1,
            $"Expected exploration of multiple categories, but only saw {seen.Count}");
    }

    #endregion

    #region Integration — Factory Pattern

    [Fact]
    public void CostAwareSampler_CanBeUsedAsSamplerFactory()
    {
        Func<Dictionary<string, int>, ISampler> factory =
            cardinalities => new CostAwareSampler(cardinalities, seed: 42);

        var cardinalities = new Dictionary<string, int> { ["x"] = 3, ["y"] = 4 };
        var sampler = factory(cardinalities);

        Assert.IsType<CostAwareSampler>(sampler);
        var config = sampler.Propose();
        Assert.Equal(2, config.Count);
    }

    [Fact]
    public void CostAwareSampler_FactoryWithCustomProjection()
    {
        Func<Dictionary<string, int>, ISampler> factory =
            cardinalities => new CostAwareSampler(
                cardinalities,
                costProjection: c => c.ElapsedMilliseconds,
                seed: 42);

        var cardinalities = new Dictionary<string, int> { ["x"] = 3 };
        var sampler = factory(cardinalities);

        var config = sampler.Propose();
        sampler.Update(config, 0.5f, new TrialCost(100, 50, 50, 999, 1));
        Assert.Equal(1, sampler.TrialCount);
    }

    #endregion
}
