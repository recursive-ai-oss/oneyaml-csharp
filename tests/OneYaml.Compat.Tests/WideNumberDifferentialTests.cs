using Xunit;
using CmCore = global::OneYaml.Compat.YamlDotNet.Core;
using RlCore = global::YamlDotNet.Core;

namespace OneYaml.Compat.Tests;

// Int128, UInt128 and Half against the real library: each documented difference is pinned on both sides, so the
// claim is checked rather than assumed, and a change in the real library shows up here.
public class WideNumberDifferentialTests
{
    public static TheoryData<string> WideValues => new()
    {
        "Int128", "UInt128", "Half",
    };

    private static object Value(string type) => type switch
    {
        "Int128" => (Int128)5,
        "UInt128" => (UInt128)5,
        _ => (Half)5,
    };

    [Theory]
    [MemberData(nameof(WideValues))]
    public void The_real_library_writes_an_empty_mapping_where_the_package_writes_the_number(string type)
    {
        var (real, compat) = Both.Serializers();
        object value = Value(type);

        Assert.Equal("{}" + Environment.NewLine, real.Serialize(value));
        Assert.Equal("5" + Environment.NewLine, compat.Serialize(value));
    }

    [Fact]
    public void The_same_holds_for_a_member_of_an_object()
    {
        var (real, compat) = Both.Serializers();
        var value = new Holder { Big = 7, Huge = 8, Small = (Half)2 };
        string nl = Environment.NewLine;

        Assert.Equal($"Big: {{}}{nl}Huge: {{}}{nl}Small: {{}}{nl}", real.Serialize(value));
        Assert.Equal($"Big: 7{nl}Huge: 8{nl}Small: 2{nl}", compat.Serialize(value));
    }

    [Fact]
    public void Plain_decimal_text_reads_to_the_same_value_in_both()
    {
        var (real, compat) = Both.Deserializers();

        foreach (string text in new[] { "5", "-5", "0", "123456789012345678" })
        {
            Assert.Equal(real.Deserialize<Int128>(text), compat.Deserialize<Int128>(text));
        }

        foreach (string text in new[] { "5", "0", "123456789012345678" })
        {
            Assert.Equal(real.Deserialize<UInt128>(text), compat.Deserialize<UInt128>(text));
        }

        foreach (string text in new[] { "5", "-5", "1.5", "0.25", "65504" })
        {
            Assert.Equal(real.Deserialize<Half>(text), compat.Deserialize<Half>(text));
        }

        Assert.Equal((Int128)(-5), compat.Deserialize<Int128>("-5"));
        Assert.Equal((Half)1.5, compat.Deserialize<Half>("1.5"));
    }

    [Theory]
    [InlineData("0x10", 16)]
    public void A_hexadecimal_number_is_read_by_the_package_and_refused_by_the_real_library(string text, int expected)
    {
        var (real, compat) = Both.Deserializers();

        Assert.ThrowsAny<RlCore.YamlException>(() => real.Deserialize<Int128>(text));
        Assert.ThrowsAny<RlCore.YamlException>(() => real.Deserialize<UInt128>(text));
        Assert.Equal((Int128)expected, compat.Deserialize<Int128>(text));
        Assert.Equal((UInt128)expected, compat.Deserialize<UInt128>(text));
    }

    [Theory]
    [InlineData(".inf", true)]
    [InlineData("-.inf", true)]
    [InlineData(".nan", false)]
    public void Float_spellings_are_read_into_a_half_by_the_package_and_refused_by_the_real_library(string text, bool infinite)
    {
        var (real, compat) = Both.Deserializers();

        Assert.ThrowsAny<RlCore.YamlException>(() => real.Deserialize<Half>(text));
        Half half = compat.Deserialize<Half>(text);
        Assert.Equal(infinite, Half.IsInfinity(half));
        Assert.Equal(!infinite, Half.IsNaN(half));
    }

    [Fact]
    public void A_number_too_large_for_a_half_is_infinity_in_the_real_library_and_an_error_in_the_package()
    {
        var (real, compat) = Both.Deserializers();

        Assert.True(Half.IsPositiveInfinity(real.Deserialize<Half>("1e10")));
        Assert.ThrowsAny<CmCore.YamlException>(() => compat.Deserialize<Half>("1e10"));
    }

    private sealed class Holder
    {
        public Int128 Big { get; set; }

        public UInt128 Huge { get; set; }

        public Half Small { get; set; }
    }
}
