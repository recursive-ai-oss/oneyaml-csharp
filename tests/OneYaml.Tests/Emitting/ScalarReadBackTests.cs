using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;
using OneYaml.Emitting;
using OneYaml.Schema;
using Xunit;

namespace OneYaml.Tests.Emitting;

/// <summary>
/// Every scalar the writer produces is parsed back by two independent parsers, and the whole event
/// stream must be the one expected, with exactly the written string in it.
/// </summary>
public sealed partial class ScalarReadBackTests
{
    private sealed record Layout(
        string Name,
        EmitContext Context,
        string Prefix,
        string Trail,
        int Parent,
        int Content,
        string[] Before,
        string[] After,
        bool IsKey);

    private static readonly string[] Header = ["+STR", "+DOC"];
    private static readonly string[] Footer = ["-DOC", "-STR"];

    private static string[] Open(params string[] middle) => [.. Header, .. middle];

    private static readonly Layout[] Layouts =
    [
        new("block value", EmitContext.BlockValue, "k: ", "z: 1\n", 0, 2, Open("+MAP", "=VAL :k"), ["=VAL :z", "=VAL :1", "-MAP", .. Footer], false),
        new("block value at the end of the stream", EmitContext.BlockValue, "k: ", string.Empty, 0, 2, Open("+MAP", "=VAL :k"), ["-MAP", .. Footer], false),
        new("nested block value", EmitContext.BlockValue, "a:\n  k: ", "  z: 1\ny: 2\n", 2, 4, Open("+MAP", "=VAL :a", "+MAP", "=VAL :k"), ["=VAL :z", "=VAL :1", "-MAP", "=VAL :y", "=VAL :2", "-MAP", .. Footer], false),
        new("odd indentation", EmitContext.BlockValue, "a:\n  k: ", "  z: 1\n", 2, 5, Open("+MAP", "=VAL :a", "+MAP", "=VAL :k"), ["=VAL :z", "=VAL :1", "-MAP", "-MAP", .. Footer], false),
        new("sequence item", EmitContext.BlockValue, "- ", "- z\n", 0, 2, Open("+SEQ"), ["=VAL :z", "-SEQ", .. Footer], false),
        new("document root", EmitContext.BlockValue, "--- ", string.Empty, -1, 2, Open(), Footer, false),
        new("block key", EmitContext.BlockKey, string.Empty, ": v\nz: 1\n", 0, 2, Open("+MAP"), ["=VAL :v", "=VAL :z", "=VAL :1", "-MAP", .. Footer], true),
        new("flow sequence item", EmitContext.FlowValue, "[a, ", ", z]\n", 0, 2, Open("+SEQ", "=VAL :a"), ["=VAL :z", "-SEQ", .. Footer], false),
        new("flow mapping value", EmitContext.FlowValue, "{k: ", ", z: 1}\n", 0, 2, Open("+MAP", "=VAL :k"), ["=VAL :z", "=VAL :1", "-MAP", .. Footer], false),
        new("flow key", EmitContext.FlowKey, "{", ": v, z: 1}\n", 0, 2, Open("+MAP"), ["=VAL :v", "=VAL :z", "=VAL :1", "-MAP", .. Footer], true),
    ];

    private static readonly ScalarWriteStyle[] Styles =
        [ScalarWriteStyle.Plain, ScalarWriteStyle.SingleQuoted, ScalarWriteStyle.DoubleQuoted, ScalarWriteStyle.Literal, ScalarWriteStyle.Folded];

    // The YAML 1.1 type patterns (yaml.org/type), as an independent check of plain output in dump mode.
    // The YAML 1.1 type patterns as PyYAML's resolver spells them (the type repository's float
    // pattern also matches "." and "1.2.3", which no reader takes as floats), plus the y/n booleans
    // that other readers add.
    [GeneratedRegex(@"^(y|Y|yes|Yes|YES|n|N|no|No|NO|true|True|TRUE|false|False|FALSE|on|On|ON|off|Off|OFF|~|null|Null|NULL|<<|=|)$")]
    private static partial Regex Words11();

    [GeneratedRegex(@"^([-+]?0b[0-1_]+|[-+]?0[0-7_]+|[-+]?(0|[1-9][0-9_]*)|[-+]?0x[0-9a-fA-F_]+|[-+]?[1-9][0-9_]*(:[0-5]?[0-9])+)$")]
    private static partial Regex Int11();

    [GeneratedRegex(@"^([-+]?([0-9][0-9_]*)\.[0-9_]*([eE][-+][0-9]+)?|\.[0-9_]+([eE][-+][0-9]+)?|[-+]?[0-9][0-9_]*(:[0-5]?[0-9])+\.[0-9_]*|[-+]?\.(inf|Inf|INF)|\.(nan|NaN|NAN))$")]
    private static partial Regex Float11();

    [GeneratedRegex(@"^([0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]|[0-9][0-9][0-9][0-9]-[0-9][0-9]?-[0-9][0-9]?([Tt]|[ \t]+)[0-9][0-9]?:[0-9][0-9]:[0-9][0-9](\.[0-9]*)?(([ \t]*)Z|[ \t]*[-+][0-9][0-9]?(:[0-9][0-9])?)?)$")]
    private static partial Regex Timestamp11();

