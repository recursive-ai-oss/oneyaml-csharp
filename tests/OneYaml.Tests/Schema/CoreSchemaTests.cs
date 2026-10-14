using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using OneYaml.Schema;
using Xunit;

namespace OneYaml.Tests.Schema;

public sealed partial class CoreSchemaTests
{
    private static byte[] U8(string s) => Encoding.UTF8.GetBytes(s);

    public static TheoryData<string, string> Resolutions => new()
    {
        // null
        { "", "Null" }, { "~", "Null" }, { "null", "Null" },
        { "Null", "Null" }, { "NULL", "Null" },
        { "nUll", "String" }, { "nil", "String" }, { "None", "String" },
        { "~~", "String" }, { "NULl", "String" },
        // bool
        { "true", "Bool" }, { "True", "Bool" }, { "TRUE", "Bool" },
        { "false", "Bool" }, { "False", "Bool" }, { "FALSE", "Bool" },
        { "tRUE", "String" }, { "fALSE", "String" }, { "t", "String" },
        { "f", "String" }, { "T", "String" }, { "F", "String" },
        // YAML 1.1 spellings are strings
        { "yes", "String" }, { "No", "String" }, { "on", "String" },
        { "OFF", "String" }, { "y", "String" }, { "N", "String" },
        { "yEs", "String" }, { "0b11", "String" }, { "0b1010", "String" },
        { "1_000", "String" }, { "1:30", "String" }, { "190:20:30", "String" },
        { "685230:15", "String" }, { "0x_1F", "String" }, { "1_000.5", "String" },
        // timestamps
        { "2001-12-14", "String" }, { "2001-12-14t21:59:43.10-05:00", "String" },
        { "2001-12-14 21:59:43.10 -5", "String" },
        // integers
        { "12", "Int" }, { "-7", "Int" }, { "+3", "Int" }, { "0", "Int" },
        { "0o14", "Int" }, { "0xFF", "Int" }, { "0777", "Int" },
        { "007", "Int" }, { "-0", "Int" }, { "+0", "Int" }, { "00", "Int" },
        { "-012", "Int" }, { "0x1f", "Int" }, { "0xaBc", "Int" },
        { "18446744073709551616", "Int" }, { "0xFFFFFFFFFFFFFFFFFF", "Int" },
        // non-decimal integers take no sign and use lower-case prefixes
        { "-0x1F", "String" }, { "+0x1F", "String" }, { "-0o17", "String" },
        { "0x", "String" }, { "0o", "String" }, { "0o8", "String" },
        { "0xG", "String" }, { "0X1F", "String" }, { "0O17", "String" },
        // floats
        { "1.5", "Float" }, { ".5", "Float" }, { "-.5", "Float" },
        { "+.5", "Float" }, { "1.", "Float" }, { "+1.5", "Float" },
        { "1e3", "Float" }, { "1E-3", "Float" }, { "1e+3", "Float" },
        { ".1e2", "Float" }, { "-1.5e-2", "Float" }, { "0.0", "Float" },
        { "6.02E+23", "Float" }, { "12.", "Float" },
        { ".inf", "Float" }, { ".Inf", "Float" }, { ".INF", "Float" },
        { "+.inf", "Float" }, { "-.INF", "Float" }, { "-.Inf", "Float" },
        { ".nan", "Float" }, { ".NaN", "Float" }, { ".NAN", "Float" },
        { ".iNf", "String" }, { ".nAn", "String" }, { "-.nan", "String" },
        { "+.nan", "String" }, { "nan", "String" }, { "inf", "String" },
        { "-inf", "String" }, { ".infinity", "String" },
        // not numbers
        { "e3", "String" }, { "1e", "String" }, { "1.5.2", "String" },
        { ".", "String" }, { "-.", "String" }, { "+", "String" },
        { "-", "String" }, { "1_0.5", "String" }, { "1,000", "String" },
        { "0x1.8", "String" }, { "++1", "String" }, { "1 2", "String" },
        { ".e3", "String" }, { "1e+", "String" }, { "--1", "String" },
        // ordinary text
        { "hello", "String" }, { "ünï", "String" }, { "- x", "String" },
        { "true story", "String" }, { "nullable", "String" }, { "~x", "String" },
    };

    [Theory]
    [MemberData(nameof(Resolutions))]
    public void ResolvePlain_follows_the_core_schema(string text, string expected) =>
        Assert.Equal(Enum.Parse<ScalarKind>(expected), CoreSchema.ResolvePlain(U8(text)));

    // The spec's own regular expressions (section 10.3.2) as an independent oracle.
    [GeneratedRegex(@"^(null|Null|NULL|~|)$")]
    private static partial Regex NullRe();

    [GeneratedRegex(@"^(true|True|TRUE|false|False|FALSE)$")]
    private static partial Regex BoolRe();

