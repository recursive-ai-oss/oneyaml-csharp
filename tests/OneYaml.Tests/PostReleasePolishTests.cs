using System.Text;
using Xunit;

// The tests pass types as values on purpose, and name the overloads without a type argument.
#pragma warning disable CA2263

namespace OneYaml.Tests;

// Reading a node as object, copies of the reader, and the wording of a few errors.
public class PostReleasePolishTests
{
    private const string Text = "m:\n  a: 1\n  b: [x, 2.5]\ns: [1, two]\nn: 7\n";

    // ---- object reads ----

    [Fact]
    public void Get_of_object_reads_a_mapping_and_a_sequence_as_the_generic_shapes()
    {
        using YamlDocument doc = YamlDocument.Parse(Text);

        var map = Assert.IsType<Dictionary<string, object?>>(doc.Get<object>("/m"));
        Assert.Equal(1L, map["a"]);
        var inner = Assert.IsType<List<object?>>(map["b"]);
        Assert.Equal("x", inner[0]);
        Assert.Equal(2.5, inner[1]);

        var list = Assert.IsType<List<object?>>(doc.Get<object>("/s"));
        Assert.Equal(new object?[] { 1L, "two" }, list);
        Assert.Equal(7L, doc.Get<object>("/n"));
    }

    [Fact]
    public void Reading_a_node_as_object_gives_what_Deserialize_of_object_gives()
    {
        using YamlDocument doc = YamlDocument.Parse(Text);
        object? fromText = YamlSerializer.Deserialize<object>(Text);

        Assert.Equal(Canon(fromText), Canon(doc.Root.GetValue<object>()));
        Assert.Equal(Canon(((Dictionary<string, object?>)fromText!)["m"]), Canon(doc.Root["m"].GetValue<object>()));
        Assert.Equal(Canon(((Dictionary<string, object?>)fromText!)["s"]), Canon(doc.Root["s"].Deserialize<object>()));
    }

    [Fact]
    public void TryGet_of_object_reads_a_collection()
    {
        using YamlDocument doc = YamlDocument.Parse(Text);

        Assert.True(doc.TryGet("/m", out object? map));
        Assert.IsType<Dictionary<string, object?>>(map);
        Assert.True(doc.Root["s"].TryGetValue(out object? list));
        Assert.IsType<List<object?>>(list);
        Assert.False(doc.TryGet("/missing", out object? none));
        Assert.Null(none);
    }

    private static string Canon(object? value) => value switch
    {
        null => "null",
        string s => "s:" + s,
        IDictionary<string, object?> d => "{" + string.Join(",", d.Select(p => p.Key + "=" + Canon(p.Value))) + "}",
        IEnumerable<object?> l => "[" + string.Join(",", l.Select(Canon)) + "]",
        _ => value.GetType().Name + ":" + Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
    };

    // ---- copies of the reader ----

    public abstract record Shape;

    public sealed record Circle(double R, int Keys) : Shape;

    public sealed class Holder
    {
        public Shape? Shape { get; set; }

        public int After { get; set; }
    }

    // Reads the whole shape with the reader itself, as a converter does after it has looked ahead.
    private static Circle ReadShape(ref Utf8YamlReader reader, string? kind)
    {
        double r = -1;
        int n = 0;
        while (reader.Read() && reader.TokenType != YamlTokenKind.MappingEnd)
        {
            string? key = reader.GetString();
            reader.Read();
            n++;
            if (key == "r")
            {
                r = reader.GetDouble();
            }
            else
            {
                reader.Skip();
            }
        }

        return kind == "circle" ? new Circle(r, n) : throw new InvalidDataException("kind " + kind);
    }

    // The idiom of System.Text.Json: copy the reader, scan the copy for a key, then read with the original.
    private sealed class PeekingConverter : YamlConverter<Shape>
    {
        public override Shape Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options)
        {
            Utf8YamlReader copy = reader;
            string? kind = null;
            while (copy.Read() && copy.TokenType != YamlTokenKind.MappingEnd)
            {
                string? key = copy.GetString();
                copy.Read();
                if (key == "kind")
                {
                    kind = copy.GetString();
                    break;
                }

                copy.Skip();
            }

            return ReadShape(ref reader, kind);
        }

