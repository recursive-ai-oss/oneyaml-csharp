using System.Buffers;
using System.Globalization;
using System.Numerics;
using System.Text;
using OneYaml.Emitting;
using OneYaml.Schema;
using Xunit;

namespace OneYaml.Tests.Emitting;

public sealed class ScalarWriterTests
{
    private static string Write(Action<Utf8YamlWriter> action)
    {
        var buffer = new ArrayBufferWriter<byte>();
        action(new Utf8YamlWriter(buffer));
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static readonly StyleMode[] Modes = [StyleMode.Dump, StyleMode.Edit];

    private static byte[] U8(string s) => Encoding.UTF8.GetBytes(s);

    private static (ScalarWriteStyle Style, string Text) Scalar(
        string value,
        ScalarWriteStyle preferred,
        StyleMode mode,
        EmitContext context = EmitContext.BlockValue,
        int parent = 0,
        int content = 2)
    {
        ScalarWriteStyle used = ScalarWriteStyle.Plain;
        string text = Write(w => used = w.WriteString(U8(value), preferred, context, mode, parent, content));
        return (used, text);
    }

    // Edit semantics: judged by the Core schema alone (the cases named in the kit's edit suite).
    [Theory]
    [InlineData("k: v", "Plain", "SingleQuoted", "'k: v'")]
    [InlineData("", "Plain", "SingleQuoted", "''")]
    [InlineData("# x", "Plain", "SingleQuoted", "'# x'")]
    [InlineData(" x", "Plain", "SingleQuoted", "' x'")]
    [InlineData("x ", "Plain", "SingleQuoted", "'x '")]
    [InlineData("null", "Plain", "SingleQuoted", "'null'")]
    [InlineData("12", "Plain", "SingleQuoted", "'12'")]
    [InlineData("1.0", "Plain", "SingleQuoted", "'1.0'")]
    [InlineData("true", "Plain", "SingleQuoted", "'true'")]
    [InlineData("yes", "Plain", "Plain", "yes")]
    [InlineData("0755", "Plain", "SingleQuoted", "'0755'")]
    [InlineData("1:30", "Plain", "Plain", "1:30")]
    [InlineData("it's", "SingleQuoted", "SingleQuoted", "'it''s'")]
    [InlineData("a\\b", "DoubleQuoted", "DoubleQuoted", "\"a\\\\b\"")]
    [InlineData("x\ny", "DoubleQuoted", "DoubleQuoted", "\"x\\ny\"")]
    [InlineData("x\ny", "Plain", "DoubleQuoted", "\"x\\ny\"")]
    [InlineData("x\ny", "SingleQuoted", "DoubleQuoted", "\"x\\ny\"")]
    [InlineData("say \"hi\"", "DoubleQuoted", "DoubleQuoted", "\"say \\\"hi\\\"\"")]
    [InlineData("old", "Plain", "Plain", "old")]
    public void Edit_mode_keeps_or_falls_back_to_the_nearest_style(string value, string preferred, string expectedStyle, string expectedText)
    {
        (ScalarWriteStyle style, string text) = Scalar(value, Enum.Parse<ScalarWriteStyle>(preferred), StyleMode.Edit);
        Assert.Equal(Enum.Parse<ScalarWriteStyle>(expectedStyle), style);
        Assert.Equal(expectedText, text);
    }

    // Dump semantics: plain only if every common reader reads a string.
    [Theory]
    [InlineData("yes", "SingleQuoted", "'yes'")]
    [InlineData("No", "SingleQuoted", "'No'")]
    [InlineData("0755", "SingleQuoted", "'0755'")]
    [InlineData("1:30", "SingleQuoted", "'1:30'")]
    [InlineData("1_000", "SingleQuoted", "'1_000'")]
    [InlineData("2001-12-14", "SingleQuoted", "'2001-12-14'")]
    [InlineData("<<", "SingleQuoted", "'<<'")]
    [InlineData("~", "SingleQuoted", "'~'")]
    [InlineData("hello world", "Plain", "hello world")]
    [InlineData("http://example.com/a?b=c", "Plain", "http://example.com/a?b=c")]
    [InlineData("a#b", "Plain", "a#b")]
    [InlineData("a: b", "SingleQuoted", "'a: b'")]
    [InlineData("a #b", "SingleQuoted", "'a #b'")]
    [InlineData("- a", "SingleQuoted", "'- a'")]
    [InlineData("-a", "Plain", "-a")]
    [InlineData("---", "SingleQuoted", "'---'")]
    [InlineData("&a", "SingleQuoted", "'&a'")]
    [InlineData("é日本\U0001F600", "Plain", "é日本\U0001F600")]
    public void Dump_mode_quotes_what_any_reader_could_misread(string value, string expectedStyle, string expectedText)
    {
        (ScalarWriteStyle style, string text) = Scalar(value, ScalarWriteStyle.Plain, StyleMode.Dump);
        Assert.Equal(Enum.Parse<ScalarWriteStyle>(expectedStyle), style);
        Assert.Equal(expectedText, text);
    }

    [Theory]
    [InlineData("a,b", "BlockValue", true)]
    [InlineData("a,b", "FlowValue", false)]
    [InlineData("[a]", "BlockValue", false)]
    [InlineData("a]", "BlockValue", true)]
    [InlineData("a]", "FlowValue", false)]
    [InlineData("a{b}", "FlowKey", false)]
    [InlineData("a:b", "BlockKey", true)]
    [InlineData("a:b", "FlowKey", false)]
    [InlineData("a:", "BlockKey", false)]
    [InlineData("?x", "BlockKey", true)]
    [InlineData("? x", "BlockKey", false)]
    [InlineData("-,", "FlowValue", false)]
    [InlineData("-,", "BlockValue", true)]
    [InlineData("a\tb", "BlockValue", false)]
    [InlineData("a\nb", "BlockValue", false)]
    public void Plain_depends_on_the_context(string value, string context, bool plain) =>
        Assert.Equal(plain, Utf8YamlWriter.CanBePlain(U8(value), Enum.Parse<EmitContext>(context), StyleMode.Edit));

    [Theory]
    [InlineData("this is a test2\n", "|\n  this is a test2\n")]
    [InlineData("this is a test2", "|-\n  this is a test2\n")]
    [InlineData("x\n\n", "|+\n  x\n\n")]
    [InlineData("x\n\n\n", "|+\n  x\n\n\n")]
    [InlineData("x\ny\n", "|\n  x\n  y\n")]
    [InlineData("x\n\ny\n", "|\n  x\n\n  y\n")]
    [InlineData("\nx\n", "|\n\n  x\n")]
    [InlineData("  y\n", "|2\n    y\n")]
    [InlineData("  y", "|2-\n    y\n")]
    [InlineData("\n  y\n", "|2\n\n    y\n")]
    [InlineData("a\n  b\n", "|\n  a\n    b\n")]
    [InlineData("a\tb\nc\t\n", "|\n  a\tb\n  c\t\n")]
    [InlineData("a #b: c\n", "|\n  a #b: c\n")]
    public void Literal_chooses_its_header_from_the_value(string value, string expected)
    {
        string text = Write(w => Assert.True(w.WriteLiteral(U8(value), 0, 2)));
        Assert.Equal(expected, text);
    }

    [Theory]
    [InlineData("new text\n", ">\n  new text\n")]
    [InlineData("new text", ">-\n  new text\n")]
    [InlineData("one\ntwo\n", ">\n  one\n\n  two\n")]
    [InlineData("one\n\ntwo\n", ">\n  one\n\n\n  two\n")]
    [InlineData("one\n  two\nthree\n", ">\n  one\n    two\n  three\n")]
    [InlineData("one\n  two\n  three\nfour\n", ">\n  one\n    two\n    three\n  four\n")]
    [InlineData("one\n\n  two\n", ">\n  one\n\n    two\n")]
    [InlineData("\none\n", ">\n\n  one\n")]
    [InlineData("one\ntwo\n\n", ">+\n  one\n\n  two\n\n")]
    [InlineData("  one\ntwo\n", ">2\n    one\n  two\n")]
    public void Folded_reproduces_the_exact_line_structure(string value, string expected)
    {
        string text = Write(w => Assert.True(w.WriteFolded(U8(value), 0, 2)));
        Assert.Equal(expected, text);
    }

    [Fact]
    public void Block_scalars_use_the_indent_they_are_given()
    {
        Assert.Equal("|\n    x\n", Write(w => Assert.True(w.WriteLiteral(U8("x\n"), 2, 4))));
        Assert.Equal("|3\n     x\n", Write(w => Assert.True(w.WriteLiteral(U8(" x\n"), 1, 4))));
        Assert.Equal("|1\n      x\n", Write(w => Assert.True(w.WriteLiteral(U8(" x\n"), 4, 5))));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("\n\n")]
    [InlineData("a\rb")]
    [InlineData("a\0b")]
    [InlineData("a\u0085b")]
    [InlineData("a\u2028b")]
    [InlineData("a\n \nb")]
    [InlineData("a\n\t\nb")]
    [InlineData("a\n   ")]
    [InlineData("\ta")]
    [InlineData("a\n\tb")]
    public void Block_scalars_refuse_text_they_cannot_hold_and_write_nothing(string value)
    {
        string literal = Write(w => Assert.False(w.WriteLiteral(U8(value), 0, 2)));
        string folded = Write(w => Assert.False(w.WriteFolded(U8(value), 0, 2)));
        Assert.Equal(string.Empty, literal + folded);
        Assert.Equal(ScalarWriteStyle.DoubleQuoted, Utf8YamlWriter.ChooseStyle(U8(value), ScalarWriteStyle.Literal, EmitContext.BlockValue, StyleMode.Edit));
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 0)]
    [InlineData(0, 10)]
    [InlineData(3, 2)]
    public void Block_scalars_refuse_indents_that_cannot_be_written(int parent, int content)
    {
        Assert.False(Utf8YamlWriter.CanWriteBlock(U8("x\n"), parent, content));
        Assert.Equal(string.Empty, Write(w => Assert.False(w.WriteLiteral(U8("x\n"), parent, content))));
    }

