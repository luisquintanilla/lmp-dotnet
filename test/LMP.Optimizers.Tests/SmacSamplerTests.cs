using LMP.Optimizers;
#pragma warning disable CS0618 // tests obsolete ISampler interface intentionally
namespace LMP.Tests;

public class SmacSamplerTests
{
    #region Constructor Validation

    [Fact]
    public void Constructor_NullCardinalities_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new SmacSampler(null!));
    }

    [Fact]
    public void Constructor_ValidParameters_DoesNotThrow()
    {
        var sampler = new SmacSampler(
            new Dictionary<string, int> { ["x"] = 5, ["y"] = 3 },
            numTrees: 10, seed: 42);
        Assert.NotNull(sampler);
    }

    #endregion

    #region ISampler Contract

    [Fact]
    public void ImplementsISampler()
    {
        var sampler = new SmacSampler(new Dictionary<string, int> { ["x"] = 3 });
        Assert.IsAssignableFrom<ISampler>(sampler);
    }

    [Fact]
    public void TrialCount_StartsAtZero()
    {
        var sampler = new SmacSampler(new Dictionary<string, int> { ["x"] = 3 });
        Assert.Equal(0, sampler.TrialCount);
    }

    [Fact]
    public void Update_IncrementsTrialCount()
    {
        var sampler = new SmacSampler(new Dictionary<string, int> { ["x"] = 3 });
        sampler.Update(new Dictionary<string, int> { ["x"] = 1 }, 0.5f);
        Assert.Equal(1, sampler.TrialCount);
    }

    [Fact]
    public void Update_NullConfig_Throws()
    {
        var sampler = new SmacSampler(new Dictionary<string, int> { ["x"] = 3 });
        Assert.Throws<ArgumentNullException>(() => sampler.Update(null!, 0.5f));
    }

    #endregion

    #region Propose — Uniform Phase

    [Fact]
    public void Propose_NoHistory_ReturnsValidConfig()
    {
        var cardinalities = new Dictionary<string, int> { ["a"] = 3, ["b"] = 5 };
        var sampler = new SmacSampler(cardinalities, seed: 42);

        var config = sampler.Propose();

        Assert.Equal(2, config.Count);
        Assert.InRange(config["a"], 0, 2);
        Assert.InRange(config["b"], 0, 4);
    }

    [Fact]
    public void Propose_DuringInitPhase_ReturnsValidConfigs()
    {
        var cardinalities = new Dictionary<string, int> { ["x"] = 4 };
        var sampler = new SmacSampler(cardinalities, seed: 42);

        // During initial phase, all proposals should be valid
        for (int i = 0; i < 5; i++)
        {
            var config = sampler.Propose();
            Assert.InRange(config["x"], 0, 3);
            sampler.Update(config, _rng.NextSingle());
        }
    }

    private static readonly Random _rng = new(42);

    #endregion

    #region Propose — SMAC Phase

    [Fact]
    public void Propose_AfterEnoughTrials_ReturnsValidConfig()
    {
        var cardinalities = new Dictionary<string, int> { ["x"] = 3 };
        var sampler = new SmacSampler(cardinalities, numInitialTrials: 4, seed: 42);

        // Fill initial phase
        for (int i = 0; i < 4; i++)
        {
            sampler.Update(new Dictionary<string, int> { ["x"] = i % 3 }, 0.1f * i);
        }

        // Now SMAC should be active
        var config = sampler.Propose();
        Assert.InRange(config["x"], 0, 2);
    }

    [Fact]
    public void Propose_SMAC_FavorsBetterCategories()
    {
        var cardinalities = new Dictionary<string, int> { ["x"] = 3 };
        var sampler = new SmacSampler(cardinalities, numInitialTrials: 6, seed: 100);

        // Bias category 0 as strongly good
        for (int i = 0; i < 20; i++)
        {
            sampler.Update(new Dictionary<string, int> { ["x"] = 0 }, 0.9f);
            sampler.Update(new Dictionary<string, int> { ["x"] = 1 }, 0.1f);
            sampler.Update(new Dictionary<string, int> { ["x"] = 2 }, 0.05f);
        }

        // Propose many times — category 0 should be favored
        int[] counts = new int[3];
        for (int i = 0; i < 50; i++)
        {
            var config = sampler.Propose();
            counts[config["x"]]++;
        }

        Assert.True(counts[0] > counts[1],
            $"Category 0 (count={counts[0]}) should be proposed more than category 1 (count={counts[1]})");
        Assert.True(counts[0] > counts[2],
            $"Category 0 (count={counts[0]}) should be proposed more than category 2 (count={counts[2]})");
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
        var sampler = new SmacSampler(cardinalities, seed: 42);

        var config = sampler.Propose();

        Assert.Equal(4, config.Count);
        Assert.InRange(config["instr_a"], 0, 4);
        Assert.InRange(config["demos_a"], 0, 3);
        Assert.InRange(config["instr_b"], 0, 2);
        Assert.InRange(config["demos_b"], 0, 5);
    }

    #endregion

    #region Determinism

    [Fact]
    public void Propose_SameSeed_SameResult()
    {
        var cardinalities = new Dictionary<string, int> { ["a"] = 5, ["b"] = 3 };

        var sampler1 = new SmacSampler(cardinalities, seed: 42);
        var sampler2 = new SmacSampler(cardinalities, seed: 42);

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

    #region Integration with MIPROv2

    [Fact]
    public void SmacSampler_CanBeUsedAsSamplerFactory()
    {
        // Verify the factory pattern works
        Func<Dictionary<string, int>, ISampler> factory =
            cardinalities => new SmacSampler(cardinalities, numTrees: 5, seed: 42);

        var cardinalities = new Dictionary<string, int> { ["x"] = 3, ["y"] = 4 };
        var sampler = factory(cardinalities);

        Assert.IsType<SmacSampler>(sampler);
        var config = sampler.Propose();
        Assert.Equal(2, config.Count);
    }

    #endregion
}
