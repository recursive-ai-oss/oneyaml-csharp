using Xunit;

namespace OneYaml.Tests;

public class SmokeTests
{
    [Fact]
    public void Assembly_is_loadable()
    {
        Assert.Equal("OneYaml", typeof(AssemblyMarker).Assembly.GetName().Name);
    }

    // Tests that take more than a moment carry the Slow category; the local checks
    // run with Category!=Slow and CI runs everything.
    [Fact]
    [Trait("Category", "Slow")]
    public void Slow_tests_are_excluded_from_the_gate()
    {
        Assert.Equal("OneYaml", typeof(AssemblyMarker).Assembly.GetName().Name);
    }
}