    [Theory]
    [InlineData("BlockKey")]
    [InlineData("FlowValue")]
    [InlineData("FlowKey")]
    public void Block_styles_are_only_for_block_values(string context) =>
        Assert.Equal(ScalarWriteStyle.DoubleQuoted, Utf8YamlWriter.ChooseStyle(U8("x\n"), ScalarWriteStyle.Literal, Enum.Parse<EmitContext>(context), StyleMode.Edit));

    [Fact]
    public void Dump_writes_text_with_line_breaks_as_a_literal_block_and_edit_keeps_it_on_one_line()
    {
        Assert.Equal((ScalarWriteStyle.Literal, "|\n  a\n  b\n"), Scalar("a\nb\n", ScalarWriteStyle.Plain, StyleMode.Dump));
        Assert.Equal((ScalarWriteStyle.DoubleQuoted, "\"a\\nb\\n\""), Scalar("a\nb\n", ScalarWriteStyle.Plain, StyleMode.Edit));
        Assert.Equal((ScalarWriteStyle.DoubleQuoted, "\"a\\nb\\n\""), Scalar("a\nb\n", ScalarWriteStyle.Plain, StyleMode.Dump, EmitContext.FlowValue));
        Assert.Equal((ScalarWriteStyle.DoubleQuoted, "\"a\\nb\\n\""), Scalar("a\nb\n", ScalarWriteStyle.Plain, StyleMode.Dump, EmitContext.BlockKey));
        Assert.Equal((ScalarWriteStyle.DoubleQuoted, "\"a\\Lb\""), Scalar("a\u2028b", ScalarWriteStyle.Plain, StyleMode.Dump));
    }

