using System.Buffers;
using System.Text;
using OneYaml.Emitting;
using Xunit;

namespace OneYaml.Tests.Emitting;

// The shortcut for simple text writes it without analysing it, so it must never accept text the general rules would quote.
public sealed class SimplePlainTests
{
    private const string Alphabet = "aAnNoOyYtTfF_-./ 1:#,'e";

    private static IEnumerable<string> Strings(int maxLength)
    {
        yield return string.Empty;
        var current = new List<string> { string.Empty };
        for (int length = 1; length <= maxLength; length++)
        {
            var next = new List<string>();
            foreach (string prefix in current)
            {
                foreach (char c in Alphabet)
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
}
