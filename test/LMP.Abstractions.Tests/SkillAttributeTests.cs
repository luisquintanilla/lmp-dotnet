namespace LMP.Tests;

public class SkillAttributeTests
{
    [Fact]
    public void DefaultProperties_AreNull()
    {
        var attr = new SkillAttribute();
        Assert.Null(attr.Name);
        Assert.Null(attr.Description);
        Assert.Null(attr.Tags);
    }

    [Fact]
    public void NameOverride_IsPreserved()
    {
        var attr = new SkillAttribute { Name = "web-search" };
        Assert.Equal("web-search", attr.Name);
    }

    [Fact]
    public void DescriptionOverride_IsPreserved()
    {
        var attr = new SkillAttribute { Description = "Searches the web." };
        Assert.Equal("Searches the web.", attr.Description);
    }

    [Fact]
    public void Tags_ArePreserved()
    {
        var attr = new SkillAttribute { Tags = ["search", "retrieval"] };
        Assert.Equal(["search", "retrieval"], attr.Tags);
    }

    [Fact]
    public void AllProperties_SetTogether()
    {
        var attr = new SkillAttribute
        {
            Name = "calc",
            Description = "Math evaluator.",
            Tags = ["math"]
        };
        Assert.Equal("calc", attr.Name);
        Assert.Equal("Math evaluator.", attr.Description);
        Assert.Equal(["math"], attr.Tags);
    }

    [Fact]
    public void AttributeUsage_IsMethodOnly()
    {
        var usage = typeof(SkillAttribute)
            .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
            .Cast<AttributeUsageAttribute>()
            .Single();
        Assert.Equal(AttributeTargets.Method, usage.ValidOn);
        Assert.False(usage.AllowMultiple);
    }
}