    [Theory]
    [InlineData("a\0b", "\"a\\0b\"")]
    [InlineData("\a\b\t\n\v\f\r\u001B", "\"\\a\\b\\t\\n\\v\\f\\r\\e\"")]
    [InlineData("\u0001\u001F\u007F\u0080\u009F", "\"\\x01\\x1F\\x7F\\x80\\x9F\"")]
    [InlineData("\u0085\u2028\u2029", "\"\\N\\L\\P\"")]
    [InlineData("\uFEFF\uFFFE\uFFFF", "\"\\uFEFF\\uFFFE\\uFFFF\"")]
    [InlineData("\"\\", "\"\\\"\\\\\"")]
    [InlineData("\u00A0é日\U0001F600\U0010FFFF", "\"\u00A0é日\U0001F600\U0010FFFF\"")]
    [InlineData("", "\"\"")]
    public void Double_quoted_escapes_only_what_it_must(string value, string expected) =>
        Assert.Equal(expected, Write(w => w.WriteDoubleQuoted(U8(value))));

    [Fact]
    public void Double_quoted_writes_malformed_utf8_as_the_replacement_character()
    {
        byte[] bad = [(byte)'a', 0xFF, (byte)'b', 0xC3, 0xE2, 0x82];
        Assert.Equal("\"a\\uFFFDb\\uFFFD\\uFFFD\"", Write(w => w.WriteDoubleQuoted(bad)));
    }