    [GeneratedRegex(@"^([-+]?[0-9]+|0o[0-7]+|0x[0-9a-fA-F]+)$")]
    private static partial Regex IntRe();

    [GeneratedRegex(@"^([-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?|[-+]?\.(inf|Inf|INF)|\.(nan|NaN|NAN))$")]
    private static partial Regex FloatRe();

    private static ScalarKind SpecKind(string s) =>
        NullRe().IsMatch(s) ? ScalarKind.Null
        : BoolRe().IsMatch(s) ? ScalarKind.Bool
        : IntRe().IsMatch(s) ? ScalarKind.Int
        : FloatRe().IsMatch(s) ? ScalarKind.Float
        : ScalarKind.String;

    [Fact]
    public void ResolvePlain_agrees_with_the_spec_expressions_on_generated_text()
    {
        // Fragments weighted toward the characters that make up numbers and the near misses.
        string[] pieces =
        [
            "0", "1", "7", "9", "8", "a", "F", "x", "o", "X", "O", ".", "+", "-", "e", "E", "_", ":", "n", "u", "l",
            "t", "r", "f", "s", "N", "T", "~", "inf", "nan", "NaN", "Inf", "null", "true", "False", "0x", "0o", " ", "é",
            "NULL", "TRUE", "FALSE", ".inf", ".nan",
        ];
        var rnd = new Random(20260214);
        var sb = new StringBuilder();
        int checkedCount = 0;
        for (int n = 0; n < 300_000; n++)
        {
            sb.Clear();
            int parts = rnd.Next(0, 7);
            for (int k = 0; k < parts; k++)
            {
                sb.Append(pieces[rnd.Next(pieces.Length)]);
            }

            string s = sb.ToString();
            ScalarKind actual = CoreSchema.ResolvePlain(U8(s));
            Assert.True(SpecKind(s) == actual, $"'{s}': spec {SpecKind(s)}, resolver {actual}");
            checkedCount++;
        }

        Assert.Equal(300_000, checkedCount);
    }

