using System.Numerics;
using Xunit;

namespace OneYaml.Tests.Generated;

internal sealed class WideNumbers
{
    public Int128 Wide { get; set; }

    public UInt128 UnsignedWide { get; set; }

    public Half Small { get; set; }

    public nint Native { get; set; }

    public nuint UnsignedNative { get; set; }
}

// Int128, UInt128, Half, nint and nuint bind on both paths, in both directions, with Core-schema number rules.
public class WideNumberTests
{
    private static WideNumbers Sample() => new()
    {
        Wide = Int128.MinValue + 7,
        UnsignedWide = UInt128.MaxValue - 3,
        Small = (Half)0.1,
        Native = -42,
        UnsignedNative = 77,
    };

    private static void Same(WideNumbers a, WideNumbers b)
    {
        Assert.Equal(a.Wide, b.Wide);
        Assert.Equal(a.UnsignedWide, b.UnsignedWide);
        Assert.Equal(a.Small, b.Small);
        Assert.Equal(a.Native, b.Native);
        Assert.Equal(a.UnsignedNative, b.UnsignedNative);
    }

    [Fact]
    public void Both_paths_write_the_same_text_and_read_it_back()
    {
        string reflected = YamlSerializer.Serialize(Sample());
        string generated = YamlSerializer.Serialize(Sample(), ModelsContext.Default.WideNumbers);
        Assert.Equal(reflected, generated);
        Assert.Contains("wide: -170141183460469231731687303715884105721\n", reflected, StringComparison.Ordinal);
        Assert.Contains("unsignedWide: 340282366920938463463374607431768211452\n", reflected, StringComparison.Ordinal);
        Assert.Contains("small: 0.1\n", reflected, StringComparison.Ordinal);

        Same(Sample(), YamlSerializer.Deserialize<WideNumbers>(reflected)!);
        Same(Sample(), YamlSerializer.Deserialize(generated, ModelsContext.Default.WideNumbers)!);
    }

    [Fact]
    public void A_top_level_value_is_written_as_a_number_not_as_an_empty_mapping()
    {
        Assert.Equal("5", YamlSerializer.Serialize((Int128)5).Trim());
        Assert.Equal("5", YamlSerializer.Serialize((UInt128)5).Trim());
        Assert.Equal("5", YamlSerializer.Serialize((nint)5).Trim());
        Assert.Equal("5", YamlSerializer.Serialize((nuint)5).Trim());
        Assert.Equal("2.0", YamlSerializer.Serialize((Half)2).Trim());
        Assert.Equal(".inf", YamlSerializer.Serialize(Half.PositiveInfinity).Trim());
        Assert.Equal(".nan", YamlSerializer.Serialize(Half.NaN).Trim());
        Assert.Equal((Half)6.0e-8, YamlSerializer.Deserialize<Half>(YamlSerializer.Serialize((Half)6.0e-8)));
    }

    [Theory]
    [InlineData("0x7fffffffffffffffffffffffffffffff", "170141183460469231731687303715884105727")]
    [InlineData("-170141183460469231731687303715884105728", "-170141183460469231731687303715884105728")]
    public void Int128_reads_core_integers_up_to_its_range(string text, string expected)
    {
        Assert.Equal(Int128.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), YamlSerializer.Deserialize<Int128>(text));
    }

    public static TheoryData<string, string> OutOfRange() => new()
    {
        { "wide", "170141183460469231731687303715884105728" },
        { "wide", "-170141183460469231731687303715884105729" },
        { "unsignedWide", "340282366920938463463374607431768211456" },
        { "unsignedWide", "-1" },
        { "small", "70000" },
        { "small", "-1e10" },
        { "native", "9223372036854775808" },
        { "unsignedNative", "-1" },
        { "unsignedNative", "18446744073709551616" },
    };

    [Theory]
    [MemberData(nameof(OutOfRange))]
    public void A_value_outside_the_range_is_a_binding_error_on_both_paths(string member, string value)
    {
        string yaml = member + ": " + value + "\n";
        YamlException reflected = Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<WideNumbers>(yaml));
        YamlException generated = Assert.Throws<YamlException>(() => YamlSerializer.Deserialize(yaml, ModelsContext.Default.WideNumbers));
        Assert.Equal(YamlErrorCategory.Binding, reflected.Category);
        Assert.Equal(reflected.Category, generated.Category);
        Assert.Equal(reflected.Message, generated.Message);
    }

    [Fact]
    public void A_quoted_or_non_numeric_value_is_refused_on_both_paths()
    {
        foreach (string yaml in new[] { "wide: \"5\"\n", "unsignedWide: x\n", "small: \"0.5\"\n", "native: 1.5\n", "unsignedNative: ~\n" })
        {
            Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<WideNumbers>(yaml));
            Assert.Throws<YamlException>(() => YamlSerializer.Deserialize(yaml, ModelsContext.Default.WideNumbers));
        }
    }

    [Fact]
    public void Half_keeps_its_special_values_and_rounds_to_the_nearest_value()
    {
        Assert.Equal(Half.PositiveInfinity, YamlSerializer.Deserialize<Half>(".inf"));
        Assert.True(Half.IsNaN(YamlSerializer.Deserialize<Half>(".nan")));
        Assert.Equal((Half)65504, YamlSerializer.Deserialize<Half>("65504"));
        Assert.Equal((Half)0.1, YamlSerializer.Deserialize<Half>("0.1"));
    }

    [Fact]
    public void The_generic_load_of_a_wide_number_still_gives_a_big_integer()
    {
        Assert.Equal(BigInteger.Parse("340282366920938463463374607431768211456", System.Globalization.CultureInfo.InvariantCulture), YamlSerializer.Deserialize<object>("340282366920938463463374607431768211456"));
    }
}