    [Fact]
    public void Single_quoted_refuses_what_needs_an_escape()
    {
        Assert.Equal("'a''b'", Write(w => Assert.True(w.WriteSingleQuoted(U8("a'b")))));
        Assert.Equal("''''", Write(w => Assert.True(w.WriteSingleQuoted(U8("'")))));
        Assert.Equal("'a\tb'", Write(w => Assert.True(w.WriteSingleQuoted(U8("a\tb")))));
        foreach (string bad in new[] { "a\nb", "a\rb", "\0", "\u0085", "\uFEFF" })
        {
            Assert.Equal(string.Empty, Write(w => Assert.False(w.WriteSingleQuoted(U8(bad)))));
        }
    }

    [Fact]
    public void Plain_writer_refuses_unsafe_text()
    {
        Assert.Equal("abc", Write(w => Assert.True(w.WritePlain(U8("abc"), EmitContext.BlockValue, StyleMode.Dump))));
        Assert.Equal(string.Empty, Write(w => Assert.False(w.WritePlain(U8("yes"), EmitContext.BlockValue, StyleMode.Dump))));
        Assert.Equal("yes", Write(w => Assert.True(w.WritePlain(U8("yes"), EmitContext.BlockValue, StyleMode.Edit))));
        Assert.Equal(string.Empty, Write(w => Assert.False(w.WritePlain(U8("a: b"), EmitContext.BlockValue, StyleMode.Edit))));
    }

    [Theory]
    [InlineData(0L, "0")]
    [InlineData(7L, "7")]
    [InlineData(-12L, "-12")]
    [InlineData(long.MaxValue, "9223372036854775807")]
    [InlineData(long.MinValue, "-9223372036854775808")]
    public void Int64_is_written_in_decimal(long value, string expected) =>
        Assert.Equal(expected, Write(w => w.WriteInt64(value)));

    [Fact]
    public void Unsigned_big_null_and_bool_are_written_in_their_core_forms()
    {
        Assert.Equal("18446744073709551615", Write(w => w.WriteUInt64(ulong.MaxValue)));
        Assert.Equal("null", Write(w => w.WriteNull()));
        Assert.Equal("true", Write(w => w.WriteBool(true)));
        Assert.Equal("false", Write(w => w.WriteBool(false)));
        Assert.Equal("0", Write(w => w.WriteBigInteger(BigInteger.Zero)));
        Assert.Equal("-1", Write(w => w.WriteBigInteger(BigInteger.MinusOne)));
        BigInteger huge = BigInteger.Pow(10, 400) + 12345;
        Assert.Equal(huge.ToString(CultureInfo.InvariantCulture), Write(w => w.WriteBigInteger(-(-huge))));
        Assert.Equal((-huge).ToString(CultureInfo.InvariantCulture), Write(w => w.WriteBigInteger(-huge)));
    }

