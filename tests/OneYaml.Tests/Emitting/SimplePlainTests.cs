using System.Buffers;
using System.Text;
using OneYaml.Emitting;
using Xunit;

namespace OneYaml.Tests.Emitting;

// The shortcut for simple text writes it without analysing it, so it must never accept text the general rules would quote.
public sealed class SimplePlainTests
{
    private const string Alphabet = "aAnNoOyYtTfF_-./ 1:#,'e";

    private static IEnumerable<string> Strings(int maxLength, string alphabet = Alphabet)
    {
        yield return string.Empty;
        var current = new List<string> { string.Empty };
        for (int length = 1; length <= maxLength; length++)
        {
            var next = new List<string>();
            foreach (string prefix in current)
            {
                foreach (char c in alphabet)
                {
                    next.Add(prefix + c);
                }
            }

            foreach (string s in next)
            {
                yield return s;
            }

            current = next;
        }
    }

    [Fact]
    public void Accepted_text_is_plain_under_the_general_rules()
    {
        int accepted = 0;
        foreach (string text in Strings(4).Concat(["yes please", "on_call", "Names are long", "null_value", "a b c d e f", "no", "None", "oneYear", "online", "falsey", "trueish"]))
        {
            if (!ScalarText.IsSimplePlain(text, out bool spaced))
            {
                continue;
            }

            accepted++;
            byte[] utf8 = Encoding.UTF8.GetBytes(text);
            foreach (StyleMode mode in new[] { StyleMode.Dump, StyleMode.Edit })
            {
                Assert.Equal(ScalarWriteStyle.Plain, Utf8YamlWriter.ChooseStyle(utf8, ScalarWriteStyle.Plain, EmitContext.BlockValue, mode));
                if (!spaced)
                {
                    Assert.Equal(ScalarWriteStyle.Plain, Utf8YamlWriter.ChooseStyle(utf8, ScalarWriteStyle.Plain, EmitContext.BlockKey, mode));
                }
            }
        }

        Assert.True(accepted > 1000);
    }

    [Theory]
    [InlineData("on")]
    [InlineData("On")]
    [InlineData("off")]
    [InlineData("y")]
    [InlineData("no")]
    [InlineData("true")]
    [InlineData("False")]
    [InlineData("null")]
    [InlineData("1a")]
    [InlineData("a ")]
    [InlineData("a: b")]
    [InlineData("a #b")]
    [InlineData("é")]
    public void Text_that_needs_care_is_left_to_the_general_rules(string text) =>
        Assert.False(ScalarText.IsSimplePlain(text, out _));

