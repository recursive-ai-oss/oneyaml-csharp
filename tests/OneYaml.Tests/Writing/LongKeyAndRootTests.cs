using System.Buffers;
using System.Text;
using OneYaml.Tests.Generated;
using Xunit;

namespace OneYaml.Tests.Writing;

/// <summary>Keys too long to be implicit, and scalars that are the whole document, through every writing path.</summary>
public class LongKeyAndRootTests
{
    // Long keys of every kind that changes how the explicit form is written.
    public static IEnumerable<object[]> LongKeys()
    {
        yield return [new string('k', 1100)];
        yield return [new string('k', 600) + ": " + new string('k', 600)];
        yield return [new string('k', 600) + " # " + new string('k', 600)];
        yield return [new string('k', 600) + "\n" + new string('k', 600)];
        yield return [new string('\u0001', 300)];
        yield return [new string('\u00E9', 1100)];
        yield return ["'" + new string('\'', 1100)];
        yield return ["- " + new string('k', 1100)];
        yield return [new string('1', 1100)];
        yield return [new string(' ', 5) + new string('k', 1100) + " "];
    }

    [Theory]
    [MemberData(nameof(LongKeys))]
    public void The_public_emitter_writes_a_long_key_in_the_explicit_form_at_every_depth(string key)
    {
        var output = new ArrayBufferWriter<byte>();
        var emitter = new Utf8YamlEmitter(output);
        emitter.WriteStartMapping();
        emitter.WritePropertyName("a");
        emitter.WriteStartSequence();
        emitter.WriteStartMapping();
        emitter.WritePropertyName(key);
        emitter.WriteNumberValue(1);
        emitter.WritePropertyName("after");
        emitter.WriteStartMapping();
        emitter.WritePropertyName(key);
        emitter.WriteStringValue("v");
        emitter.WriteEndMapping();
        emitter.WriteEndMapping();
        emitter.WriteEndSequence();
        emitter.WriteEndMapping();
        string yaml = Encoding.UTF8.GetString(output.WrittenSpan);

        ReadBack.Check(yaml);
        var back = YamlSerializer.Deserialize<Dictionary<string, List<Dictionary<string, object?>>>>(yaml)!;
        Dictionary<string, object?> entry = back["a"][0];
        Assert.Equal(1L, entry[key]);
        Assert.Equal("v", ((Dictionary<string, object?>)entry["after"]!)[key]);
    }

    [Theory]
    [MemberData(nameof(LongKeys))]
    public void Reflection_and_generated_output_agree_on_a_long_key(string key)
    {
        var value = new Dictionary<string, List<Person>> { [key] = [new Person { Name = "Ada" }], ["short"] = [] };
        var context = new ModelsContext(new YamlSerializerOptions());

        string reflected = YamlSerializer.Serialize(value);
        string generated = YamlSerializer.Serialize(value, context.DictionaryStringListPerson);

        Assert.Equal(reflected, generated);
        Assert.StartsWith("? ", reflected, StringComparison.Ordinal);
        ReadBack.Check(reflected);
        Dictionary<string, List<Person>> back = YamlSerializer.Deserialize<Dictionary<string, List<Person>>>(reflected)!;
        Assert.Equal(new[] { key, "short" }, back.Keys.ToArray());
        Assert.Equal("Ada", back[key][0].Name);
    }

    // Strings written as the whole document: block scalars with every header the text needs, and the quoted forms.
    public static IEnumerable<object[]> RootStrings()
    {
        foreach (string s in new[]
        {
            "a\nb\n", "a\nb", "a\nb\n\n\n", "\n", "\n\n", "\na\n", "\n\na", " lead\nx\n", "x\n lead\n", "x\n\n lead\n",
            "a\n\nb\n", "a  \nb\n", "a\n  \nb\n", "a\n\tb\n", "- a\nb\n", "# c\nb\n", "---\nb\n", "...\nb\n", "a: b\nc\n",
            "\u00E9\nb\n", "line one is long enough to be folded by a careless writer\nline two\n", "plain", "", " ", "null", "1", "a\u0001b\n",
        })
        {
            yield return [s];
        }
    }

    [Theory]
    [MemberData(nameof(RootStrings))]
    public void A_string_as_the_whole_document_reads_back_exactly(string text)
    {
        string yaml = YamlSerializer.Serialize(text);
        Assert.Equal(text, YamlSerializer.Deserialize<string>(yaml));
        ReadBack.Check(yaml);

        var output = new ArrayBufferWriter<byte>();
        var emitter = new Utf8YamlEmitter(output);
        emitter.WriteStringValue(text);
        Assert.Equal(yaml, Encoding.UTF8.GetString(output.WrittenSpan));

        // The same text after "---" on a document of its own and as a sequence item keeps its value too.
        string inList = YamlSerializer.Serialize(new List<string> { text, text });
        Assert.Equal([text, text], YamlSerializer.Deserialize<List<string>>(inList));

        foreach (int indent in new[] { 1, 3, 4 })
        {
            var options = new YamlSerializerOptions { IndentSize = indent };
            string indented = YamlSerializer.Serialize(text, options);
            Assert.Equal(text, YamlSerializer.Deserialize<string>(indented, options));
            ReadBack.Check(indented);
        }
    }

    [Fact]
    public void A_multi_line_string_at_the_root_is_a_block_scalar_when_it_can_be()
    {
        Assert.Equal("|\n  a\n  b\n", YamlSerializer.Serialize("a\nb\n"));
        Assert.Equal("|-\n  a\n  b\n", YamlSerializer.Serialize("a\nb"));
        Assert.Equal("|+\n  a\n  b\n\n\n", YamlSerializer.Serialize("a\nb\n\n\n"));
    }
}
