using LMP.Optimizers;

namespace LMP.Tests;

public class CategoricalTpeSamplerTests
{
    #region Constructor Validation

    [Fact]
    public void Constructor_NullCardinalities_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new CategoricalTpeSampler(null!));
    }

    [Fact]
    public void Constructor_GammaZero_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CategoricalTpeSampler(new Dictionary<string, int> { ["x"] = 3 }, gamma: 0));
    }

    [Fact]
    public void Constructor_GammaOne_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CategoricalTpeSampler(new Dictionary<string, int> { ["x"] = 3 }, gamma: 1.0));
    }

    [Fact]
    public void Constructor_GammaNegative_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CategoricalTpeSampler(new Dictionary<string, int> { ["x"] = 3 }, gamma: -0.1));
    }

    [Fact]
    public void Constructor_ValidParameters_DoesNotThrow()
    {
        var sampler = new CategoricalTpeSampler(
            new Dictionary<string, int> { ["x"] = 5, ["y"] = 3 },
            gamma: 0.25, seed: 42);
        Assert.NotNull(sampler);
    }

    #endregion

    #region Propose — Uniform Phase

    [Fact]
    public void Propose_NoHistory_ReturnsValidConfig()
    {
        var cardinalities = new Dictionary<string, int> { ["a"] = 3, ["b"] = 5 };
        var sampler = new CategoricalTpeSampler(cardinalities, seed: 42);

        var config = sampler.Propose();

        Assert.Equal(2, config.Count);
        Assert.InRange(config["a"], 0, 2);
        Assert.InRange(config["b"], 0, 4);
    }

    [Fact]
    public void Propose_FewTrials_StillReturnsValidConfig()
    {
        var cardinalities = new Dictionary<string, int> { ["x"] = 4 };
        var sampler = new CategoricalTpeSampler(cardinalities, seed: 42);

        // Report one trial — not enough for TPE
        sampler.Update(new Dictionary<string, int> { ["x"] = 0 }, 0.5f);

        var config = sampler.Propose();
        Assert.InRange(config["x"], 0, 3);
    }

    #endregion

    #region Propose — TPE Phase

    [Fact]
    public void Propose_AfterEnoughTrials_UsesTPE()
    {
        var cardinalities = new Dictionary<string, int> { ["x"] = 3 };
        var sampler = new CategoricalTpeSampler(cardinalities, gamma: 0.25, seed: 42);

        // Report enough trials so TPE kicks in (need >= 1/0.25 = 4)
        sampler.Update(new Dictionary<string, int> { ["x"] = 0 }, 0.9f);
        sampler.Update(new Dictionary<string, int> { ["x"] = 0 }, 0.8f);
        sampler.Update(new Dictionary<string, int> { ["x"] = 1 }, 0.2f);
        sampler.Update(new Dictionary<string, int> { ["x"] = 2 }, 0.1f);

        // After these trials, category 0 is strongly preferred (high scores)
        var config = sampler.Propose();
        Assert.InRange(config["x"], 0, 2); // Valid regardless
    }

    [Fact]
    public void Propose_TPE_FavorsBetterCategories()
    {
        var cardinalities = new Dictionary<string, int> { ["x"] = 3 };
        var sampler = new CategoricalTpeSampler(cardinalities, gamma: 0.25, seed: 100);

        // Heavily bias category 0 as good
        for (int i = 0; i < 20; i++)
        {
            sampler.Update(new Dictionary<string, int> { ["x"] = 0 }, 0.9f);
            sampler.Update(new Dictionary<string, int> { ["x"] = 1 }, 0.1f);
            sampler.Update(new Dictionary<string, int> { ["x"] = 2 }, 0.05f);
        }

        // Propose many times and check that category 0 is proposed more often
        int[] counts = new int[3];
        for (int i = 0; i < 100; i++)
            counts[sampler.Propose()["x"]]++;

        Assert.True(counts[0] > counts[1],
            $"Category 0 (count={counts[0]}) should be proposed more than category 1 (count={counts[1]})");
        Assert.True(counts[0] > counts[2],
            $"Category 0 (count={counts[0]}) should be proposed more than category 2 (count={counts[2]})");
    }

    #endregion

    #region Report

    [Fact]
    public void Report_NullConfig_Throws()
    {
        var sampler = new CategoricalTpeSampler(new Dictionary<string, int> { ["x"] = 3 });
        Assert.Throws<ArgumentNullException>(() => sampler.Update(null!, 0.5f));
    }

    [Fact]
    public void Report_IncrementsTrialCount()
    {
        var sampler = new CategoricalTpeSampler(new Dictionary<string, int> { ["x"] = 3 });
        Assert.Equal(0, sampler.TrialCount);

        sampler.Update(new Dictionary<string, int> { ["x"] = 1 }, 0.5f);
        Assert.Equal(1, sampler.TrialCount);

        sampler.Update(new Dictionary<string, int> { ["x"] = 2 }, 0.7f);
        Assert.Equal(2, sampler.TrialCount);
    }

    #endregion

    #region Determinism

    [Fact]
    public void Propose_SameSeed_SameResult()
    {
        var cardinalities = new Dictionary<string, int> { ["a"] = 5, ["b"] = 3 };

        var sampler1 = new CategoricalTpeSampler(cardinalities, seed: 42);
        var sampler2 = new CategoricalTpeSampler(cardinalities, seed: 42);

        for (int i = 0; i < 10; i++)
        {
            var c1 = sampler1.Propose();
            var c2 = sampler2.Propose();

            Assert.Equal(c1["a"], c2["a"]);
            Assert.Equal(c1["b"], c2["b"]);

            sampler1.Update(c1, (float)i / 10);
            sampler2.Update(c2, (float)i / 10);
        }
    }

    #endregion

    #region Multi-Parameter

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
        var sampler = new CategoricalTpeSampler(cardinalities, seed: 42);

        var config = sampler.Propose();

        Assert.Equal(4, config.Count);
        Assert.InRange(config["instr_a"], 0, 4);
        Assert.InRange(config["demos_a"], 0, 3);
        Assert.InRange(config["instr_b"], 0, 2);
        Assert.InRange(config["demos_b"], 0, 5);
    }

    #endregion
}
