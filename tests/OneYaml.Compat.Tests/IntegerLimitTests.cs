using Xunit;
using CmS = global::OneYaml.Compat.YamlDotNet.Serialization;
using Core = global::OneYaml.Compat.YamlDotNet.Core;

namespace OneYaml.Compat.Tests;

// Integer text the compat reader must refuse, or read, in bounded time.
public class IntegerLimitTests
{
    [Fact]
    public void A_tagged_integer_beyond_the_digit_limit_is_refused_in_bounded_time()
    {
        string yaml = "a: !!int " + new string('1', 100_000) + "\n";
        long before = GC.GetAllocatedBytesForCurrentThread();

        Assert.ThrowsAny<Core.YamlException>(() => new CmS.DeserializerBuilder().Build().Deserialize<object>(yaml));

        // The refusal happens before any digit is converted; converting 100,000 digits one at a time takes gigabytes.
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 50_000_000);
    }

    [Fact]
    public void A_typed_integer_beyond_the_digit_limit_is_refused()
    {
        string yaml = "a: " + new string('1', 100_000) + "\n";

        Assert.ThrowsAny<Core.YamlException>(() => new CmS.DeserializerBuilder().Build().Deserialize<Dictionary<string, System.Numerics.BigInteger>>(yaml));
    }

    [Fact]
    public void A_long_sexagesimal_integer_is_refused_before_it_is_split()
    {
        string yaml = "a: !!int " + string.Join(":", Enumerable.Repeat("1", 50_000)) + "\n";
        long before = GC.GetAllocatedBytesForCurrentThread();

        Assert.ThrowsAny<Core.YamlException>(() => new CmS.DeserializerBuilder().Build().Deserialize<object>(yaml));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 50_000_000);
    }

    [Fact]
    public void An_integer_of_four_thousand_digits_is_still_read_exactly()
    {
        string digits = "7" + new string('0', 3999);
        object? value = new CmS.DeserializerBuilder().Build().Deserialize<object>("a: !!int " + digits + "\n");

        var map = Assert.IsAssignableFrom<IDictionary<object, object>>(value);
        Assert.Equal(System.Numerics.BigInteger.Parse(digits), Assert.IsType<System.Numerics.BigInteger>(map["a"]));
    }

    [Fact]
    public void A_plain_scalar_of_many_digits_stays_text_when_read_without_a_type()
    {
        string digits = new('1', 100_000);
        object? value = new CmS.DeserializerBuilder().WithAttemptingUnquotedStringTypeDeserialization().Build().Deserialize<object>("a: " + digits + "\n");

        var map = Assert.IsAssignableFrom<IDictionary<object, object>>(value);
        Assert.Equal(digits, Assert.IsType<string>(map["a"]));
    }
}