    [Theory]
    [InlineData("0", 0L)]
    [InlineData("-0", 0L)]
    [InlineData("+3", 3L)]
    [InlineData("007", 7L)]
    [InlineData("-012", -12L)]
    [InlineData("0o14", 12L)]
    [InlineData("0xFF", 255L)]
    [InlineData("0x7FFFFFFFFFFFFFFF", long.MaxValue)]
    [InlineData("9223372036854775807", long.MaxValue)]
    [InlineData("-9223372036854775808", long.MinValue)]
    [InlineData("0o777777777777777777777", long.MaxValue)]
    public void TryParseInt64_reads_core_integers(string text, long expected)
    {
        Assert.True(CoreSchema.TryParseInt64(U8(text), out long value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-")]
    [InlineData("9223372036854775808")]
    [InlineData("-9223372036854775809")]
    [InlineData("0x8000000000000000")]
    [InlineData("0xFFFFFFFFFFFFFFFF")]
    [InlineData("0o1000000000000000000000")]
    [InlineData("18446744073709551616")]
    [InlineData("1.5")]
    [InlineData("1e3")]
    [InlineData("-0x1")]
    [InlineData("0x")]
    [InlineData("0xG")]
    [InlineData("12a")]
    [InlineData("1_0")]
    public void TryParseInt64_refuses_what_is_not_a_64_bit_integer(string text) =>
        Assert.False(CoreSchema.TryParseInt64(U8(text), out _));

    [Theory]
    [InlineData("18446744073709551616", "18446744073709551616")]
    [InlineData("-9223372036854775809", "-9223372036854775809")]
    [InlineData("0xFFFFFFFFFFFFFFFFFF", "4722366482869645213695")]
    [InlineData("0o7777777777777777777777", "73786976294838206463")]
    [InlineData("123456789012345678901234567890", "123456789012345678901234567890")]
    [InlineData("+000000000000000000000000000001", "1")]
    [InlineData("0x0", "0")]
    [InlineData("0xff", "255")]
    [InlineData("0o10", "8")]
    [InlineData("0x8000000000000000", "9223372036854775808")]
    public void TryParseBigInteger_is_exact(string text, string expected)
    {
        Assert.True(CoreSchema.TryParseBigInteger(U8(text), out BigInteger value));
        Assert.Equal(BigInteger.Parse(expected, CultureInfo.InvariantCulture), value);
    }

    [Fact]
    public void TryParseBigInteger_handles_very_long_digit_runs_and_refuses_junk()
    {
        string digits = new('7', 5000);
        Assert.True(CoreSchema.TryParseBigInteger(U8(digits), out BigInteger v));
        Assert.Equal(BigInteger.Parse(digits, CultureInfo.InvariantCulture), v);
        string hex = "0x" + new string('F', 400);
        Assert.True(CoreSchema.TryParseBigInteger(U8(hex), out v));
        Assert.Equal((BigInteger.One << 1600) - 1, v);
        foreach (string bad in new[] { "", "+", "0x", "0o9", "1.0", "-0x1", "1 ", "abc" })
        {
            Assert.False(CoreSchema.TryParseBigInteger(U8(bad), out _), bad);
        }
    }

    [Fact]
    public void Integer_parsers_agree_with_the_resolver_and_each_other_on_generated_text()
    {
        var rnd = new Random(7);
        string[] pieces = ["0", "1", "9", "x", "o", "7", "F", "a", "-", "+", "8", "G", "."];
        var sb = new StringBuilder();
        for (int n = 0; n < 100_000; n++)
        {
            sb.Clear();
            int parts = rnd.Next(1, 24);
            for (int k = 0; k < parts; k++)
            {
                sb.Append(pieces[rnd.Next(pieces.Length)]);
            }

            string s = sb.ToString();
            byte[] b = U8(s);
            bool isInt = CoreSchema.ResolvePlain(b) == ScalarKind.Int;
            bool big = CoreSchema.TryParseBigInteger(b, out BigInteger bv);
            Assert.Equal(isInt, big);
            bool small = CoreSchema.TryParseInt64(b, out long lv);
            if (small)
            {
                Assert.True(big);
                Assert.Equal(new BigInteger(lv), bv);
            }
            else if (big)
            {
                Assert.True(bv > long.MaxValue || bv < long.MinValue, s);
            }
        }
    }

    [Theory]
    [InlineData("1.5", 1.5)]
    [InlineData(".5", 0.5)]
    [InlineData("-.5", -0.5)]
    [InlineData("+.5", 0.5)]
    [InlineData("1.", 1.0)]
    [InlineData("1e3", 1000.0)]
    [InlineData("6.02E+23", 6.02e23)]
    [InlineData("-1.5e-2", -0.015)]
    [InlineData("12", 12.0)]
    [InlineData("-0", -0.0)]
    [InlineData("9007199254740993", 9007199254740992.0)]
    [InlineData("0.1", 0.1)]
    [InlineData("4.9e-324", double.Epsilon)]
    [InlineData("1.7976931348623157e308", double.MaxValue)]
    [InlineData("2.2250738585072011e-308", 2.2250738585072011e-308)]
    [InlineData("1e400", double.PositiveInfinity)]
    [InlineData(".inf", double.PositiveInfinity)]
    [InlineData("+.INF", double.PositiveInfinity)]
    [InlineData("-.Inf", double.NegativeInfinity)]
    public void TryParseDouble_is_correctly_rounded(string text, double expected)
    {
        Assert.True(CoreSchema.TryParseDouble(U8(text), out double value));
        Assert.Equal(BitConverter.DoubleToInt64Bits(expected), BitConverter.DoubleToInt64Bits(value));
    }

    [Fact]
    public void TryParseDouble_reads_nan_and_refuses_what_is_not_a_float()
    {
        foreach (string nan in new[] { ".nan", ".NaN", ".NAN" })
        {
            Assert.True(CoreSchema.TryParseDouble(U8(nan), out double v));
            Assert.True(double.IsNaN(v));
        }

        foreach (string bad in new[] { "", ".", "e3", "1e", "+.nan", "-.nan", ".iNf", "0x10", "0o7", "1_0", "inf", "nan", "Infinity", "1 ", " 1" })
        {
            Assert.False(CoreSchema.TryParseDouble(U8(bad), out _), bad);
        }
    }

    [Fact]
    public void TryParseDouble_matches_the_runtime_on_generated_numbers()
    {
        var rnd = new Random(99);
        var sb = new StringBuilder();
        for (int n = 0; n < 60_000; n++)
        {
            sb.Clear();
            if (rnd.Next(4) == 0)
            {
                sb.Append(rnd.Next(2) == 0 ? '-' : '+');
            }

            int intDigits = rnd.Next(0, 22);
            for (int k = 0; k < intDigits; k++)
            {
                sb.Append((char)('0' + rnd.Next(10)));
            }

            int frac = rnd.Next(0, 22);
            if (frac > 0 || (rnd.Next(3) == 0 && intDigits > 0))
            {
                sb.Append('.');
                for (int k = 0; k < frac; k++)
                {
                    sb.Append((char)('0' + rnd.Next(10)));
                }
            }

            if (rnd.Next(3) == 0)
            {
                sb.Append(rnd.Next(2) == 0 ? 'e' : 'E');
                if (rnd.Next(2) == 0)
                {
                    sb.Append(rnd.Next(2) == 0 ? '-' : '+');
                }

                sb.Append(rnd.Next(0, 340));
            }

            string s = sb.ToString();
            if (SpecKind(s) is not (ScalarKind.Float or ScalarKind.Int))
            {
                Assert.False(CoreSchema.TryParseDouble(U8(s), out _), s);
                continue;
            }

            Assert.True(CoreSchema.TryParseDouble(U8(s), out double actual), s);
            double expected = double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
            Assert.True(BitConverter.DoubleToInt64Bits(expected) == BitConverter.DoubleToInt64Bits(actual), s);
        }
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("TRUE", true)]
    [InlineData("false", false)]
    [InlineData("False", false)]
    [InlineData("FALSE", false)]
    public void TryParseBool_reads_the_core_spellings(string text, bool expected)
    {
        Assert.True(CoreSchema.TryParseBool(U8(text), out bool value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("tRUE")]
    [InlineData("t")]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("on")]
    public void TryParseBool_refuses_other_text(string text) => Assert.False(CoreSchema.TryParseBool(U8(text), out _));

    [Theory]
    [InlineData("tag:yaml.org,2002:str", "Str")]
    [InlineData("tag:yaml.org,2002:int", "Int")]
    [InlineData("tag:yaml.org,2002:float", "Float")]
    [InlineData("tag:yaml.org,2002:bool", "Bool")]
    [InlineData("tag:yaml.org,2002:null", "Null")]
    [InlineData("!!str", "Str")]
    [InlineData("!!int", "Int")]
    [InlineData("!", "Str")]
    [InlineData("!!timestamp", "Unknown")]
    [InlineData("!!binary", "Unknown")]
    [InlineData("!local", "Unknown")]
    [InlineData("tag:yaml.org,2002:map", "Unknown")]
    [InlineData("tag:example.com,2000:int", "Unknown")]
    [InlineData("", "Unknown")]
    public void ParseTag_knows_the_core_scalar_tags(string tag, string expected) =>
        Assert.Equal(Enum.Parse<CoreTag>(expected), CoreSchema.ParseTag(U8(tag)));

    [Theory]
    [InlineData("Float", "1", true, "Float")]
    [InlineData("Float", ".inf", true, "Float")]
    [InlineData("Float", "1.5e3", true, "Float")]
    [InlineData("Float", "0x10", false, "Float")]
    [InlineData("Float", "abc", false, "Float")]
    [InlineData("Int", "3", true, "Int")]
    [InlineData("Int", "0x10", true, "Int")]
    [InlineData("Int", "-7", true, "Int")]
    [InlineData("Int", "1.5", false, "Int")]
    [InlineData("Int", "", false, "Int")]
    [InlineData("Null", "", true, "Null")]
    [InlineData("Null", "~", true, "Null")]
    [InlineData("Null", "x", false, "Null")]
    [InlineData("Bool", "true", true, "Bool")]
    [InlineData("Bool", "yes", false, "Bool")]
    [InlineData("Str", "0o10", true, "String")]
    [InlineData("Str", "", true, "String")]
    [InlineData("Unknown", "1", false, "String")]
    public void TryResolveTagged_lets_the_tag_decide_the_type(string tag, string text, bool ok, string kind)
    {
        Assert.Equal(ok, CoreSchema.TryResolveTagged(Enum.Parse<CoreTag>(tag), U8(text), out ScalarKind actual));
        Assert.Equal(Enum.Parse<ScalarKind>(kind), actual);
    }

    [Fact]
    public void Resolving_and_parsing_do_not_allocate()
    {
        byte[][] inputs = ["hello"u8.ToArray(), "12"u8.ToArray(), "-9223372036854775808"u8.ToArray(), "6.02E+23"u8.ToArray(),
            "true"u8.ToArray(), "null"u8.ToArray(), "0xFF"u8.ToArray(), ".inf"u8.ToArray(), "2001-12-14"u8.ToArray(), ""u8.ToArray()];
        long sink = 0;

        // Warm up so JIT and static initialisation are not counted.
        for (int i = 0; i < 3; i++)
        {
            sink += Run(inputs);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            sink += Run(inputs);
        }

        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0, after - before);
        Assert.NotEqual(-1, sink);

        static long Run(byte[][] inputs)
        {
            long acc = 0;
            foreach (byte[] b in inputs)
            {
                acc += (int)CoreSchema.ResolvePlain(b);
                acc += CoreSchema.TryParseInt64(b, out long l) ? l : 0;
                acc += CoreSchema.TryParseDouble(b, out double d) ? (long)d : 0;
                acc += CoreSchema.TryParseBool(b, out bool t) && t ? 1 : 0;
                acc += OneYaml.Schema.Yaml11.LooksNonString(b) ? 1 : 0;
            }

            return acc;
        }
    }
}