        public override void Write(Utf8YamlEmitter writer, Shape value, YamlSerializerOptions options) => writer.WriteNullValue();
    }

    // A copy that reads on past the end of the value it was made in.
    private sealed class GreedyConverter : YamlConverter<Shape>
    {
        public override Shape Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options)
        {
            Utf8YamlReader copy = reader;
            while (copy.Read())
            {
            }

            return ReadShape(ref reader, "circle");
        }

        public override void Write(Utf8YamlEmitter writer, Shape value, YamlSerializerOptions options) => writer.WriteNullValue();
    }

    // The original reads on first; the copy is used afterwards.
    private sealed class ReverseConverter : YamlConverter<Shape>
    {
        public override Shape Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options)
        {
            Utf8YamlReader copy = reader;
            Shape shape = ReadShape(ref reader, "circle");
            while (reader.Read())
            {
                if (reader.TokenType == YamlTokenKind.Scalar && reader.GetString() == "stop")
                {
                    break;
                }
            }

            _ = copy.Read();
            _ = copy.Read();
            _ = copy.Read();
            return shape;
        }

        public override void Write(Utf8YamlEmitter writer, Shape value, YamlSerializerOptions options) => writer.WriteNullValue();
    }

    private static string ShapeText(int pad)
    {
        var sb = new StringBuilder("shape:\n");
        for (int i = 0; i < pad; i++)
        {
            sb.Append("  p").Append(i).Append(": ").Append('x', 40).Append('\n');
        }

        return sb.Append("  kind: circle\n  r: 2\nafter: 7\n").ToString();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(2000)]
    public async Task Looking_ahead_on_a_copy_within_the_value_still_works(int pad)
    {
        var options = new YamlSerializerOptions { Converters = { new PeekingConverter() } };
        string text = ShapeText(pad);

        Holder[] results =
        [
            YamlSerializer.Deserialize<Holder>(text, options)!,
            YamlSerializer.Deserialize<Holder>(new MemoryStream(Encoding.UTF8.GetBytes(text)), options)!,
            (await YamlSerializer.DeserializeAsync<Holder>(new MemoryStream(Encoding.UTF8.GetBytes(text)), options, TestContext.Current.CancellationToken))!,
        ];
        foreach (Holder h in results)
        {
            Assert.Equal(new Circle(2, pad + 2), h.Shape);
            Assert.Equal(7, h.After);
        }
    }

    [Fact]
    public void A_copy_that_reads_past_its_value_makes_the_original_throw_a_usage_error_not_a_syntax_error()
    {
        var options = new YamlSerializerOptions { Converters = { new GreedyConverter() } };

        InvalidOperationException e = Assert.Throws<InvalidOperationException>(() => YamlSerializer.Deserialize<Holder>(ShapeText(3), options));

        Assert.Contains("copy", e.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Utf8YamlReader", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_copy_used_after_the_original_read_on_throws_a_usage_error()
    {
        var options = new YamlSerializerOptions { Converters = { new ReverseConverter() } };
        string text = "shape:\n  kind: circle\n  r: 2\n  stop: 1\n  more: [a, b]\nafter: 7\n";

        _ = Assert.Throws<InvalidOperationException>(() => YamlSerializer.Deserialize<Holder>(text, options));
    }

    [Fact]
    public void Invalid_text_is_still_reported_as_invalid_text_when_a_copy_peeked_in_the_value()
    {
        var options = new YamlSerializerOptions { Converters = { new PeekingConverter() } };

        YamlException e = Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<Holder>("shape:\n  kind: circle\n  r: 2\n  bad: [1\nafter: 7\n", options));

        Assert.Equal(YamlErrorCategory.Syntax, e.Category);
    }

    // ---- error wording ----

    private static readonly YamlLimits s_tiny = new()
    {
        AliasNodeFactor = 1,
        AliasNodeFloor = 10,
        AliasByteFactor = 1,
        AliasByteFloor = 100,
    };

    private const string AliasFan = "a: &a [1, 2, 3, 4, 5, 6, 7, 8, 9]\nb: [*a, *a, *a, *a, *a, *a]\n";
    private const string Budget = "Expanding the aliases exceeds the budget set by YamlLimits (AliasNodeFactor 1, AliasNodeFloor 10, AliasByteFactor 1, AliasByteFloor 100).";

    [Fact]
    public void The_alias_budget_error_reads_the_same_in_the_document_and_the_serializer_and_names_a_position()
    {
        YamlException fromSerializer = Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<object>(AliasFan, new YamlSerializerOptions { Limits = s_tiny }));

        using YamlDocument doc = YamlDocument.Parse(AliasFan, new YamlDocumentOptions { Limits = s_tiny });
        YamlException fromDocument = Assert.Throws<YamlException>(() => doc.Get<object>("/b"));

        foreach (YamlException e in new[] { fromSerializer, fromDocument })
        {
            Assert.Equal(YamlErrorCategory.Limit, e.Category);
            Assert.Contains(Budget, e.Message, StringComparison.Ordinal);
            Assert.Contains("(line 2, column ", e.Message, StringComparison.Ordinal);
        }
    }

    [YamlPolymorphic(TypeDiscriminatorPropertyName = "type")]
    [YamlDerivedType(typeof(Run), "run")]
    public abstract record Stage;

    public sealed record Run(string Cmd) : Stage;

    public sealed class Plan
    {
        public List<Stage> Steps { get; set; } = [];
    }

    [Fact]
    public void A_missing_key_discriminator_points_at_the_mapping()
    {
        const string text = "steps:\n  - cmd: ls\n    other: 1\n";

        YamlException e = Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<Plan>(text));

        Assert.Equal("/steps/0", e.Path);
        Assert.Equal(2, e.Line);
        Assert.Equal(5, e.Column);
    }
}
