namespace LMP.Tests;

public class SkillManifestTests
{
    [Fact]
    public void Constructor_Name_IsPreserved()
    {
        var m = new SkillManifest("search");
        Assert.Equal("search", m.Name);
    }

    [Fact]
    public void Constructor_Description_DefaultsToNull()
    {
        var m = new SkillManifest("search");
        Assert.Null(m.Description);
    }

    [Fact]
    public void Constructor_Tags_DefaultsToNull()
    {
        var m = new SkillManifest("search");
        Assert.Null(m.Tags);
    }

    [Fact]
    public void Constructor_AllParameters()
    {
        var m = new SkillManifest("calc", "Evaluates math.", ["math", "tools"]);
        Assert.Equal("calc", m.Name);
        Assert.Equal("Evaluates math.", m.Description);
        Assert.Equal(["math", "tools"], m.Tags);
    }

    [Fact]
    public void For_Factory_SetsNameAndDescription()
    {
        var m = SkillManifest.For("search", "Searches the web.");
        Assert.Equal("search", m.Name);
        Assert.Equal("Searches the web.", m.Description);
    }

    [Fact]
    public void For_Factory_DescriptionOptional()
    {
        var m = SkillManifest.For("search");
        Assert.Equal("search", m.Name);
        Assert.Null(m.Description);
    }

    [Fact]
    public void RecordEquality_SameName_Equal()
    {
        var m1 = new SkillManifest("search");
        var m2 = new SkillManifest("search");
        Assert.Equal(m1, m2);
    }

    [Fact]
    public void RecordEquality_DifferentName_NotEqual()
    {
        var m1 = new SkillManifest("search");
        var m2 = new SkillManifest("calc");
        Assert.NotEqual(m1, m2);
    }

    [Fact]
    public void RecordEquality_DifferentDescription_NotEqual()
    {
        var m1 = new SkillManifest("search", "desc1");
        var m2 = new SkillManifest("search", "desc2");
        Assert.NotEqual(m1, m2);
    }
}
