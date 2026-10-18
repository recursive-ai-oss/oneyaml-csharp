using System.Buffers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace OneYaml.Tests;

// One behaviour across the document, the serializer and the reader: tags, limits, source names, exception types,
// typed reads, copied options, exceptions built by callers, and System.Text.Json members.

[YamlPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[YamlDerivedType(typeof(ConfigMapDoc), "ConfigMap")]
[YamlDerivedType(typeof(DeploymentDoc), "Deployment")]
[YamlDerivedType(typeof(ListDoc), "List")]
internal class ManifestDoc
{
    public string ApiVersion { get; set; } = "";
}

internal sealed class ConfigMapDoc : ManifestDoc
{
    public Dictionary<string, string> Data { get; set; } = [];
}

internal sealed class DeploymentDoc : ManifestDoc
{
    public int Replicas { get; set; }
}

internal sealed class ListDoc : ManifestDoc
{
    public List<ManifestDoc> Items { get; set; } = [];
}

[YamlPolymorphic(DiscriminatorKind = YamlDiscriminatorKind.Key)]
[YamlDerivedType(typeof(WideCircle), "circle")]
[YamlDerivedType(typeof(WideSquare), "square")]
internal abstract class WideShape
{
    public string? Label { get; set; }

    public WideShape? Inner { get; set; }

    public object? Fill { get; set; }
}

internal sealed class WideCircle : WideShape
{
    public int Radius { get; set; }
}

internal sealed class WideSquare : WideShape
{
    public int Side { get; set; }
}

[YamlPolymorphic(DiscriminatorKind = YamlDiscriminatorKind.Tag)]
[YamlDerivedType(typeof(TagOne), "!!foo")]
[YamlDerivedType(typeof(TagTwo), "!e!x")]
internal abstract class TagShape
{
}

internal sealed class TagOne : TagShape
{
    public int A { get; set; }
}

internal sealed class TagTwo : TagShape
{
    public int B { get; set; }
}

internal sealed class JsonHolder
{
    public string Name { get; set; } = "";

    public JsonElement Settings { get; set; }

    public JsonNode? Node { get; set; }

    public JsonObject? Object { get; set; }

    public JsonArray? Array { get; set; }
}

internal sealed class DocumentHolder
{
    public JsonDocument? Document { get; set; }
}

internal enum PullMode
{
    Always,
    IfNotPresent,
}

internal sealed class RolloutPlan
{
    public string Type { get; set; } = "";

    public int Surge { get; set; }
}

internal sealed class NeedsName
{
    [YamlRequired]
    public string Name { get; set; } = "";
}

internal sealed class NeedsNameParent
{
    public NeedsName? Inner { get; set; }
}

[YamlSerializable(typeof(ManifestDoc))]
[YamlSerializable(typeof(WideShape))]
[YamlSerializable(typeof(TagShape))]
[YamlSerializable(typeof(JsonHolder))]
[YamlSerializable(typeof(DocumentHolder))]
[YamlSerializable(typeof(RolloutPlan))]
[YamlSerializable(typeof(PullMode))]
[YamlSerializable(typeof(List<int>))]
[YamlSerializable(typeof(List<object>))]
[YamlSerializable(typeof(object))]
[YamlSerializable(typeof(NeedsNameParent))]
[YamlSerializable(typeof(NeedsName))]
internal sealed partial class ApiConsistencyContext : YamlSerializerContext
{
}

public class ApiConsistencyTests
{
    private static readonly ApiConsistencyContext s_context = new();

    // 0 = reflection, 1 = generated metadata, 2 = bound from a YamlDocument.
    public static TheoryData<int> Paths => new() { 0, 1, 2 };

    private static T? Read<T>(int path, string yaml, YamlSerializerOptions? options = null) => path switch
    {
        0 => YamlSerializer.Deserialize<T>(yaml, options),
        1 => YamlSerializer.Deserialize(yaml, (YamlTypeInfo<T>)(options is null ? s_context : new ApiConsistencyContext(options)).GetTypeInfo(typeof(T))!),
        _ => YamlDocument.Parse(yaml).Root.Deserialize<T>(options),
    };

    private static string Write<T>(int path, T value) =>
        path == 1 ? YamlSerializer.Serialize(value, (YamlTypeInfo<T>)s_context.GetTypeInfo(typeof(T))!) : YamlSerializer.Serialize(value);

    // ---- one tag form ----

    private const string Tagged = "%TAG !e! tag:example.com,2026:\n---\na: !e!widget x\nb: !!str 1\nc: !Sub y\nd: !<tag:example.com,2026:v> z\ne: plain\n";

    [Fact]
    public void A_node_reports_the_resolved_tag_and_keeps_the_written_form_apart()
    {
        using YamlDocument doc = YamlDocument.Parse(Tagged);

        Assert.Equal("tag:example.com,2026:widget", doc.Root["a"].Tag);
        Assert.Equal("tag:yaml.org,2002:str", doc.Root["b"].Tag);
        Assert.Equal("!Sub", doc.Root["c"].Tag);
        Assert.Equal("tag:example.com,2026:v", doc.Root["d"].Tag);
        Assert.Null(doc.Root["e"].Tag);

        Assert.Equal("!e!widget", doc.Root["a"].RawTag);
        Assert.Equal("!!str", doc.Root["b"].RawTag);
        Assert.Equal("!<tag:example.com,2026:v>", doc.Root["d"].RawTag);
        Assert.Null(doc.Root["e"].RawTag);
    }

    private sealed class TagForms : YamlConverter<string>
    {
        public override string? Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options)
        {
            string tryForm = reader.TryGetTag(out string? tag) ? tag : "-";
            string raw = reader.TryGetRawTag(out ReadOnlySpan<byte> bytes) ? Encoding.UTF8.GetString(bytes) : "-";
            return $"{reader.Tag ?? "-"}|{tryForm}|{raw}";
        }

        public override void Write(Utf8YamlEmitter writer, string value, YamlSerializerOptions options) => writer.WriteStringValue(value);
    }

    private sealed class TagFormsHolder
    {
        public string? A { get; set; }

        public string? B { get; set; }

        public string? C { get; set; }
    }

    [Fact]
    public void The_reader_gives_the_resolved_tag_from_Tag_and_TryGetTag_and_the_written_form_from_TryGetRawTag()
    {
        var options = new YamlSerializerOptions { Converters = { new TagForms() } };

        TagFormsHolder h = YamlSerializer.Deserialize<TagFormsHolder>("%TAG !e! tag:example.com,2026:\n---\na: !e!widget x\nb: !!str 1\nc: plain\n", options)!;

        Assert.Equal("tag:example.com,2026:widget|tag:example.com,2026:widget|!e!widget", h.A);
        Assert.Equal("tag:yaml.org,2002:str|tag:yaml.org,2002:str|!!str", h.B);
        Assert.Equal("-|-|-", h.C);
    }

    private static string Emit(Action<Utf8YamlEmitter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        write(new Utf8YamlEmitter(buffer));
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    [Fact]
    public void WriteTag_writes_a_resolved_tag_as_exactly_that_tag()
    {
        Assert.Equal("!!str '1'\n", Emit(w =>
        {
            w.WriteTag("tag:yaml.org,2002:str");
            w.WriteStringValue("1");
        }));

        // A local tag that merely looks like a shorthand stays a local tag.
        foreach (string local in new[] { "!!foo", "!e!w", "!<x>", "!" })
        {
            string text = Emit(w =>
            {
                w.WriteTag(local);
                w.WriteStringValue("v");
            });

            using YamlDocument back = YamlDocument.Parse(text);
            Assert.Equal(local, back.Root.Tag);
        }

        Assert.Throws<ArgumentException>(() => Emit(w => w.WriteTag("")));
    }

    [Fact]
    public void A_tag_taken_from_a_node_is_written_back_as_the_same_tag()
    {
        const string text = Tagged + "f: !%21foo x\ng: !<!!foo> x\nh: !e%21w x\ni: !<!e!w> x\n";
        using YamlDocument source = YamlDocument.Parse(text);
        foreach (string key in new[] { "a", "b", "c", "d", "f", "g", "h", "i" })
        {
            string? tag = source.Root[key].Tag;
            string written = Emit(w =>
            {
                w.WriteTag(tag!);
                w.WriteStringValue("v");
            });

            using YamlDocument back = YamlDocument.Parse(written);
            Assert.Equal(tag, back.Root.Tag);
        }

        Assert.Equal("!!foo", source.Root["f"].Tag);
        Assert.Equal("!e!w", source.Root["h"].Tag);
    }

    [Theory, MemberData(nameof(Paths))]
    public void Tag_discriminators_that_look_like_shorthands_round_trip(int path)
    {
        if (path == 2)
        {
            return; // Writing has no document path.
        }

        foreach ((TagShape shape, string tag) in new (TagShape, string)[] { (new TagOne { A = 1 }, "!!foo"), (new TagTwo { B = 2 }, "!e!x") })
        {
            string yaml = Write<TagShape>(path, shape);
            Assert.StartsWith("!", yaml, StringComparison.Ordinal);
            using YamlDocument doc = YamlDocument.Parse(yaml);
            Assert.Equal(tag, doc.Root.Tag);
            Assert.Equal(shape.GetType(), Read<TagShape>(path, yaml)!.GetType());
        }
    }

    // ---- the document's own settings on its nodes ----

    private static string AliasFan()
    {
        var sb = new StringBuilder("a: &a [1,1,1,1,1,1,1,1,1,1]\n");
        string previous = "a";
        foreach (string name in new[] { "b", "c", "d", "e" })
        {
            sb.Append(name).Append(": &").Append(name).Append(" [").Append(string.Join(",", Enumerable.Repeat("*" + previous, 10))).Append("]\n");
            previous = name;
        }

        return sb.ToString();
    }

    private static readonly YamlLimits s_tiny = YamlLimits.Untrusted with { AliasNodeFloor = 1000, AliasNodeFactor = 1, AliasByteFloor = 4096, AliasByteFactor = 1 };

    [Fact]
    public void Binding_a_node_without_options_keeps_the_limits_the_document_was_opened_with()
    {
        string fan = AliasFan();

        Assert.NotNull(YamlDocument.Parse(fan).Root.Deserialize<object>());
        using YamlDocument doc = YamlDocument.Parse(fan, new YamlDocumentOptions { Limits = s_tiny });

        YamlException ex = Assert.Throws<YamlException>(() => doc.Root.Deserialize<object>());
        Assert.Equal(YamlErrorCategory.Limit, ex.Category);
        ex = Assert.Throws<YamlException>(() => doc.Get<List<object?>>("/e"));
        Assert.Equal(YamlErrorCategory.Limit, ex.Category);
        ex = Assert.Throws<YamlException>(() => doc.Root["e"].GetValue<List<object?>>());
        Assert.Equal(YamlErrorCategory.Limit, ex.Category);
    }

    [Fact]
    public void Options_given_to_a_node_cannot_loosen_the_limits_of_its_document()
    {
        using YamlDocument doc = YamlDocument.Parse(AliasFan(), new YamlDocumentOptions { Limits = s_tiny });

        foreach (YamlSerializerOptions options in new[] { new YamlSerializerOptions(), YamlSerializerOptions.Default, new YamlSerializerOptions { Limits = YamlLimits.None } })
        {
            YamlException ex = Assert.Throws<YamlException>(() => doc.Root.Deserialize<object>(options));
            Assert.Equal(YamlErrorCategory.Limit, ex.Category);
        }

        // Tighter options still apply, and the same options object is reused.
        var tighter = new YamlSerializerOptions { Limits = YamlLimits.Untrusted with { MaxDepth = 2 } };
        Assert.Equal(YamlErrorCategory.Limit, Assert.Throws<YamlException>(() => doc.Root.Deserialize<object>(tighter)).Category);
        Assert.Equal(YamlErrorCategory.Limit, Assert.Throws<YamlException>(() => doc.Root.Deserialize<object>(tighter)).Category);
    }

    [Fact]
    public void Generated_metadata_given_to_a_node_cannot_loosen_the_limits_of_its_document()
    {
        // The metadata comes from a context whose own limits are the defaults, far above what this document allows.
        var context = new ApiConsistencyContext(new YamlSerializerOptions { Limits = YamlLimits.None });
        var objectInfo = (YamlTypeInfo<object>)context.GetTypeInfo(typeof(object))!;
        var listInfo = (YamlTypeInfo<List<object>>)context.GetTypeInfo(typeof(List<object>))!;
        using YamlDocument doc = YamlDocument.Parse(AliasFan(), new YamlDocumentOptions { Limits = s_tiny });

        Assert.Equal(YamlErrorCategory.Limit, Assert.Throws<YamlException>(() => doc.Root.Deserialize(objectInfo)).Category);
        Assert.Equal(YamlErrorCategory.Limit, Assert.Throws<YamlException>(() => doc.Root["e"].Deserialize(listInfo)).Category);
        Assert.Equal(YamlErrorCategory.Limit, Assert.Throws<YamlException>(() => doc.Root["e"].GetValue(listInfo)).Category);
        Assert.Equal(YamlErrorCategory.Limit, Assert.Throws<YamlException>(() => doc.Get("/e", listInfo)).Category);
    }

    [Fact]
    public void A_typed_try_read_that_does_not_fit_makes_a_quiet_error_without_the_work_of_a_position()
    {
        using YamlDocument doc = YamlDocument.Parse(Spec);
        int thread = Environment.CurrentManagedThreadId;
        var seen = new List<YamlException>();
        void OnThrown(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
        {
            if (Environment.CurrentManagedThreadId == thread && e.Exception is YamlException y)
            {
                seen.Add(y);
            }
        }

        AppDomain.CurrentDomain.FirstChanceException += OnThrown;
        try
        {
            Assert.False(doc.Root["spec"]["mode"].TryGetValue(out RolloutPlan? _));
            Assert.False(doc.TryGet("/spec/bad", out RolloutPlan? _));
            Assert.False(doc.TryGet("/spec/mode", out RolloutPlan? _));
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= OnThrown;
        }

        // A quiet error carries its category and offset and nothing that costs a scan of the text to find.
        Assert.NotEmpty(seen);
        Assert.All(seen, y => Assert.Equal((0, 0), (y.Line, y.Column)));
    }

    [Fact]
    public void A_source_name_reaches_errors_from_parsing_and_from_binding_a_node()
    {
        YamlException parse = Assert.Throws<YamlException>(() => YamlDocument.Parse("a: [1\n", new YamlDocumentOptions { SourceName = "app.yaml" }));
        Assert.Equal("app.yaml", parse.SourceName);
        Assert.StartsWith("app.yaml: ", parse.Message, StringComparison.Ordinal);

        using YamlDocument doc = YamlDocument.Parse("replicas: three\n");
        YamlException named = Assert.Throws<YamlException>(() => doc.Root.Deserialize<DeploymentDoc>(new YamlSerializerOptions { SourceName = "svc.yaml" }));
        Assert.StartsWith("svc.yaml: ", named.Message, StringComparison.Ordinal);

        using YamlDocument inherits = YamlDocument.Parse("replicas: three\n", new YamlDocumentOptions { SourceName = "doc.yaml" });
        YamlException fromDocument = Assert.Throws<YamlException>(() => inherits.Root.Deserialize<DeploymentDoc>());
        Assert.StartsWith("doc.yaml: ", fromDocument.Message, StringComparison.Ordinal);
    }

    // ---- one exception family per kind of miss ----

    [Fact]
    public void A_bad_index_is_out_of_range_the_wrong_kind_of_node_is_an_invalid_operation_and_a_missing_key_is_not_found()
    {
        const string text = "a: [1, 2]\nb: x\nc: {k: v}\n";
        using YamlDocument doc = YamlDocument.Parse(text);

        ArgumentOutOfRangeException tooFar = Assert.Throws<ArgumentOutOfRangeException>(() => doc.Root["a"][5]);
        Assert.Contains("line 1", tooFar.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.Root["a"][-1]);

        InvalidOperationException wrong = Assert.Throws<InvalidOperationException>(() => doc.Root["c"][0]);
        Assert.Contains("line 3", wrong.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => doc.Root["b"][0]);
        Assert.Throws<InvalidOperationException>(() => doc.Root["a"]["k"]);
        Assert.Throws<InvalidOperationException>(() => doc.Root["b"]["k"]);

        KeyNotFoundException missing = Assert.Throws<KeyNotFoundException>(() => doc.Root["c"]["zz"]);
        Assert.Contains("line 3", missing.Message, StringComparison.Ordinal);

        // Writing follows the same families, and a refused write leaves the text alone.
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.Root["a"][9] = 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.Root["a"][-1] = 1);
        Assert.Throws<InvalidOperationException>(() => doc.Root["c"][0] = 1);
        Assert.Throws<InvalidOperationException>(() => doc.Root["a"]["k"] = 1);
        Assert.Equal(text, doc.ToString());

        // A pointer is a path, and a miss there is still the library's own error.
        YamlException pointer = Assert.Throws<YamlException>(() => doc.Get<int>("/a/5"));
        Assert.Equal(YamlErrorCategory.PatchPath, pointer.Category);
    }

    // ---- DeserializeAll ----

    [Fact]
    public void DeserializeAll_returns_a_read_only_list_from_every_overload()
    {
        MethodInfo[] all = typeof(YamlSerializer).GetMethods().Where(m => m.Name == "DeserializeAll").ToArray();

        Assert.Equal(8, all.Length);
        Assert.All(all, m => Assert.Equal(typeof(IReadOnlyList<>), m.ReturnType.GetGenericTypeDefinition()));
    }

    // ---- exceptions built by callers ----

    [Fact]
    public void A_YamlException_has_three_public_constructors_and_init_properties_for_every_field()
    {
        Assert.Equal(3, typeof(YamlException).GetConstructors().Length);

        var e = new YamlException("bad value")
        {
            Category = YamlErrorCategory.Binding,
            Line = 3,
            Column = 4,
            Offset = 9,
            OperationIndex = 2,
            Path = "/a/b",
            SourceName = "f.yaml",
        };

        Assert.Equal(YamlErrorCategory.Binding, e.Category);
        Assert.Equal((3, 4, 9L, 2), (e.Line, e.Column, e.Offset, e.OperationIndex));
        Assert.Equal("/a/b", e.Path);
        Assert.Equal("f.yaml: bad value", e.Message);
        Assert.Equal(-1, new YamlException().Offset);
        Assert.Same(e, new YamlException("outer", e).InnerException);
    }

    // ---- polymorphism: renamed members, discriminator anywhere ----

    [Fact]
    public void The_polymorphism_attributes_name_the_kind_and_the_value_apart()
    {
        var polymorphic = new YamlPolymorphicAttribute { DiscriminatorKind = YamlDiscriminatorKind.Tag };
        var derived = new YamlDerivedTypeAttribute(typeof(WideCircle), "circle");

        Assert.Equal(YamlDiscriminatorKind.Tag, polymorphic.DiscriminatorKind);
        Assert.Equal("circle", derived.TypeDiscriminator);
    }

    [Theory, MemberData(nameof(Paths))]
    public void A_manifest_whose_kind_comes_after_apiVersion_binds_to_the_derived_type(int path)
    {
        ConfigMapDoc map = Assert.IsType<ConfigMapDoc>(Read<ManifestDoc>(path, "apiVersion: v1\nkind: ConfigMap\ndata:\n  a: b\n"));
        Assert.Equal("v1", map.ApiVersion);
        Assert.Equal("b", map.Data["a"]);

        DeploymentDoc last = Assert.IsType<DeploymentDoc>(Read<ManifestDoc>(path, "apiVersion: apps/v1\nreplicas: 3\nunknown: {x: [1, 2]}\nkind: Deployment\n"));
        Assert.Equal((3, "apps/v1"), (last.Replicas, last.ApiVersion));
    }

    [Theory, MemberData(nameof(Paths))]
    public void A_list_of_manifests_with_the_kind_last_at_every_level_binds_every_item(int path)
    {
        const string yaml = "apiVersion: v1\nitems:\n- apiVersion: v1\n  data: {a: b}\n  kind: ConfigMap\n- apiVersion: apps/v1\n  replicas: 2\n  kind: Deployment\nkind: List\n";

        ListDoc list = Assert.IsType<ListDoc>(Read<ManifestDoc>(path, yaml));

        Assert.Equal(2, list.Items.Count);
        Assert.Equal("b", Assert.IsType<ConfigMapDoc>(list.Items[0]).Data["a"]);
        Assert.Equal(2, Assert.IsType<DeploymentDoc>(list.Items[1]).Replicas);
    }

    [Theory, MemberData(nameof(Paths))]
    public void A_discriminator_after_other_keys_reads_through_anchors_and_aliases_before_it(int path)
    {
        DeploymentDoc d = Assert.IsType<DeploymentDoc>(Read<ManifestDoc>(path, "defaults: &d {replicas: 2, tags: [x, y]}\ncopy: *d\napiVersion: v1\nkind: Deployment\nreplicas: 5\n"));

        Assert.Equal(5, d.Replicas);
    }

    [Theory, MemberData(nameof(Paths))]
    public void A_repeated_or_unknown_discriminator_after_other_keys_is_an_error(int path)
    {
        YamlException twice = Assert.Throws<YamlException>(() => Read<ManifestDoc>(path, "apiVersion: v1\nkind: ConfigMap\nkind: List\n"));
        Assert.Equal(YamlErrorCategory.DuplicateKey, twice.Category);
        Assert.Equal(3, twice.Line);

        YamlException unknown = Assert.Throws<YamlException>(() => Read<ManifestDoc>(path, "apiVersion: v1\nkind: Nope\n"));
        Assert.Equal(YamlErrorCategory.Binding, unknown.Category);
        Assert.Equal(2, unknown.Line);

        YamlException notText = Assert.Throws<YamlException>(() => Read<ManifestDoc>(path, "apiVersion: v1\nkind: [a]\n"));
        Assert.Equal(YamlErrorCategory.Binding, notText.Category);
    }

    [Theory, MemberData(nameof(Paths))]
    public void A_mapping_without_the_discriminator_is_the_base_when_the_base_can_be_created(int path)
    {
        ManifestDoc plain = Read<ManifestDoc>(path, "apiVersion: v1\nother: 1\n")!;

        Assert.Equal(typeof(ManifestDoc), plain.GetType());
        Assert.Equal("v1", plain.ApiVersion);
    }

    [Theory, MemberData(nameof(Paths))]
    public void A_shape_whose_kind_is_not_first_binds_and_one_without_it_is_refused(int path)
    {
        Assert.Equal(4, Assert.IsType<WideSquare>(Read<WideShape>(path, "label: s\nside: 4\n$type: square\n")).Side);

        YamlException ex = Assert.Throws<YamlException>(() => Read<WideShape>(path, "label: s\nside: 4\n"));
        Assert.Equal(YamlErrorCategory.Binding, ex.Category);
        Assert.Contains("$type", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Looking_ahead_for_the_discriminator_is_charged_to_the_alias_budget()
    {
        // Every level names its type last, so each one looks past all the levels inside it.
        const int levels = 400;
        string yaml = string.Concat(Enumerable.Repeat("items: [{", levels)) + "kind: List" + string.Concat(Enumerable.Repeat("}], kind: List", levels - 1)) + "}]\nkind: List\n";
        var tight = new YamlSerializerOptions { Limits = YamlLimits.Untrusted with { MaxDepth = 1000, AliasNodeFloor = 5000, AliasNodeFactor = 1 } };

        YamlException ex = Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<ManifestDoc>(yaml, tight));

        Assert.Equal(YamlErrorCategory.Limit, ex.Category);
    }

    // ---- System.Text.Json members ----

    [Theory, MemberData(nameof(Paths))]
    public void System_Text_Json_members_bind_and_write_back_without_losing_data(int path)
    {
        const string yaml = "name: x\nsettings:\n  retries: 3\n  mode: fast\n  ratio: 1.5\n  list: [1, true, null, big]\n  big: 123456789012345678901234567890\nnode: {a: [1, 2], b: text}\nobject: {k: v}\narray: [1, two]\n";

        JsonHolder h = Read<JsonHolder>(path, yaml)!;

        Assert.Equal(JsonValueKind.Object, h.Settings.ValueKind);
        Assert.Equal(3, h.Settings.GetProperty("retries").GetInt32());
        Assert.Equal("fast", h.Settings.GetProperty("mode").GetString());
        Assert.Equal(1.5, h.Settings.GetProperty("ratio").GetDouble());
        Assert.Equal(JsonValueKind.Null, h.Settings.GetProperty("list")[2].ValueKind);
        Assert.Equal("123456789012345678901234567890", h.Settings.GetProperty("big").GetRawText());
        Assert.Equal(2, h.Node!["a"]![1]!.GetValue<long>());
        Assert.Equal("v", (string?)h.Object!["k"]);
        Assert.Equal("two", (string?)h.Array![1]);

        string written = Write(path, h);
        JsonHolder back = Read<JsonHolder>(path, written)!;

        // Compared with the text the YAML says, not with the first load: both loads could be wrong the same way.
        Assert.Equal("{\"retries\":3,\"mode\":\"fast\",\"ratio\":1.5,\"list\":[1,true,null,\"big\"],\"big\":123456789012345678901234567890}", back.Settings.GetRawText());
        Assert.Equal("{\"a\":[1,2],\"b\":\"text\"}", back.Node!.ToJsonString());
        Assert.Equal("{\"k\":\"v\"}", back.Object!.ToJsonString());
        Assert.Equal("[1,\"two\"]", back.Array!.ToJsonString());
    }

    private const string NumberYaml = "name: x\nsettings: {a: -0.0, b: 1.0, c: 1e3, d: 3.141592653589793238462643383279, e: 1e-400, f: 1.5e400, g: -0, h: .5, i: 1., j: 0x1F, k: 007, l: +3, m: 1E5, p: 0.1}\n";

    private const string NumberJson = "{\"a\":-0.0,\"b\":1.0,\"c\":1e3,\"d\":3.141592653589793238462643383279,\"e\":1e-400,\"f\":1.5e400,\"g\":-0,\"h\":0.5,\"i\":1.0,\"j\":31,\"k\":7,\"l\":3,\"m\":1E5,\"p\":0.1}";

    [Theory, MemberData(nameof(Paths))]
    public void JSON_members_read_a_number_with_the_digits_it_was_written_with(int path)
    {
        JsonHolder h = Read<JsonHolder>(path, NumberYaml)!;

        Assert.Equal(NumberJson, h.Settings.GetRawText());
        JsonHolder viaNode = Read<JsonHolder>(path, NumberYaml.Replace("settings:", "node:", StringComparison.Ordinal))!;
        Assert.Equal(NumberJson, viaNode.Node!.ToJsonString());
    }

    [Theory, MemberData(nameof(Paths))]
    public void JSON_members_write_a_number_as_the_digits_it_has(int path)
    {
        using JsonDocument source = JsonDocument.Parse(NumberJson);
        var h = new JsonHolder { Name = "x", Settings = source.RootElement.Clone(), Node = JsonNode.Parse(NumberJson) };

        string yaml = Write(path, h);

        foreach (string line in new[] { "a: -0.0", "b: 1.0", "c: 1e3", "d: 3.141592653589793238462643383279", "e: 1e-400", "f: 1.5e400", "g: -0", "m: 1E5", "p: 0.1" })
        {
            Assert.Contains("  " + line + "\n", yaml, StringComparison.Ordinal);
        }

        JsonHolder back = Read<JsonHolder>(path, yaml)!;
        string expected = NumberJson;
        Assert.Equal(expected, back.Settings.GetRawText());
        Assert.Equal(expected, back.Node!.ToJsonString());
    }

    [Fact]
    public void A_JSON_node_member_that_is_a_scalar_is_built_without_a_detour_through_JSON_text()
    {
        // Each item used to be written out as JSON text and parsed again, about 760 bytes for a small scalar.
        const int items = 4000;
        string yaml = string.Join("\n", Enumerable.Range(0, items).Select(i => (i % 5) switch
        {
            0 => $"- {i}",
            1 => $"- name{i}",
            2 => "- true",
            3 => "- null",
            _ => $"- {i}.5",
        })) + "\n";

        // A scalar member is allowed 400 bytes; the detour through JSON text costs about twice that on every call and
        // never gets under it. SteadyState waits for optimised code and counts this thread's allocations only.
        const long Bound = 400;
        List<JsonNode?>? nodes = null;
        SteadyState.AssertAllocatesAtMost(() => nodes = YamlSerializer.Deserialize<List<JsonNode?>>(yaml), 1, Bound * items);

        Assert.NotNull(nodes);
        Assert.Equal(items, nodes.Count);
        Assert.Equal(3, nodes[0]!.GetValue<int>() + 3);
        Assert.Equal("name1", nodes[1]!.GetValue<string>());
        Assert.Equal("4.5", nodes[4]!.ToJsonString());
    }

    [Theory, MemberData(nameof(Paths))]
    public void A_JSON_element_with_a_repeated_property_name_is_refused_when_written(int path)
    {
        // JsonDocument keeps both entries. YAML needs unique keys, and the text written must read back.
        foreach (string json in new[] { "{\"a\":1,\"a\":2}", "{\"x\":[{\"k\":1,\"b\":2,\"k\":3}]}" })
        {
            using JsonDocument source = JsonDocument.Parse(json);
            var holder = new JsonHolder { Name = "x", Settings = source.RootElement.Clone() };

            YamlException ex = Assert.Throws<YamlException>(() => Write(path, holder));
            Assert.Equal(YamlErrorCategory.Binding, ex.Category);
            Assert.Contains(json.Contains("\"k\":3") ? "'k'" : "'a'", ex.Message, StringComparison.Ordinal);
        }

        // The same names in different objects are fine.
        using JsonDocument fine = JsonDocument.Parse("{\"a\":{\"a\":1},\"b\":[{\"a\":2},{\"a\":3}]}");
        string yaml = Write(path, new JsonHolder { Name = "x", Settings = fine.RootElement.Clone() });
        Assert.Equal("{\"a\":{\"a\":1},\"b\":[{\"a\":2},{\"a\":3}]}", Read<JsonHolder>(path, yaml)!.Settings.GetRawText());
    }

    [Theory, MemberData(nameof(Paths))]
    public void JSON_members_refuse_not_a_number_and_infinity_from_YAML(int path)
    {
        foreach (string value in new[] { ".inf", "-.inf", ".nan" })
        {
            YamlException ex = Assert.Throws<YamlException>(() => Read<JsonHolder>(path, "settings: {a: " + value + "}\n"));
            Assert.Equal(YamlErrorCategory.Binding, ex.Category);
        }
    }

    private static JsonNode Nested(int levels)
    {
        JsonNode node = new JsonArray();
        for (int i = 1; i < levels; i++)
        {
            node = new JsonArray(node);
        }

        return node;
    }

    [Theory, MemberData(nameof(Paths))]
    public void A_JSON_node_nested_past_the_depth_the_types_read_is_a_limit_error_when_written(int path)
    {
        var fits = new JsonHolder { Node = Nested(JsonTrees.MaxJsonDepth) };
        Assert.Equal(fits.Node.ToJsonString(), Read<JsonHolder>(path, Write(path, fits))!.Node!.ToJsonString());

        foreach (int levels in new[] { JsonTrees.MaxJsonDepth + 1, 1_001, 5_000 })
        {
            var tooDeep = new JsonHolder { Node = Nested(levels) };
            YamlException ex = Assert.Throws<YamlException>(() => Write(path, tooDeep));
            Assert.Equal(YamlErrorCategory.Limit, ex.Category);
        }
    }

    [Theory, MemberData(nameof(Paths))]
    public void A_JSON_element_nested_past_the_depth_the_types_read_is_a_limit_error_when_written(int path)
    {
        string json = new string('[', 100) + new string(']', 100);
        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 200 });

        YamlException ex = Assert.Throws<YamlException>(() => Write(path, new JsonHolder { Settings = document.RootElement }));

        Assert.Equal(YamlErrorCategory.Limit, ex.Category);
    }

    [Theory, MemberData(nameof(Paths))]
    public void A_null_reads_as_a_JSON_null_element_and_a_null_node(int path)
    {
        JsonHolder h = Read<JsonHolder>(path, "name: x\nsettings: null\nnode: null\n")!;

        Assert.Equal(JsonValueKind.Null, h.Settings.ValueKind);
        Assert.Null(h.Node);
    }

    [Theory, MemberData(nameof(Paths))]
    public void A_JSON_type_that_does_not_fit_the_member_is_a_binding_error(int path)
    {
        YamlException ex = Assert.Throws<YamlException>(() => Read<JsonHolder>(path, "name: x\nobject: [1]\n"));

        Assert.Equal(YamlErrorCategory.Binding, ex.Category);
    }

    [Fact]
    public void A_JSON_document_member_is_refused_when_the_metadata_is_made_by_either_path()
    {
        Assert.Throws<NotSupportedException>(() => YamlSerializer.Deserialize<DocumentHolder>("document: {a: 1}\n"));
        Assert.Throws<NotSupportedException>(() => YamlSerializer.Serialize(new DocumentHolder()));
        Assert.Throws<NotSupportedException>(() => YamlSerializer.Deserialize("document: {a: 1}\n", (YamlTypeInfo<DocumentHolder>)s_context.GetTypeInfo(typeof(DocumentHolder))!));
    }

    // ---- reading back what Set writes ----

    private const string Spec = "spec:\n  s: {type: RollingUpdate, surge: 2}\n  mode: IfNotPresent\n  ports: [80, 443]\n  bad: {type: x, surge: many}\n";

    [Fact]
    public void Get_and_TryGet_read_what_Set_writes_including_collections_and_enums()
    {
        using YamlDocument doc = YamlDocument.Parse(Spec);

        RolloutPlan plan = doc.Get<RolloutPlan>("/spec/s");
        Assert.Equal(("RollingUpdate", 2), (plan.Type, plan.Surge));
        Assert.Equal(PullMode.IfNotPresent, doc.Get<PullMode>("/spec/mode"));
        Assert.Equal(PullMode.IfNotPresent, doc.Root["spec"]["mode"].GetValue<PullMode>());
        Assert.Equal([80, 443], doc.Get<List<int>>("/spec/ports"));
        Assert.Equal(443, doc.Get<int[]>("/spec/ports")[1]);

        Assert.True(doc.TryGet("/spec/s", out RolloutPlan? read));
        Assert.Equal(2, read!.Surge);
        Assert.False(doc.TryGet("/spec/bad", out RolloutPlan? _));
        Assert.False(doc.TryGet("/spec/missing", out RolloutPlan? _));
        Assert.False(doc.TryGet("/spec/mode", out RolloutPlan? _));

        doc.Set("/spec/s", new RolloutPlan { Type = "Recreate", Surge = 7 });
        Assert.Equal(7, doc.Get<RolloutPlan>("/spec/s").Surge);
        doc.Set("/spec/mode", PullMode.Always);
        Assert.Equal(PullMode.Always, doc.Get<PullMode>("/spec/mode"));
    }

    [Fact]
    public void A_typed_read_names_the_position_and_path_of_what_does_not_fit()
    {
        using YamlDocument doc = YamlDocument.Parse(Spec);

        YamlException notScalar = Assert.Throws<YamlException>(() => doc.Get<int>("/spec/s"));
        Assert.Equal(YamlErrorCategory.Binding, notScalar.Category);
        Assert.Equal("/spec/s", notScalar.Path);
        Assert.Equal(2, notScalar.Line);

        YamlException bad = Assert.Throws<YamlException>(() => doc.Get<RolloutPlan>("/spec/bad"));
        Assert.Equal("/spec/bad/surge", bad.Path);
    }

    [Fact]
    public void Get_reads_through_generated_metadata_without_reflection()
    {
        using YamlDocument doc = YamlDocument.Parse(Spec);
        var info = (YamlTypeInfo<RolloutPlan>)s_context.GetTypeInfo(typeof(RolloutPlan))!;
        var modeInfo = (YamlTypeInfo<PullMode>)s_context.GetTypeInfo(typeof(PullMode))!;

        Assert.Equal(2, doc.Get("/spec/s", info).Surge);
        Assert.Equal(PullMode.IfNotPresent, doc.Root["spec"]["mode"].GetValue(modeInfo));
    }

    // ---- copied options ----

    [Fact]
    public void Options_can_be_copied_and_the_copy_changed_before_use()
    {
        var original = new YamlSerializerOptions
        {
            PropertyNamingPolicy = null,
            EnumNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            UnmappedMemberHandling = YamlUnmappedMemberHandling.Disallow,
            IndentSize = 4,
            DefaultIgnoreCondition = YamlIgnoreCondition.WhenWritingNull,
            Limits = YamlLimits.Untrusted,
            MergeKeys = true,
            SourceName = "a.yaml",
            Converters = { new TagForms() },
        };

        // Using the original locks it; the copy is still open.
        YamlSerializer.Serialize(new RolloutPlan(), original);
        var copy = new YamlSerializerOptions(original) { SourceName = "b.yaml" };
        copy.Converters.Add(new TagForms());

        Assert.Equal("b.yaml", copy.SourceName);
        Assert.Equal("a.yaml", original.SourceName);
        Assert.Null(copy.PropertyNamingPolicy);
        Assert.Same(JsonNamingPolicy.CamelCase, copy.EnumNamingPolicy);
        Assert.True(copy.PropertyNameCaseInsensitive);
        Assert.Equal(YamlUnmappedMemberHandling.Disallow, copy.UnmappedMemberHandling);
        Assert.Equal(4, copy.IndentSize);
        Assert.Equal(YamlIgnoreCondition.WhenWritingNull, copy.DefaultIgnoreCondition);
        Assert.Same(YamlLimits.Untrusted, copy.Limits);
        Assert.True(copy.MergeKeys);
        Assert.Equal(2, copy.Converters.Count);
        Assert.Single(original.Converters);

        Assert.Throws<ArgumentNullException>(() => new YamlSerializerOptions(null!));
    }

    // ---- smaller messages ----

    // Visits every node, following aliases, so an alias bomb is expanded one step at a time.
    private static void Walk(YamlNode node)
    {
        YamlNode target = node.Kind == YamlNodeKind.Alias ? node.Target : node;
        if (target.Kind is YamlNodeKind.Sequence or YamlNodeKind.Mapping)
        {
            foreach (YamlNode child in target.Items)
            {
                Walk(child);
            }
        }
    }

    [Fact]
    public void The_alias_limit_error_of_a_document_names_the_settings()
    {
        using YamlDocument doc = YamlDocument.Parse(AliasFan(), new YamlDocumentOptions { Limits = s_tiny });

        YamlException ex = Assert.ThrowsAny<YamlException>(() => Walk(doc.Root));

        Assert.Equal(YamlErrorCategory.Limit, ex.Category);
        Assert.Contains("AliasNodeFactor 1", ex.Message, StringComparison.Ordinal);
        Assert.Contains("AliasNodeFloor 1000", ex.Message, StringComparison.Ordinal);
    }

    [Theory, MemberData(nameof(Paths))]
    public void A_missing_required_key_reports_the_path_of_the_mapping_not_of_its_first_key(int path)
    {
        YamlException top = Assert.Throws<YamlException>(() => Read<NeedsName>(path, "other: 1\n"));
        Assert.Equal(string.Empty, top.Path);

        YamlException nested = Assert.Throws<YamlException>(() => Read<NeedsNameParent>(path, "inner:\n  other: 1\n"));
        Assert.Equal("/inner", nested.Path);
    }

    [Fact]
    public void A_detached_node_prints_the_YAML_of_its_value()
    {
        Assert.Equal("a: 1\nb:\n  - x\n  - y", YamlNode.Create(new Dictionary<string, object?> { ["a"] = 1, ["b"] = new List<object?> { "x", "y" } }).ToString());
        Assert.Equal("text", YamlNode.Create("text").ToString());
        Assert.Equal("5", YamlNode.Create(5).ToString());
    }
}
