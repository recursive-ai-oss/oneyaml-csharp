using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace OneYaml.Tests;

// JSON values held in `object`, emitter misuse, exceptions made by converters, and the compat refusals.

internal sealed class ObjectHolder
{
    public object? Value { get; set; }

    public List<object?> Items { get; set; } = [];

    public Dictionary<string, object?> Map { get; set; } = [];
}

[YamlConverter(typeof(OkOnlyConverter))]
internal sealed class OkOnly
{
}

internal sealed class OkOnlyConverter : YamlConverter<OkOnly>
{
    public override OkOnly? Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options) =>
        throw new YamlException("expected 'ok'");

    public override void Write(Utf8YamlEmitter writer, OkOnly value, YamlSerializerOptions options) => writer.WriteStringValue("ok");
}

[YamlConverter(typeof(LimitConverter))]
internal sealed class LimitMarker
{
}

internal sealed class LimitConverter : YamlConverter<LimitMarker>
{
    public override LimitMarker? Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options) =>
        throw new YamlException("too much") { Category = YamlErrorCategory.Limit };

    public override void Write(Utf8YamlEmitter writer, LimitMarker value, YamlSerializerOptions options) => writer.WriteStringValue("x");
}

internal sealed class OkHolder
{
    public int First { get; set; }

    public OkOnly? V { get; set; }

    public LimitMarker? L { get; set; }
}

[YamlSerializable(typeof(ObjectHolder))]
[YamlSerializable(typeof(Dictionary<string, object?>))]
[YamlSerializable(typeof(List<object?>))]
[YamlSerializable(typeof(OkHolder))]
internal sealed partial class FinalApiContext : YamlSerializerContext
{
}

public class FinalApiTests
{
    private static readonly FinalApiContext s_context = new();

    // 0 = reflection, 1 = generated metadata.
    public static TheoryData<int> Paths => new() { 0, 1 };

    private const string JsonObjectText = "{\"x\":[1,\"y\"],\"n\":1.50,\"big\":12345678901234567890123}";

    private static string Write<T>(int path, T value) =>
        path == 1 ? YamlSerializer.Serialize(value, (YamlTypeInfo<T>)s_context.GetTypeInfo(typeof(T))!) : YamlSerializer.Serialize(value);

    private static T? Read<T>(int path, string yaml) =>
        path == 1 ? YamlSerializer.Deserialize(yaml, (YamlTypeInfo<T>)s_context.GetTypeInfo(typeof(T))!) : YamlSerializer.Deserialize<T>(yaml);

    // ---- JSON values in object ----

