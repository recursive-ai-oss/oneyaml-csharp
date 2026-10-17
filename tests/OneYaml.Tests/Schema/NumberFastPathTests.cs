using System.Text;
using OneYaml.Schema;
using Xunit;

namespace OneYaml.Tests.Schema;

/// <summary>
/// The number fast paths must agree with the exact parsers on every spelling: the same value to the last bit
/// (including -0.0, subnormals and values beyond the double range), and nothing accepted that the exact path refuses.
/// </summary>
public sealed class NumberFastPathTests
{
    private static void Compare(string text, ref long fastDoubles, ref long fastInts)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);

        bool exactOk = CoreSchema.TryParseDoubleExact(bytes, out double exact);
        bool finalOk = CoreSchema.TryParseDouble(bytes, out double final);
        Assert.True(exactOk == finalOk && BitConverter.DoubleToInt64Bits(exact) == BitConverter.DoubleToInt64Bits(final), $"TryParseDouble differs for '{text}'");
        if (CoreSchema.TryParseSimpleDouble(bytes, out double fast, out bool isInteger))
        {
            fastDoubles++;
            Assert.True(exactOk && BitConverter.DoubleToInt64Bits(exact) == BitConverter.DoubleToInt64Bits(fast), $"fast double differs for '{text}': {fast:R} vs {exact:R}");
            Assert.Equal(!text.AsSpan().ContainsAny('.', 'e', 'E'), isInteger);
        }

        if (CoreSchema.TryParseSimpleInt64(bytes, out long whole))
        {
            fastInts++;
            Assert.True(CoreSchema.TryParseInt64(bytes, out long reference) && reference == whole, $"fast int differs for '{text}'");
            Assert.Equal(ScalarKind.Int, CoreSchema.ResolvePlain(bytes));
        }
    }

    private static void Run(int seed, int count)
    {
        var rnd = new Random(seed);
        long fastDoubles = 0;
        long fastInts = 0;
        for (int i = 0; i < count; i++)
        {
            Compare(NumberScalarGenerator.Next(rnd), ref fastDoubles, ref fastInts);
        }

        // The generator must keep the fast paths busy, or the comparison proves nothing.
        Assert.True(fastDoubles > count / 10, "too few fast doubles: " + fastDoubles);
        Assert.True(fastInts > count / 20, "too few fast ints: " + fastInts);
    }

    [Fact]
    public void Fast_paths_agree_with_the_exact_parsers_on_a_quick_sample() => Run(1, 200_000);

    [Theory]
    [InlineData(101)]
    [InlineData(102)]
    [InlineData(103)]
    [InlineData(104)]
    [InlineData(105)]
    [InlineData(106)]
    [InlineData(107)]
    [InlineData(108)]
    [Trait("Category", "Slow")]
    public void Fast_paths_agree_with_the_exact_parsers_on_1_5_million_scalars_per_seed(int seed) => Run(seed, 1_500_000);

    [Fact]
    public void Doubles_that_need_digit_by_digit_conversion_still_use_the_exact_path()
    {
        // 2^53 + 1 is not a double, 0.1 has no exact binary form past 22 powers of ten: both must round like the BCL does.
        Assert.False(CoreSchema.TryParseSimpleDouble("9007199254740993.0"u8, out _, out _));
        Assert.False(CoreSchema.TryParseSimpleDouble("1e23"u8, out _, out _));
        Assert.True(CoreSchema.TryParseDouble("9007199254740993.0"u8, out double d));
        Assert.Equal(9007199254740992.0, d);
        Assert.True(CoreSchema.TryParseDouble("-0.0"u8, out double z));
        Assert.Equal(BitConverter.DoubleToInt64Bits(-0.0), BitConverter.DoubleToInt64Bits(z));
    }

    [Theory]
    [InlineData("v: -0", 0.0)]
    [InlineData("v: 12", 12.0)]
    [InlineData("v: 0.5", 0.5)]
    [InlineData("v: 1.5e3", 1500.0)]
    [InlineData("v: 0x10", 16.0)]
    public void Typed_double_reads_the_same_value_as_before(string yaml, double expected)
    {
        var r = YamlSerializer.Deserialize<Holder>(yaml)!;
        Assert.Equal(BitConverter.DoubleToInt64Bits(expected), BitConverter.DoubleToInt64Bits(r.V));
    }

    [Fact]
    public void A_negative_zero_integer_reads_as_positive_zero_and_a_negative_zero_float_keeps_its_sign()
    {
        Assert.Equal(BitConverter.DoubleToInt64Bits(0.0), BitConverter.DoubleToInt64Bits(YamlSerializer.Deserialize<Holder>("v: -0")!.V));
        Assert.Equal(BitConverter.DoubleToInt64Bits(-0.0), BitConverter.DoubleToInt64Bits(YamlSerializer.Deserialize<Holder>("v: -0.0")!.V));
        Assert.Equal(BitConverter.DoubleToInt64Bits(-0.0), BitConverter.DoubleToInt64Bits((double)YamlSerializer.Deserialize<Dictionary<string, object?>>("v: -0.0")!["v"]!));
    }

    [Fact]
    public void Out_of_range_and_invalid_numbers_keep_their_errors()
    {
        YamlException big = Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<Holder>("v: 1e400"));
        Assert.Equal(1, big.Line);
        YamlException wide = Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<IntHolder>("v: 9223372036854775808"));
        Assert.Equal(1, wide.Line);
        Assert.Equal(YamlErrorCategory.Binding, Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<IntHolder>("v: 1.5")).Category);
    }

    public sealed class Holder
    {
        public double V { get; set; }
    }

    public sealed class IntHolder
    {
        public long V { get; set; }
    }
}
