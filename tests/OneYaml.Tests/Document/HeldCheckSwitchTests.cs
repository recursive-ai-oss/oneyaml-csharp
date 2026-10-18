using Xunit;

namespace OneYaml.Tests.Document;

/// <summary>
/// The CI step that builds the library with the held-back check sets <c>ONEYAML_EXPECT_CHECK_HELD=1</c> and runs this
/// test with the rest. If the build switch is not applied, the step fails here instead of passing without checking.
/// </summary>
public class HeldCheckSwitchTests
{
    [Fact]
    public void The_library_is_built_with_the_held_back_check_exactly_when_the_run_asks_for_it()
    {
        bool expected = Environment.GetEnvironmentVariable("ONEYAML_EXPECT_CHECK_HELD") == "1";
        Assert.Equal(OneYaml.Documents.DocState.CheckHeldBuild, expected);
    }
}
