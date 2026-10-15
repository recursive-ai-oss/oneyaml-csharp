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
    [InlineData(-2, 1)]
    [InlineData(-1, 0)]
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
    [InlineData(double.Epsilon, "5.0E-324")]
    [InlineData(-double.Epsilon, "-5.0E-324")]
    [InlineData(1e17, "1.0E+17")]
    [InlineData(1e-5, "1.0E-05")]
    [InlineData(-1e300, "-1.0E+300")]
    [InlineData(double.PositiveInfinity, ".inf")]
    [InlineData(double.NegativeInfinity, "-.inf")]
    [InlineData(double.NaN, ".nan")]
    public void Double_is_written_shortest_and_never_as_an_integer(double value, string expected) =>
        Assert.Equal(expected, Write(w => w.WriteDouble(value)));

    [Theory]
    [InlineData(1f, "1.0")]
    [InlineData(0.1f, "0.1")]
    [InlineData(-3.5f, "-3.5")]
    [InlineData(1e10f, "1.0E+10")]
    [InlineData(float.Epsilon, "1.0E-45")]
    [InlineData(float.PositiveInfinity, ".inf")]
    [InlineData(float.NaN, ".nan")]
    public void Single_is_written_like_a_double(float value, string expected) =>
        Assert.Equal(expected, Write(w => w.WriteSingle(value)));

    // The YAML 1.1 float pattern (PyYAML's spelling): digits, a point, and an exponent with a sign.
    private static readonly System.Text.RegularExpressions.Regex Float11 = new(
        @"^([-+]?([0-9][0-9_]*)\.[0-9_]*([eE][-+][0-9]+)?|\.[0-9_]+([eE][-+][0-9]+)?|[-+]?\.(inf|Inf|INF)|\.(nan|NaN|NAN))$");

    private static void AssertReadsAsFloatEverywhere(string text)
    {
        Assert.Equal(ScalarKind.Float, CoreSchema.ResolvePlain(U8(text)));
        Assert.True(Float11.IsMatch(text), "YAML 1.1 would not read this as a float: " + text);
    }

    [Fact]
    public void Floats_with_one_digit_and_an_exponent_still_show_a_point_for_every_reader()
    {
        var values = new List<double> { double.Epsilon, -double.Epsilon, double.MaxValue, double.MinValue, 1e15, 1e16, 1e17, 2e22, 1e-5, 1e-7, 5e-324 };
        for (int e = -320; e <= 308; e += 7)
        {
            values.Add(Math.Pow(10, e));
            values.Add(-3 * Math.Pow(10, e));
        }

        foreach (double d in values)
        {
            AssertReadsAsFloatEverywhere(Write(w => w.WriteDouble(d)));
        }

        foreach (float f in new[] { 1e10f, 1e-10f, float.Epsilon, float.MaxValue, 1e20f, 3e-5f })
        {
            string text = Write(w => w.WriteSingle(f));
            AssertReadsAsFloatEverywhere(text);
            Assert.True(CoreSchema.TryParseDouble(U8(text), out double back));
            Assert.Equal(f, (float)back);
        }
    }

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
            AssertReadsAsFloatEverywhere(text);
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
        byte[][] values = [U8("hello"), U8("yes"), U8("it's"), U8("a\nb\n"), U8("x: y"), U8("日本\U0001F600"), U8("  lead\n"), U8("tab\there"), U8("\"q\" \\ \u0001"), U8("+_1"), U8("1_000"), U8(new string('k', 400))];
        void RunAll()
        {
            buffer.Clear();
            foreach (byte[] v in values)
            {
                foreach (StyleMode mode in Modes)
                {
                    writer.WriteString(v, ScalarWriteStyle.Plain, EmitContext.BlockValue, mode);
                    writer.WriteString(v, ScalarWriteStyle.Folded, EmitContext.BlockValue, mode);
                    writer.WriteString(v, ScalarWriteStyle.Literal, EmitContext.BlockValue, mode, 0, 2, new BlockHeader(BlockChomping.Keep, 2));
                    writer.WriteString(v, ScalarWriteStyle.Literal, EmitContext.BlockValue, mode, -1, 2);
                    writer.WriteString(v, ScalarWriteStyle.Plain, EmitContext.BlockKey, mode);
                }
            }

            writer.WriteInt64(-123456789);
            writer.WriteDouble(12345.678);
            writer.WriteDouble(double.NaN);
            writer.WriteDouble(1e17);
            writer.WriteDouble(double.Epsilon);
            writer.WriteBool(true);
            writer.WriteNull();
        }

        SteadyState.AssertAllocatesNothing(RunAll, 500);
    }

    [Theory]
    [InlineData("new\n", "Keep", 0, "|+\n  new\n")]
    [InlineData("new\n", "Clip", 0, "|\n  new\n")]
    [InlineData("new\n", "Strip", 0, "|\n  new\n")]
    [InlineData("new", "Keep", 0, "|-\n  new\n")]
    [InlineData("new", "Clip", 0, "|-\n  new\n")]
    [InlineData("new\n\n", "Clip", 0, "|+\n  new\n\n")]
    [InlineData("new\n\n", "Strip", 0, "|+\n  new\n\n")]
    [InlineData("new\n", "Keep", 2, "|2+\n  new\n")]
    [InlineData("new\n", "Clip", 2, "|2\n  new\n")]
    [InlineData("new\n", "Clip", 3, "|\n  new\n")]
    [InlineData(" new\n", "Clip", 3, "|2\n   new\n")]
    [InlineData(" new", "Strip", 0, "|2-\n   new\n")]
    public void A_block_header_is_kept_while_the_value_reads_back_and_changed_only_as_far_as_it_needs(string value, string chomping, int indicator, string expected)
    {
        // The content sits two columns in, whatever the old indicator said.
        var header = new BlockHeader(Enum.Parse<BlockChomping>(chomping), indicator);
        string text = Write(w => Assert.True(w.WriteLiteral(U8(value), 0, 2, header)));
        Assert.Equal(expected, text);
    }

    [Fact]
    public void Folded_and_WriteString_take_the_existing_header_too()
    {
        var keep = new BlockHeader(BlockChomping.Keep, 0);
        Assert.Equal(">+\n  new\n", Write(w => Assert.True(w.WriteFolded(U8("new\n"), 0, 2, keep))));
        Assert.Equal("|+\n  new\n", Write(w => w.WriteString(U8("new\n"), ScalarWriteStyle.Literal, EmitContext.BlockValue, StyleMode.Edit, 0, 2, keep)));
        Assert.Equal(new BlockHeader(BlockChomping.Keep, 0), Utf8YamlWriter.ResolveHeader(U8("new\n"), 0, 2, keep));
        Assert.Equal(new BlockHeader(BlockChomping.Clip, 0), Utf8YamlWriter.ResolveHeader(U8("new\n"), 0, 2));
    }

    [Theory]
    [InlineData("root\n", "|\n  root\n")]
    [InlineData("root", "|-\n  root\n")]
    [InlineData("a\nb\n\n", "|+\n  a\n  b\n\n")]
    public void A_document_root_can_be_a_block_scalar(string value, string expected)
    {
        Assert.True(Utf8YamlWriter.CanWriteBlock(U8(value), -1, 2));
        Assert.Equal(expected, Write(w => Assert.True(w.WriteLiteral(U8(value), -1, 2))));
        Assert.Equal(ScalarWriteStyle.Literal, Scalar(value, ScalarWriteStyle.Literal, StyleMode.Edit, EmitContext.BlockValue, -1, 2).Style);
    }

    [Theory]
    [InlineData(" lead\n", -1, 2)]
    [InlineData("x\n", -1, 0)]
    public void A_document_root_block_scalar_that_would_need_an_indicator_or_a_marker_column_stays_quoted(string value, int parent, int content)
    {
        Assert.False(Utf8YamlWriter.CanWriteBlock(U8(value), parent, content));
        Assert.Equal(ScalarWriteStyle.DoubleQuoted, Scalar(value, ScalarWriteStyle.Literal, StyleMode.Edit, EmitContext.BlockValue, parent, content).Style);
    }

    [Theory]
    [InlineData("BlockKey")]
    [InlineData("FlowKey")]
    public void A_key_longer_than_1024_characters_asks_for_the_explicit_form_and_writes_nothing(string context)
    {
        var ctx = Enum.Parse<EmitContext>(context);
        foreach (StyleMode mode in Modes)
        {
            foreach (ScalarWriteStyle preferred in new[] { ScalarWriteStyle.Plain, ScalarWriteStyle.SingleQuoted, ScalarWriteStyle.DoubleQuoted })
            {
                (ScalarWriteStyle style, string text) = Scalar(new string('k', 1025), preferred, mode, ctx);
                Assert.Equal(ScalarWriteStyle.ExplicitKeyRequired, style);
                Assert.Equal(string.Empty, text);
            }
        }

        Assert.Equal(ScalarWriteStyle.ExplicitKeyRequired, Utf8YamlWriter.ChooseStyle(U8(new string('k', 5000)), ScalarWriteStyle.Plain, ctx, StyleMode.Edit));
        Assert.False(Utf8YamlWriter.CanBePlain(U8(new string('k', 1025)), ctx, StyleMode.Edit));
        Assert.Equal(string.Empty, Write(w => Assert.False(w.WritePlain(U8(new string('k', 1025)), ctx, StyleMode.Edit))));
    }

    [Fact]
    public void Key_length_counts_characters_quotes_and_escapes()
    {
        const EmitContext key = EmitContext.BlockKey;
        // 1,024 plain characters fit; one more does not.
        Assert.Equal(ScalarWriteStyle.Plain, Scalar(new string('k', 1024), ScalarWriteStyle.Plain, StyleMode.Edit, key).Style);
        Assert.Equal(ScalarWriteStyle.ExplicitKeyRequired, Scalar(new string('k', 1025), ScalarWriteStyle.Plain, StyleMode.Edit, key).Style);

        // Multi-byte characters count once: 600 of them are 1,800 bytes but 600 characters.
        Assert.Equal(ScalarWriteStyle.Plain, Scalar(new string('\u00E9', 600), ScalarWriteStyle.Plain, StyleMode.Edit, key).Style);
        Assert.Equal(ScalarWriteStyle.ExplicitKeyRequired, Scalar(new string('\u00E9', 1025), ScalarWriteStyle.Plain, StyleMode.Edit, key).Style);

        // Single quotes add two characters and double each quote inside.
        string quoted = " " + new string('k', 1020) + " ";
        Assert.Equal(1022, quoted.Length);
        Assert.Equal(ScalarWriteStyle.SingleQuoted, Scalar(quoted, ScalarWriteStyle.Plain, StyleMode.Edit, key).Style);
        Assert.Equal(ScalarWriteStyle.ExplicitKeyRequired, Scalar(" " + new string('k', 1021) + " ", ScalarWriteStyle.Plain, StyleMode.Edit, key).Style);
        string withQuotes = new string('k', 990) + new string('\'', 20);
        Assert.Equal(ScalarWriteStyle.Plain, Scalar(withQuotes, ScalarWriteStyle.Plain, StyleMode.Edit, key).Style);
        Assert.Equal(ScalarWriteStyle.ExplicitKeyRequired, Scalar(withQuotes, ScalarWriteStyle.SingleQuoted, StyleMode.Edit, key).Style);

        // Escapes count as written: each control character is four characters ("\x01") in double quotes.
        (ScalarWriteStyle style, string text) = Scalar(new string('\u0001', 255), ScalarWriteStyle.Plain, StyleMode.Edit, key);
        Assert.Equal(ScalarWriteStyle.DoubleQuoted, style);
        Assert.Equal(1022, text.Length);
        Assert.Equal(ScalarWriteStyle.ExplicitKeyRequired, Scalar(new string('\u0001', 256), ScalarWriteStyle.Plain, StyleMode.Edit, key).Style);

        // The same text as a value has no such limit.
        Assert.Equal(ScalarWriteStyle.Plain, Scalar(new string('k', 5000), ScalarWriteStyle.Plain, StyleMode.Edit).Style);
    }
}
