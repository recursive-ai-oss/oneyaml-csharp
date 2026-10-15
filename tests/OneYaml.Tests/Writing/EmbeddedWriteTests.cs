using System.Buffers;
using System.Text;
using Xunit;

namespace OneYaml.Tests.Writing;

/// <summary>Values written as the value of an entry in text that is already there, for the document editor.</summary>
public class EmbeddedWriteTests
{
    private static string Embedded(object? value, Utf8YamlEmitter.Embedding embedding, int parentIndent)
    {
        var buffer = new ArrayBufferWriter<byte>();
        YamlSerializer.WriteEmbedded(buffer, value, new YamlSerializerOptions { TypeInfoResolver = new DefaultYamlTypeInfoResolver() }.GetTypeInfo<object>(), embedding, parentIndent);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    [Fact]
    public void A_scalar_follows_the_key_or_the_dash()
    {
        Assert.Equal(" 5\n", Embedded(5L, Utf8YamlEmitter.Embedding.AfterKey, 2));
        Assert.Equal("5\n", Embedded(5L, Utf8YamlEmitter.Embedding.AfterDash, 2));
        Assert.Equal(" |\n    a\n    b\n", Embedded("a\nb\n", Utf8YamlEmitter.Embedding.AfterKey, 2));
        Assert.Equal("|\n    a\n    b\n", Embedded("a\nb\n", Utf8YamlEmitter.Embedding.AfterDash, 2));
    }

    [Fact]
    public void Collections_are_indented_from_the_column_of_the_entry()
    {
        var person = new Person { Name = "Ada", Tags = ["t"] };
        var map = new Dictionary<string, object?> { ["a"] = new List<object?> { 1L, new List<object?> { 2L } } };

        Assert.Equal(" {}\n", Embedded(new Dictionary<string, object?>(), Utf8YamlEmitter.Embedding.AfterKey, 2));
        Assert.Equal("[]\n", Embedded(new List<object?>(), Utf8YamlEmitter.Embedding.AfterDash, 2));
        Assert.Equal("\n    name: Ada\n    age: 0\n    nickname: null\n    tags:\n      - t\n", Embedded(person, Utf8YamlEmitter.Embedding.AfterKey, 2));
        Assert.Equal("name: Ada\n    age: 0\n    nickname: null\n    tags:\n      - t\n", Embedded(person, Utf8YamlEmitter.Embedding.AfterDash, 2));
        Assert.Equal("\n    a:\n      - 1\n      - - 2\n", Embedded(map, Utf8YamlEmitter.Embedding.AfterKey, 2));
    }

    [Fact]
    public void Embedded_values_parse_back_inside_their_surroundings()
    {
        var person = new Person { Name = "Ada\nLovelace\n", Tags = ["t", "u\nv", " lead\nx"] };
        foreach (int column in new[] { 0, 2, 6 })
        {
            string pad = new(' ', column);
            string underKey = pad + "key:" + Embedded(person, Utf8YamlEmitter.Embedding.AfterKey, column);
            string afterDash = pad + "- " + Embedded(person, Utf8YamlEmitter.Embedding.AfterDash, column);
            string scalar = pad + "key:" + Embedded(" a\nb\n", Utf8YamlEmitter.Embedding.AfterKey, column);

            // The surrounding text sits in a mapping or sequence at that column; the reader needs the first line at the left edge.
            Assert.Equal(Canon.Of(person), Canon.Of(YamlSerializer.Deserialize<Dictionary<string, Person>>(underKey)!["key"]));
            Assert.Equal(Canon.Of(person), Canon.Of(YamlSerializer.Deserialize<List<Person>>(afterDash)![0]));
            Assert.Equal(" a\nb\n", YamlSerializer.Deserialize<Dictionary<string, string>>(scalar)!["key"]);
            ReadBack.Check(underKey);
            ReadBack.Check(scalar);
        }
    }
}