    private static bool AnyReaderTakesAsNonString(string s) =>
        Words11().IsMatch(s) || Int11().IsMatch(s) || Float11().IsMatch(s) || Timestamp11().IsMatch(s);

    private static List<string> Guard(Func<List<string>> read, string where)
    {
        try
        {
            return read();
        }
        catch (Exception e) when (e is not Xunit.Sdk.XunitException)
        {
            throw new InvalidOperationException("parser failed (" + e.GetType().Name + ": " + e.Message + ") on: " + where, e);
        }
    }

    private static char Mark(ScalarWriteStyle s) => s switch
    {
        ScalarWriteStyle.SingleQuoted => '\'',
        ScalarWriteStyle.DoubleQuoted => '"',
        ScalarWriteStyle.Literal => '|',
        ScalarWriteStyle.Folded => '>',
        _ => ':',
    };

    private static string Escape(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s)
        {
            sb.Append(c is >= ' ' and < '\u007F' ? c.ToString() : $"\\u{(int)c:X4}");
        }

        return sb.ToString();
    }

    // Long keys have no implicit form; the harness writes the explicit one the caller is told to write.
    private static string ExplicitKey(Layout layout, string value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        new Utf8YamlWriter(buffer).WriteDoubleQuoted(Encoding.UTF8.GetBytes(value));
        string quoted = Encoding.UTF8.GetString(buffer.WrittenSpan);
        return layout.Context == EmitContext.FlowKey ? "{? " + quoted + " : v, z: 1}\n" : "? " + quoted + "\n: v\nz: 1\n";
    }

    /// <summary>Writes, parses with both parsers, compares. Returns the number of documents checked.</summary>
    private static int CheckValue(string value, bool deepNesting)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(value);
        int count = 0;
        foreach (Layout layout in Layouts)
        {
            foreach (StyleMode mode in new[] { StyleMode.Dump, StyleMode.Edit })
            {
                foreach (ScalarWriteStyle preferred in Styles)
                {
                    var buffer = new ArrayBufferWriter<byte>();
                    var writer = new Utf8YamlWriter(buffer);
                    ScalarWriteStyle used = writer.WriteString(utf8, preferred, layout.Context, mode, layout.Parent, layout.Content);
                    count += Verify(layout, mode, preferred, used, value, utf8, buffer, default);

                    // The block header the scalar has now: every chomping and indentation indicator.
                    if (used is ScalarWriteStyle.Literal or ScalarWriteStyle.Folded)
                    {
                        int delta = layout.Content - layout.Parent;
                        foreach (BlockChomping chomping in new[] { BlockChomping.Clip, BlockChomping.Strip, BlockChomping.Keep })
                        {
                            foreach (int indicator in new[] { 0, delta, delta % 9 + 1 })
                            {
                                var header = new BlockHeader(chomping, indicator);
                                var again = new ArrayBufferWriter<byte>();
                                ScalarWriteStyle usedAgain = new Utf8YamlWriter(again).WriteString(utf8, preferred, layout.Context, mode, layout.Parent, layout.Content, header);
                                Assert.Equal(used, usedAgain);
                                count += Verify(layout, mode, preferred, usedAgain, value, utf8, again, header);
                            }
                        }
                    }
                }
            }
        }

        return count;
    }

    private static int Verify(Layout layout, StyleMode mode, ScalarWriteStyle preferred, ScalarWriteStyle used, string value, byte[] utf8, ArrayBufferWriter<byte> buffer, BlockHeader header)
    {
        string text;
        string[] expected;
        string scalar = Encoding.UTF8.GetString(buffer.WrittenSpan);
        if (used == ScalarWriteStyle.ExplicitKeyRequired)
        {
            Assert.True(layout.IsKey, "only a key can need the explicit form");
            Assert.Equal(string.Empty, scalar);
            Assert.True(SomeStyleExceedsTheLimit(utf8), "asked for an explicit key but the key is short: " + Escape(value));
            text = ExplicitKey(layout, value);
            expected = [.. layout.Before, "=VAL \"" + value, .. layout.After];
        }
        else
        {
            if (layout.IsKey)
            {
                // No style may produce a key over the limit.
                Assert.True(scalar.EnumerateRunes().Count() <= Utf8YamlWriter.MaxImplicitKeyLength, "key over 1024 characters: " + Escape(value));
            }

            string separator = layout.Context == EmitContext.BlockValue && !scalar.EndsWith('\n') ? "\n" : string.Empty;
            text = layout.Prefix + scalar + separator + layout.Trail;
            expected = [.. layout.Before, "=VAL " + Mark(used) + value, .. layout.After];
        }

        string where = $"{layout.Name}, {mode}, preferred {preferred}, used {used}, header {header}, value '{Escape(value)}'\n--- document:\n{Escape(text).Replace("\\u000A", "\\n\n")}";

        Assert.True(
            expected.SequenceEqual(Guard(() => YamlDotNetEvents.Read(text), where)),
            "YamlDotNet read back something else: " + where);
        Assert.True(
            expected.SequenceEqual(Guard(() => SharpYamlEvents.Read(text), where)),
            "SharpYaml read back something else: " + where);

        if (used == ScalarWriteStyle.Plain)
        {
            Assert.Equal(ScalarKind.String, CoreSchema.ResolvePlain(utf8));
            if (mode == StyleMode.Dump)
            {
                Assert.False(AnyReaderTakesAsNonString(value), "plain in dump mode but YAML 1.1 reads it as a non-string: " + where);
            }
        }

        return 1;
    }

    // The key is long in the style the writer picked: plain, single or double quotes.
    private static bool SomeStyleExceedsTheLimit(byte[] utf8)
    {
        string text = Encoding.UTF8.GetString(utf8);
        var buffer = new ArrayBufferWriter<byte>();
        new Utf8YamlWriter(buffer).WriteDoubleQuoted(utf8);
        int quotes = text.Count(c => c == '\'');
        return text.EnumerateRunes().Count() > Utf8YamlWriter.MaxImplicitKeyLength
            || text.EnumerateRunes().Count() + quotes + 2 > Utf8YamlWriter.MaxImplicitKeyLength
            || Encoding.UTF8.GetString(buffer.WrittenSpan).EnumerateRunes().Count() > Utf8YamlWriter.MaxImplicitKeyLength;
    }

    private static int Run(int seed, int values, bool allowLong)
    {
        var gen = new HostileStrings(seed);
        int documents = 0;
        for (int i = 0; i < values; i++)
        {
            documents += CheckValue(gen.Next(allowLong), deepNesting: false);
        }

        return documents;
    }

    public static IEnumerable<object[]> Curated() =>
        new[]
        {
            string.Empty, " ", "  ", "\t", "a", "word", "two words", " lead", "trail ", "x:y", "x: y", "x:", ":x", "x #y", "x#y", "#x", "- x", "-x", "-",
            "? x", "?x", "?", "---", "--- x", "...", "yes", "null", "~", "true", "1", "-1", "1.5", ".5", "1e3", "0x1F", "0o17", "0755", "1_000", "1:30",
            ".inf", ".nan", "2001-12-14", "<<", "=", "it's", "say \"hi\"", "a\\b", "[a]", "{a}", "a, b", "a\nb", "a\n", "a\n\n", "\n", "\n\n", "\na",
            "  a\n", "a\n  b\n", "a\n\nb\n", "a\n\n\nb\n\n", "one\ntwo\n", "x\n\n", "\r", "a\rb", "a\r\nb", "\u0085", "\u2028", "\uFEFF", "\0",
            "é", "日本", "\U0001F600", "a\tb", "\ta", "a\t", "a\n\tb\n", "a \nb", "a\n \nb\n", "\u00A0x", "x\u00A0", "&a", "*a", "!a", "|", ">", "%a", "@a",
            "`a", "'a'", "\"a\"", "a'b", "null ", " null", "'", "\"", "\\",
        }.Select(s => new object[] { s });

    [Theory]
    [MemberData(nameof(Curated))]
    public void Curated_values_read_back_exactly(string value) => Assert.True(CheckValue(value, false) > 0);

    [Fact]
    public void Generated_values_read_back_exactly_fast_slice()
    {
        // 3,000 values x 9 layouts x 2 modes x 5 preferred styles (key layouts skipped for long values).
        int documents = Run(seed: 17, values: 3000, allowLong: true);
        TestContext.Current.SendDiagnosticMessage($"{documents} documents read back by two parsers");
        Assert.True(documents > 250_000, $"only {documents} documents");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void Generated_values_read_back_exactly_exhaustive()
    {
        int documents = Run(seed: 20260214, values: 100_000, allowLong: true);
        TestContext.Current.SendDiagnosticMessage($"{documents} documents read back by two parsers");
        Assert.True(documents > 8_000_000, $"only {documents} documents");
    }

    [Fact]
    public void Plain_in_dump_mode_never_reads_as_a_non_string_in_the_yaml_11_patterns_on_generated_words()
    {
        // A dense sweep of short strings over the characters that make up 1.1 scalars.
        string alphabet = "0123456789abBxXoO_:.+-eEnNyYtTfFsSuUlLrRiIaAZz ~=<";
        var rnd = new Random(5);
        var sb = new StringBuilder();
        int plainCount = 0;
        for (int n = 0; n < 200_000; n++)
        {
            sb.Clear();
            int len = rnd.Next(1, 9);
            for (int k = 0; k < len; k++)
            {
                sb.Append(alphabet[rnd.Next(alphabet.Length)]);
            }

            string s = sb.ToString();
            byte[] u = Encoding.UTF8.GetBytes(s);
            if (Utf8YamlWriter.CanBePlain(u, EmitContext.BlockValue, StyleMode.Dump))
            {
                plainCount++;
                Assert.False(AnyReaderTakesAsNonString(s), "[" + s + "]");
            }
        }

        Assert.True(plainCount > 10_000, $"only {plainCount} plain values");
    }
}
