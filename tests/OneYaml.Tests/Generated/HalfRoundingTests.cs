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
    public void A_decimal_text_rounds_once_on_the_reflection_and_generator_paths(string text, ushort bits)
    {
        string yaml = "small: " + text + "\n";
        WideNumbers reflected = YamlSerializer.Deserialize<WideNumbers>(yaml)!;
        WideNumbers generated = YamlSerializer.Deserialize(yaml, ModelsContext.Default.WideNumbers)!;
        Assert.Equal(bits, BitConverter.HalfToUInt16Bits(reflected.Small));
        Assert.Equal(bits, BitConverter.HalfToUInt16Bits(generated.Small));
        Assert.Equal(bits, BitConverter.HalfToUInt16Bits(YamlSerializer.Deserialize<Half>(text)));
    }
}
