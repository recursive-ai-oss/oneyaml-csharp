using Xunit;

namespace OneYaml.Tests.Generated;

// A decimal text is rounded to Half once, from the text; going through double first rounds twice.
public class HalfRoundingTests
{
    [Theory]
    [InlineData("1.00048828125000000001", (ushort)0x3C01)]
    [InlineData("1.00048828125", (ushort)0x3C00)]
    [InlineData("1.0", (ushort)0x3C00)]
    [InlineData("0.1", (ushort)0x2E66)]
    [InlineData("6.1e-5", (ushort)0x03FF)]
    [InlineData("-0.0", (ushort)0x8000)]
    [InlineData("65504", (ushort)0x7BFF)]
    // Exact ties and values a hair either side of them, where parsers that round twice or that lose digits disagree.
    [InlineData("0.0000611245632171630859375", (ushort)0x0402)]
    [InlineData("0.0000610053539276123046875", (ushort)0x0400)]
    [InlineData("0.0000101029872894287109375", (ushort)0x00AA)]
    [InlineData("0.00001004338264465332031250000000001", (ushort)0x00A9)]
    [InlineData("0.00001004338264465332031249999999999", (ushort)0x00A8)]
    [InlineData("-0.0000101029872894287109375", (ushort)0x80AA)]
    [InlineData("2.98023223876953125e-8", (ushort)0x0000)]
    [InlineData("2.980232238769531251e-8", (ushort)0x0001)]
    [InlineData("65519.99999999999999999", (ushort)0x7BFF)]
    public void A_decimal_text_rounds_once_on_the_reflection_and_generator_paths(string text, ushort bits)
    {
        string yaml = "small: " + text + "\n";
        WideNumbers reflected = YamlSerializer.Deserialize<WideNumbers>(yaml)!;
        WideNumbers generated = YamlSerializer.Deserialize(yaml, ModelsContext.Default.WideNumbers)!;
        Assert.Equal(bits, BitConverter.HalfToUInt16Bits(reflected.Small));
        Assert.Equal(bits, BitConverter.HalfToUInt16Bits(generated.Small));
        Assert.Equal(bits, BitConverter.HalfToUInt16Bits(YamlSerializer.Deserialize<Half>(text)));
    }

    [Theory]
    [InlineData("65520")]
    [InlineData("65520.0")]
    [InlineData("65520.00000000000000001")]
    [InlineData("-65520.0")]
    [InlineData("1e5")]
    public void A_value_that_rounds_to_infinity_is_a_binding_error_on_both_paths(string text)
    {
        string yaml = "small: " + text + "\n";
        Assert.Equal(YamlErrorCategory.Binding, Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<WideNumbers>(yaml)).Category);
        Assert.Equal(YamlErrorCategory.Binding, Assert.Throws<YamlException>(() => YamlSerializer.Deserialize(yaml, ModelsContext.Default.WideNumbers)).Category);
    }
}
