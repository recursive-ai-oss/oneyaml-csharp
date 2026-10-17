using Xunit;
using Cm = global::OneYaml.Compat.YamlDotNet.RepresentationModel;
using CmS = global::OneYaml.Compat.YamlDotNet.Serialization;
using Core = global::OneYaml.Compat.YamlDotNet.Core;
using Rl = global::YamlDotNet.RepresentationModel;
using RlS = global::YamlDotNet.Serialization;

namespace OneYaml.Compat.Tests;

// Wide numbers and repeated keys read from a parser: places where YamlDotNet's own behaviour loses data or throws the wrong type.
public class LateFixTests
{
    private sealed class Wide
    {
        public Int128 Big { get; set; }

        public UInt128 Huge { get; set; }

        public Half Small { get; set; }
    }

    [Fact]
    public void Wide_numbers_are_written_as_the_numbers_they_are()
    {
        var value = new Wide { Big = Int128.MinValue, Huge = UInt128.MaxValue, Small = (Half)1.5 };
        string yaml = new CmS.Serializer().Serialize(value);

        // The adapter writes the platform's line break, as YamlDotNet does.
        string nl = Environment.NewLine;
        Assert.Equal($"Big: -170141183460469231731687303715884105728{nl}Huge: 340282366920938463463374607431768211455{nl}Small: 1.5{nl}", yaml);
    }

    [Fact]
    public void Wide_numbers_read_back_exactly()
    {
        var value = new Wide { Big = Int128.MinValue, Huge = UInt128.MaxValue, Small = Half.Epsilon };
        string yaml = new CmS.Serializer().Serialize(value);
        Wide back = new CmS.Deserializer().Deserialize<Wide>(yaml);

        Assert.Equal(value.Big, back.Big);
        Assert.Equal(value.Huge, back.Huge);
        Assert.Equal(value.Small, back.Small);
    }

    [Theory]
    [InlineData(".inf", true)]
    [InlineData("-.inf", true)]
    [InlineData(".nan", false)]
    public void Special_halves_use_the_float_spellings(string text, bool infinite)
    {
        Half half = new CmS.Deserializer().Deserialize<Half>(text);
        Assert.Equal(infinite, Half.IsInfinity(half));
        Assert.Equal(text + Environment.NewLine, new CmS.Serializer().Serialize(half));
    }

    [Fact]
    public void A_half_is_rounded_once_from_the_text()
    {
        // 1.00048828125 is exactly between two halves; going through a double first would still land on the even one, so a
        // value just above it pins the single rounding: it must round up.
        Half up = new CmS.Deserializer().Deserialize<Half>("1.000488281250000000000000001");
        Assert.Equal(Half.Parse("1.000488281250000000000000001", System.Globalization.CultureInfo.InvariantCulture), up);
        Assert.Equal(BitConverter.HalfToUInt16Bits((Half)1) + 1, BitConverter.HalfToUInt16Bits(up));
    }

    [Theory]
    [InlineData("340282366920938463463374607431768211456")]
    [InlineData("-1")]
    public void A_number_that_does_not_fit_a_wide_type_is_an_error_not_a_wrong_value(string text)
    {
        Assert.ThrowsAny<Core.YamlException>(() => new CmS.Deserializer().Deserialize<UInt128>(text));
    }

    [Fact]
    public void A_half_too_large_for_the_type_is_an_error_not_infinity()
    {
        Assert.ThrowsAny<Core.YamlException>(() => new CmS.Deserializer().Deserialize<Half>("1e10"));
    }

    [Fact]
    public void A_repeated_key_from_a_parser_is_a_yaml_exception_at_the_key_like_the_real_library()
    {
        const string text = "a: 1\nb: 2\na: 3\n";

        var real = Assert.ThrowsAny<global::YamlDotNet.Core.YamlException>(() => new Rl.YamlStream().Load(new global::YamlDotNet.Core.Parser(new StringReader(text))));
        var ours = Assert.ThrowsAny<Core.YamlException>(() => new Cm.YamlStream().Load(new Core.Parser(new StringReader(text))));

        Assert.Equal(real.Start.Line, ours.Start.Line);
        Assert.Equal(real.Start.Column, ours.Start.Column);
        Assert.Equal(3, ours.Start.Line);
    }
}