    private static string Emit(Action<Utf8YamlEmitter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var emitter = new Utf8YamlEmitter(buffer);
        write(emitter);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    // The same document written from characters (the shortcut applies) and from UTF-8 bytes (it never does).
    [Fact]
    public void Characters_and_bytes_give_the_same_text_in_every_place()
    {
        string[] texts = [.. Strings(3).Where(t => t.Length > 0 && t.IndexOf('\n') < 0), "yes please", "on_call", "Names are long", "no", "oneYear"];
        foreach (string t in texts)
        {
            byte[] u = Encoding.UTF8.GetBytes(t);
            Assert.Equal(
                Emit(e => { e.WriteStartMapping(); e.WritePropertyName(u); e.WriteStringValue(u); e.WriteEndMapping(); }),
                Emit(e => { e.WriteStartMapping(); e.WritePropertyName(t); e.WriteStringValue(t); e.WriteEndMapping(); }));
            Assert.Equal(
                Emit(e => { e.WriteStartSequence(); e.WriteStringValue(u); e.WriteStartSequence(); e.WriteStringValue(u); e.WriteEndSequence(); e.WriteEndSequence(); }),
                Emit(e => { e.WriteStartSequence(); e.WriteStringValue(t); e.WriteStartSequence(); e.WriteStringValue(t); e.WriteEndSequence(); e.WriteEndSequence(); }));
            Assert.Equal(Emit(e => e.WriteStringValue(u)), Emit(e => e.WriteStringValue(t)));
            Assert.Equal(
                Emit(e => { e.WriteStartMapping(); e.WriteStringValue(u); e.WriteStringValue(u); e.WriteEndMapping(); }),
                Emit(e => { e.WriteStartMapping(); e.WriteStringValue(t); e.WriteStringValue(t); e.WriteEndMapping(); }));
        }
    }

    // Words YAML 1.1 reads as booleans stay quoted as keys of a generic tree, where keys come through a cache.
    [Fact]
    public void Generic_tree_keys_keep_their_quotes()
    {
        var tree = new Dictionary<string, object?>
        {
            ["name"] = "Build and Test",
            ["on"] = new Dictionary<string, object?> { ["on"] = "on", ["push"] = null, ["a b"] = "x y" },
            ["list"] = new List<object?> { new Dictionary<string, object?> { ["on"] = 1L, ["yes"] = true } },
        };
        string text = YamlSerializer.Serialize<object>(tree);
        Assert.Equal("name: Build and Test\n'on':\n  'on': 'on'\n  push: null\n  a b: x y\nlist:\n  - 'on': 1\n    'yes': true\n", text);
    }

    // The shortcut must agree with the general rules on every short text over the letters of the YAML 1.1 words,
    // digits, underscores, signs and dots, in every place a plain scalar can sit.
    [Fact]
    public void The_shortcut_agrees_with_the_general_rules_over_words_digits_and_underscores()
    {
        int accepted = 0;
        foreach (string text in Strings(5, "yNOtFelsa_1.-"))
        {
            if (!ScalarText.IsSimplePlain(text, out _))
            {
                continue;
            }

            accepted++;
            byte[] utf8 = Encoding.UTF8.GetBytes(text);
            Assert.Equal(ScalarWriteStyle.Plain, Utf8YamlWriter.ChooseStyle(utf8, ScalarWriteStyle.Plain, EmitContext.BlockValue, StyleMode.Dump));
            Assert.Equal(ScalarWriteStyle.Plain, Utf8YamlWriter.ChooseStyle(utf8, ScalarWriteStyle.Plain, EmitContext.BlockKey, StyleMode.Dump));
        }

        Assert.True(accepted > 10000);
    }

    [Fact]
    public void Every_spelling_of_a_yaml_1_1_word_is_left_to_the_general_rules()
    {
        foreach (string word in new[] { "y", "n", "yes", "no", "on", "off", "true", "false", "null" })
        {
            for (int mask = 0; mask < (1 << word.Length); mask++)
            {
                char[] c = word.ToCharArray();
                for (int i = 0; i < c.Length; i++)
                {
                    if ((mask & (1 << i)) != 0)
                    {
                        c[i] = char.ToUpperInvariant(c[i]);
                    }
                }

                Assert.False(ScalarText.IsSimplePlain(new string(c), out _), new string(c));
            }
        }
    }

    [Theory]
    [InlineData("_1")]
    [InlineData("+_1")]
    [InlineData("1e3_")]
    [InlineData("0_x123")]
    [InlineData("1_0.5")]
    [InlineData("__1")]
    [InlineData("_.5")]
    [InlineData("_+1")]
    [InlineData("_-2_")]
    [InlineData("_0b1")]
    [InlineData(".")]
    [InlineData("-.")]
    [InlineData("+.")]
    [InlineData(".e-12")]
    [InlineData(".E+3")]
    public void Text_some_readers_take_for_a_number_is_quoted_as_a_value_and_as_a_key(string text)
    {
        Assert.Equal($"'{text}'\n", YamlSerializer.Serialize<object>(text));
        var tree = new Dictionary<string, object?> { [text] = text };
        Assert.Equal($"'{text}': '{text}'\n", YamlSerializer.Serialize<object>(tree));
    }

    [Theory]
    [InlineData("_")]
    [InlineData("__")]
    [InlineData("_id")]
    [InlineData("_null")]
    [InlineData("_a1")]
    [InlineData("a_1")]
    public void Names_with_underscores_stay_plain(string text)
    {
        Assert.Equal(text + "\n", YamlSerializer.Serialize<object>(text));
        var tree = new Dictionary<string, object?> { [text] = text };
        Assert.Equal($"{text}: {text}\n", YamlSerializer.Serialize<object>(tree));
    }

    // The implicit key limit counts UTF-16 code units: 600 emoji are 600 characters but 1,200 units.
    [Theory]
    [InlineData(250, false)]
    [InlineData(510, false)]
    [InlineData(513, true)]
    [InlineData(600, true)]
    public void The_implicit_key_limit_counts_utf16_code_units(int emoji, bool explicitKey)
    {
        string key = string.Concat(Enumerable.Repeat("\U0001F600", emoji));
        string text = YamlSerializer.Serialize<object>(new Dictionary<string, object?> { [key] = 1L });
        Assert.Equal(explicitKey, text.StartsWith("? ", StringComparison.Ordinal));
        var back = YamlSerializer.Deserialize<Dictionary<string, object?>>(text);
        Assert.Equal(1L, back![key]);
    }
}