    [Theory]
    [InlineData(1.0, "1.0")]
    [InlineData(0.0, "0.0")]
    [InlineData(-0.0, "-0.0")]
    [InlineData(100.0, "100.0")]
    [InlineData(0.5, "0.5")]
    [InlineData(-1.5, "-1.5")]
    [InlineData(0.1, "0.1")]
    [InlineData(1e16, "10000000000000000.0")]
    [InlineData(1.5e-7, "1.5E-07")]
    [InlineData(6.02e23, "6.02E+23")]
    [InlineData(double.MaxValue, "1.7976931348623157E+308")]
    [InlineData(double.Epsilon, "5E-324")]
    [InlineData(double.PositiveInfinity, ".inf")]
    [InlineData(double.NegativeInfinity, "-.inf")]
    [InlineData(double.NaN, ".nan")]
    public void Double_is_written_shortest_and_never_as_an_integer(double value, string expected) =>
        Assert.Equal(expected, Write(w => w.WriteDouble(value)));

    [Theory]
    [InlineData(1f, "1.0")]
    [InlineData(0.1f, "0.1")]
    [InlineData(-3.5f, "-3.5")]
    [InlineData(float.PositiveInfinity, ".inf")]
    [InlineData(float.NaN, ".nan")]
    public void Single_is_written_like_a_double(float value, string expected) =>
        Assert.Equal(expected, Write(w => w.WriteSingle(value)));

    [Fact]
    public void Written_numbers_resolve_back_to_the_same_value()
    {
        var rnd = new Random(31);
        byte[] bits = new byte[8];
        int checkedCount = 0;
        for (int n = 0; n < 50_000; n++)
        {
            rnd.NextBytes(bits);
            double d = n % 3 == 0 ? rnd.NextDouble() * 1000 - 500 : BitConverter.Int64BitsToDouble(BitConverter.ToInt64(bits));
            string text = Write(w => w.WriteDouble(d));
            byte[] b = U8(text);
            Assert.Equal(ScalarKind.Float, CoreSchema.ResolvePlain(b));
            Assert.True(CoreSchema.TryParseDouble(b, out double back), text);
            if (double.IsNaN(d))
            {
                Assert.True(double.IsNaN(back));
            }
            else
            {
                Assert.Equal(BitConverter.DoubleToInt64Bits(d), BitConverter.DoubleToInt64Bits(back));
            }

            long l = BitConverter.ToInt64(bits);
            string it = Write(w => w.WriteInt64(l));
            Assert.Equal(ScalarKind.Int, CoreSchema.ResolvePlain(U8(it)));
            Assert.True(CoreSchema.TryParseInt64(U8(it), out long lb));
            Assert.Equal(l, lb);

            BigInteger big = new BigInteger(bits, isUnsigned: false) * BigInteger.Pow(2, rnd.Next(0, 300)) * (n % 2 == 0 ? 1 : -1);
            string bt = Write(w => w.WriteBigInteger(big));
            Assert.True(CoreSchema.TryParseBigInteger(U8(bt), out BigInteger bb));
            Assert.Equal(big, bb);
            checkedCount++;
        }

        Assert.Equal(50_000, checkedCount);
    }

    [Fact]
    public void Scalar_writers_do_not_allocate_in_steady_state()
    {
        var buffer = new ArrayBufferWriter<byte>(1 << 16);
        var writer = new Utf8YamlWriter(buffer);
        byte[][] values = [U8("hello"), U8("yes"), U8("it's"), U8("a\nb\n"), U8("x: y"), U8("日本\U0001F600"), U8("  lead\n"), U8("tab\there"), U8("\"q\" \\ \u0001")];
        void RunAll()
        {
            buffer.Clear();
            foreach (byte[] v in values)
            {
                foreach (StyleMode mode in Modes)
                {
                    writer.WriteString(v, ScalarWriteStyle.Plain, EmitContext.BlockValue, mode);
                    writer.WriteString(v, ScalarWriteStyle.Folded, EmitContext.BlockValue, mode);
                }
            }

            writer.WriteInt64(-123456789);
            writer.WriteDouble(12345.678);
            writer.WriteDouble(double.NaN);
            writer.WriteBool(true);
            writer.WriteNull();
        }

        for (int i = 0; i < 3; i++)
        {
            RunAll();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 500; i++)
        {
            RunAll();
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