    [Theory, MemberData(nameof(Paths))]
    public void A_JSON_value_in_an_object_is_written_as_that_value(int path)
    {
        using JsonDocument source = JsonDocument.Parse(JsonObjectText);
        var holder = new ObjectHolder
        {
            Value = JsonValue.Create(5),
            Items = [JsonNode.Parse(JsonObjectText), new JsonArray(1, "y"), source.RootElement.Clone(), JsonValue.Create("s"), null],
            Map = new Dictionary<string, object?> { ["node"] = JsonNode.Parse("[1.50, true, null]"), ["number"] = JsonNode.Parse("1e3") },
        };

        string yaml = Write(path, holder);

        ObjectHolder? back = Read<ObjectHolder>(path, yaml);
        Assert.NotNull(back);
        Assert.Equal(5L, back.Value);
        Assert.Equal(5, back.Items.Count);
        JsonNode expected = JsonNode.Parse(JsonObjectText)!;
        Assert.True(JsonNode.DeepEquals(expected, Section(yaml, 0)));
        Assert.True(JsonNode.DeepEquals(new JsonArray(1, "y"), Section(yaml, 1)));
        Assert.True(JsonNode.DeepEquals(expected, Section(yaml, 2)));
        Assert.Contains("'n': 1.50\n", yaml, StringComparison.Ordinal);
        Assert.Contains("big: 12345678901234567890123\n", yaml, StringComparison.Ordinal);
        Assert.Contains("number: 1e3\n", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("options", yaml, StringComparison.Ordinal);
    }

    // One entry of the written list, read back as a JSON value.
    private static JsonNode? Section(string yaml, int index)
    {
        using YamlDocument doc = YamlDocument.Parse(yaml);
        return doc.Root["items"][index].Deserialize<JsonNode>();
    }

    [Theory, MemberData(nameof(Paths))]
    public void A_JSON_scalar_in_a_dictionary_or_list_is_written_as_that_scalar(int path)
    {
        string map = Write(path, new Dictionary<string, object?> { ["v"] = JsonValue.Create(5), ["w"] = JsonValue.Create("t") });
        Assert.Equal("v: 5\nw: t\n", map);

        string list = Write(path, new List<object?> { JsonValue.Create(true), new JsonObject { ["a"] = 1 } });
        Assert.Equal("- true\n- a: 1\n", list);
    }

    [Fact]
    public void A_JSON_value_in_an_object_that_is_nested_too_deeply_is_a_limit_error()
    {
        JsonNode node = JsonValue.Create(1);
        for (int i = 0; i < 200; i++)
        {
            node = new JsonArray(node);
        }

        var holder = new ObjectHolder { Value = node };
        YamlException ex = Assert.Throws<YamlException>(() => YamlSerializer.Serialize(holder));
        Assert.Equal(YamlErrorCategory.Limit, ex.Category);
    }

    [Fact]
    public void Every_document_route_writes_a_JSON_value_as_that_value()
    {
        JsonNode obj = JsonNode.Parse("{\"x\":[1,\"y\"]}")!;
        using JsonDocument source = JsonDocument.Parse("{\"x\":[1,\"y\"],\"n\":1.50}");

        using (YamlDocument doc = YamlDocument.Parse("a: 0 # keep\nb: 1\n"))
        {
            doc.Set("/a", obj);
            Assert.Equal("a: # keep\n  x:\n    - 1\n    - y\nb: 1\n", doc.ToString());
        }

        using (YamlDocument doc = YamlDocument.Parse("a: 0\n"))
        {
            doc.Set("/a", JsonValue.Create(5));
            Assert.Equal("a: 5\n", doc.ToString());
            doc.Set("/a", new JsonArray(1, "y"));
            Assert.Equal("a:\n  - 1\n  - y\n", doc.ToString());
            doc.Set("/a", source.RootElement.Clone());
            Assert.Equal("a:\n  x:\n    - 1\n    - y\n  n: 1.50\n", doc.ToString());
            doc.Set("/a", new Dictionary<string, object?> { ["k"] = obj, ["v"] = JsonValue.Create(7) });
            Assert.Equal("a:\n  k:\n    x:\n      - 1\n      - y\n  v: 7\n", doc.ToString());
        }

        using (YamlDocument doc = YamlDocument.Parse("a: 0\nb: 1\n"))
        {
            doc.Root["a"] = YamlNode.Create(obj);
            doc.Root["b"] = YamlNode.Create(JsonValue.Create(5));
            Assert.Equal("a:\n  x:\n    - 1\n    - y\nb: 5\n", doc.ToString());
            doc.Root["b"].Set(new JsonArray(JsonValue.Create(1), JsonValue.Create(2)));
            Assert.Equal("a:\n  x:\n    - 1\n    - y\nb:\n  - 1\n  - 2\n", doc.ToString());
        }

        using (YamlDocument doc = YamlDocument.Parse("a: 0\n"))
        {
            doc.ApplyPatch(new YamlPatch().Add("/b", obj).Add("/c", JsonValue.Create(5)).Replace("/a", new JsonArray(1)));
            Assert.Equal("a:\n  - 1\nb:\n  x:\n    - 1\n    - y\nc: 5\n", doc.ToString());
        }
    }

    // ---- emitter misuse ----

    private static Utf8YamlEmitter NewEmitter() => new(new ArrayBufferWriter<byte>());

    [Fact]
    public void Calling_the_emitter_in_the_wrong_order_throws_InvalidOperationException()
    {
        Assert.Throws<InvalidOperationException>(() =>
        {
            Utf8YamlEmitter w = NewEmitter();
            w.WriteStartMapping();
            w.WriteStartSequence(); // a collection where a key is required
        });
        Assert.Throws<InvalidOperationException>(() =>
        {
            Utf8YamlEmitter w = NewEmitter();
            w.WriteStartMapping();
            w.WriteBinaryValue([1, 2]);
        });
        Assert.Throws<InvalidOperationException>(() =>
        {
            Utf8YamlEmitter w = NewEmitter();
            w.WriteTag("!a");
            w.WriteTag("!b");
        });
        Assert.Throws<InvalidOperationException>(() =>
        {
            Utf8YamlEmitter w = NewEmitter();
            w.WriteAnchor("a");
            w.WriteAnchor("b");
        });
        Assert.Throws<InvalidOperationException>(() => NewEmitter().WriteEndMapping());
        Assert.Throws<InvalidOperationException>(() => NewEmitter().WriteEndSequence());
        Assert.Throws<InvalidOperationException>(() =>
        {
            Utf8YamlEmitter w = NewEmitter();
            w.WriteStartMapping();
            w.WriteEndSequence();
        });
        Assert.Throws<InvalidOperationException>(() =>
        {
            Utf8YamlEmitter w = NewEmitter();
            w.WriteStartMapping();
            w.WriteTag("!a");
            w.WritePropertyName("k");
        });
        Assert.Throws<InvalidOperationException>(() =>
        {
            Utf8YamlEmitter w = NewEmitter();
            w.WriteStartMapping();
            w.WritePropertyName("k");
            w.WriteEndMapping(); // a key without a value
        });
        Assert.Throws<InvalidOperationException>(() => NewEmitter().WritePropertyName("k"));
        Assert.Throws<InvalidOperationException>(() =>
        {
            Utf8YamlEmitter w = NewEmitter();
            w.WriteNullValue();
            w.WriteNullValue(); // one call writes one document
        });
        Assert.Throws<InvalidOperationException>(() =>
        {
            Utf8YamlEmitter w = NewEmitter();
            w.WriteTag("!a");
            w.WriteAlias("x");
        });
    }

    [Fact]
    public void Malformed_data_given_to_the_emitter_stays_an_argument_error()
    {
        Assert.Throws<ArgumentException>(() => NewEmitter().WriteTag(""));
        Assert.Throws<ArgumentException>(() => NewEmitter().WriteTag("!a\uD800"));
        Assert.Throws<ArgumentException>(() => NewEmitter().WriteAnchor("has space"));
    }

    [Fact]
    public void A_key_the_data_cannot_write_is_still_a_YamlException_from_the_serializer()
    {
        var listKey = new Dictionary<object, int> { [new List<int> { 1 }] = 1 };
        YamlException ex = Assert.Throws<YamlException>(() => YamlSerializer.Serialize(listKey));
        Assert.Equal(YamlErrorCategory.Binding, ex.Category);

        var bytesKey = new Dictionary<object, int> { [new byte[] { 1 }] = 1 };
        Assert.Throws<YamlException>(() => YamlSerializer.Serialize(bytesKey));

        // A fresh write after the failure is unaffected by it.
        Assert.Equal("a: 1\n", YamlSerializer.Serialize(new Dictionary<string, int> { ["a"] = 1 }));
    }

    // ---- WriteTag ----

    [Fact]
    public void The_core_string_tag_is_written_as_a_shorthand_and_the_two_bang_form_is_a_local_tag()
    {
        var buffer = new ArrayBufferWriter<byte>();
        var w = new Utf8YamlEmitter(buffer);
        w.WriteStartMapping();
        w.WritePropertyName("a");
        w.WriteTag("tag:yaml.org,2002:str");
        w.WriteStringValue("1");
        w.WritePropertyName("b");
        w.WriteTag("!!str");
        w.WriteStringValue("1");
        w.WriteEndMapping();

        using YamlDocument doc = YamlDocument.Parse(Encoding.UTF8.GetString(buffer.WrittenSpan));
        Assert.Equal("tag:yaml.org,2002:str", doc.Root["a"].Tag);
        Assert.Equal("!!str", doc.Root["b"].Tag);
    }

    // ---- exceptions made by converters ----

    [Theory, MemberData(nameof(Paths))]
    public void A_YamlException_from_a_converter_gets_a_category_a_position_and_a_path(int path)
    {
        YamlException ex = Assert.Throws<YamlException>(() => Read<OkHolder>(path, "first: 1\nv: bad\n"));

        Assert.Equal(YamlErrorCategory.Binding, ex.Category);
        Assert.Equal(2, ex.Line);
        Assert.Equal(4, ex.Column);
        Assert.Equal("/v", ex.Path);
        Assert.True(ex.Offset > 0);
        Assert.Contains("expected 'ok'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("line 2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_YamlException_from_a_converter_is_completed_when_bound_from_a_document_and_a_stream()
    {
        using YamlDocument doc = YamlDocument.Parse("first: 1\nv: bad\n");
        YamlException fromNode = Assert.Throws<YamlException>(() => doc.Root.Deserialize<OkHolder>());
        Assert.Equal(YamlErrorCategory.Binding, fromNode.Category);
        Assert.Equal("/v", fromNode.Path);
        Assert.Equal(2, fromNode.Line);

        YamlException fromList = Assert.Throws<YamlException>(() => YamlSerializer.DeserializeAll<OkHolder>("v: bad\n---\nv: bad\n"));
        Assert.Equal("/v", fromList.Path);
    }

    [Fact]
    public void A_category_the_converter_chose_is_kept_and_the_position_is_still_added()
    {
        YamlException ex = Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<OkHolder>("first: 1\nl: x\n"));

        Assert.Equal(YamlErrorCategory.Limit, ex.Category);
        Assert.Equal(2, ex.Line);
        Assert.Equal("/l", ex.Path);
    }

    [Fact]
    public void The_public_constructors_do_not_claim_a_syntax_error()
    {
        Assert.Equal(YamlErrorCategory.None, new YamlException().Category);
        Assert.Equal(YamlErrorCategory.None, new YamlException("m").Category);
        Assert.Equal(YamlErrorCategory.None, new YamlException("m", new InvalidOperationException()).Category);
        Assert.Equal(default, YamlErrorCategory.None);
        Assert.Equal(YamlErrorCategory.Syntax, Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<object>("a: [1")).Category);
        Assert.Equal(0, new YamlException("m").Line);
    }
}
