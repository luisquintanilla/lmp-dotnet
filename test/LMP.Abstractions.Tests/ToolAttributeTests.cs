namespace LMP.Tests;

/// <summary>
/// Tests for the <see cref="ToolAttribute"/> declaration.
/// </summary>
public class ToolAttributeTests
{
    [Fact]
    public void ToolAttribute_CanBeAppliedToMethod()
    {
        var attr = new ToolAttribute();
        Assert.Null(attr.Name);
        Assert.Null(attr.Description);
    }

    [Fact]
    public void ToolAttribute_NameAndDescription_CanBeSet()
    {
        var attr = new ToolAttribute { Name = "search", Description = "Searches the web." };
        Assert.Equal("search", attr.Name);
        Assert.Equal("Searches the web.", attr.Description);
    }

    [Fact]
    public void ToolAttribute_IsAttributeUsageRestricted_ToMethod()
    {
        var usage = (AttributeUsageAttribute)Attribute.GetCustomAttribute(
            typeof(ToolAttribute), typeof(AttributeUsageAttribute))!;
        Assert.Equal(AttributeTargets.Method, usage.ValidOn);
    }

    [Fact]
    public void ToolAttribute_AllowMultiple_IsFalse()
    {
        var usage = (AttributeUsageAttribute)Attribute.GetCustomAttribute(
            typeof(ToolAttribute), typeof(AttributeUsageAttribute))!;
        Assert.False(usage.AllowMultiple);
    }

    [Fact]
    public void ToolAttribute_Inherited_IsFalse()
    {
        var usage = (AttributeUsageAttribute)Attribute.GetCustomAttribute(
            typeof(ToolAttribute), typeof(AttributeUsageAttribute))!;
        Assert.False(usage.Inherited);
    }
}
