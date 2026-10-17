using System.Buffers;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OneYaml.Serialization.Metadata;
using Xunit;

namespace OneYaml.Tests;

internal enum PullPolicy
{
    Always,
    IfNotPresent,
    Never,
}

internal enum Proto
{
    TCP,
    UDP,
}

internal enum Access
{
#if NET9_0_OR_GREATER
    [JsonStringEnumMemberName("ro")]
#endif
    ReadOnly,

    [YamlEnumMemberName("rw")]
    ReadWrite,

    Other,
}

[Flags]
internal enum Rights
{
    None = 0,

    [YamlEnumMemberName("r")]
    Read = 1,

    Write = 2,
}

internal sealed class Container
{
    public string Name { get; set; } = string.Empty;

    public PullPolicy ImagePullPolicy { get; set; }

    public Proto Protocol { get; set; }

    public Access Access { get; set; }

    public Rights Rights { get; set; }
}

internal sealed class Immutables
{
    public ImmutableArray<int> Array { get; set; }

    public ImmutableList<string>? List { get; set; }

    public ImmutableHashSet<int>? HashSet { get; set; }

    public ImmutableSortedSet<int>? SortedSet { get; set; }

    public ImmutableQueue<int>? Queue { get; set; }

    public ImmutableStack<int>? Stack { get; set; }

    public ImmutableDictionary<string, int>? Dictionary { get; set; }

    public ImmutableSortedDictionary<string, int>? SortedDictionary { get; set; }

    public IImmutableList<int>? IList { get; set; }

    public IImmutableSet<int>? ISet { get; set; }

    public IImmutableQueue<int>? IQueue { get; set; }

    public IImmutableStack<int>? IStack { get; set; }

    public IImmutableDictionary<string, int>? IDictionary { get; set; }
}

internal sealed class Endpoint
{
    public string Path { get; set; } = string.Empty;

    public int Weight { get; set; }
}

internal sealed class AppConfig
{
    public string Name { get; set; } = string.Empty;

    public int Replicas { get; set; }

    public List<Endpoint> Endpoints { get; set; } = [];

    public Dictionary<string, Endpoint> ByName { get; set; } = [];
}

// A struct collection cannot be filled through the reflection resolver's collection maker.
internal struct StructBag : ICollection<int>
{
    public readonly int Count => 0;

    public readonly bool IsReadOnly => false;

    public readonly void Add(int item)
    {
    }

    public readonly void Clear()
    {
    }

    public readonly bool Contains(int item) => false;

    public readonly void CopyTo(int[] array, int arrayIndex)
    {
    }

    public readonly IEnumerator<int> GetEnumerator() => Enumerable.Empty<int>().GetEnumerator();

    public readonly bool Remove(int item) => false;

    readonly System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

internal sealed class HoldsStructBag
{
    public StructBag Bag { get; set; }
}

internal sealed class Slash
{
    [YamlPropertyName("a/b")]
    public int Value { get; set; }
}

[YamlSerializable(typeof(Container))]
[YamlSerializable(typeof(Immutables))]
[YamlSerializable(typeof(AppConfig))]
[YamlSerializable(typeof(Slash))]
[YamlSerializable(typeof(Access))]
[YamlSerializable(typeof(Rights))]
[YamlSerializable(typeof(PullPolicy))]
[YamlSerializable(typeof(List<int>))]
[YamlSerializable(typeof(Endpoint))]
internal sealed partial class ApiShapeContext : YamlSerializerContext
{
}

public class ApiShapeTests
{
    // ---- errors ----

    private static YamlException Fail(Action action)
    {
        YamlException? error = null;
        try
        {
            action();
        }
        catch (YamlException e)
        {
            error = e;
        }

        Assert.NotNull(error);
        return error;
    }

    [Fact]
    public void A_value_error_names_the_position_the_path_and_the_value()
    {
        YamlException e = Fail(() => YamlSerializer.Deserialize<AppConfig>("name: x\nendpoints:\n  - path: /a\n    weight: 1\n  - path: /b\n    weight: heavy\n"));

        Assert.Equal("/endpoints/1/weight", e.Path);
        Assert.Equal(YamlErrorCategory.Binding, e.Category);
        Assert.Equal(6, e.Line);
        Assert.Contains("'heavy'", e.Message, StringComparison.Ordinal);
        Assert.Contains("line 6, column 13", e.Message, StringComparison.Ordinal);
        Assert.Contains("path /endpoints/1/weight", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_range_error_names_the_number()
    {
        YamlException e = Fail(() => YamlSerializer.Deserialize<AppConfig>("replicas: 99999999999\n"));

        Assert.Contains("'99999999999'", e.Message, StringComparison.Ordinal);
        Assert.Equal("/replicas", e.Path);
    }

    [Fact]
    public void An_unknown_key_is_named()
    {
        var options = new YamlSerializerOptions { UnmappedMemberHandling = YamlUnmappedMemberHandling.Disallow };
        YamlException e = Fail(() => YamlSerializer.Deserialize<AppConfig>("name: x\nreplicaz: 3\n", options));

        Assert.Contains("'replicaz'", e.Message, StringComparison.Ordinal);
        Assert.Contains("AppConfig", e.Message, StringComparison.Ordinal);
        Assert.Equal("/replicaz", e.Path);
    }

    [Fact]
    public void An_unknown_key_in_a_nested_mapping_is_named_with_its_path()
    {
        var options = new YamlSerializerOptions { UnmappedMemberHandling = YamlUnmappedMemberHandling.Disallow };
        YamlException e = Fail(() => YamlSerializer.Deserialize<AppConfig>("endpoints:\n  - nope: 1\n", options));

        Assert.Equal("/endpoints/0/nope", e.Path);
        Assert.Contains("'nope'", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_duplicate_key_is_named_by_every_path()
    {
        YamlException typed = Fail(() => YamlSerializer.Deserialize<AppConfig>("name: x\nname: y\n"));
        YamlException generic = Fail(() => YamlSerializer.Deserialize<Dictionary<string, object>>("a:\n  b: 1\n  b: 2\n"));

        Assert.Equal(YamlErrorCategory.DuplicateKey, typed.Category);
        Assert.Contains("'name'", typed.Message, StringComparison.Ordinal);
        Assert.Equal("/a/b", generic.Path);
        Assert.Contains("'b'", generic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_collection_where_a_value_is_expected_points_at_the_collection()
    {
        YamlException e = Fail(() => YamlSerializer.Deserialize<AppConfig>("name: x\nendpoints:\n  - path:\n      deep: 1\n"));

        Assert.Equal("/endpoints/0/path", e.Path);
    }

    [Fact]
    public void Keys_with_a_slash_are_escaped_in_the_path()
    {
        YamlException e = Fail(() => YamlSerializer.Deserialize<Slash>("a/b: nope\n"));

        Assert.Equal("/a~1b", e.Path);
    }

    [Fact]
    public void A_syntax_error_has_a_position_in_its_message_and_no_path()
    {
        YamlException e = Fail(() => YamlSerializer.Deserialize<AppConfig>("name: x\n  bad: indent\n"));

        Assert.Equal(YamlErrorCategory.Syntax, e.Category);
        Assert.Null(e.Path);
        Assert.Contains($"line {e.Line}, column {e.Column}", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_limit_error_names_the_limit_and_its_value()
    {
        string deep = string.Concat(Enumerable.Repeat("[", 1100)) + string.Concat(Enumerable.Repeat("]", 1100));
        YamlException depth = Fail(() => YamlSerializer.Deserialize<object>(deep));
        var small = new YamlSerializerOptions { Limits = YamlLimits.Default with { MaxInputBytes = 4 } };
        YamlException size = Fail(() => YamlSerializer.Deserialize<object>("a: 1234567\n", small));
        var digits = new YamlSerializerOptions { Limits = YamlLimits.Default with { MaxIntegerDigits = 5 } };
        YamlException integer = Fail(() => YamlSerializer.Deserialize<object>("a: 12345678901234567890\n", digits));

        Assert.Equal(YamlErrorCategory.Limit, depth.Category);
        Assert.Contains("MaxDepth (1000)", depth.Message, StringComparison.Ordinal);
        Assert.Contains("MaxInputBytes (4)", size.Message, StringComparison.Ordinal);
        Assert.Contains("MaxIntegerDigits (5)", integer.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_value_is_cut_short_in_the_message()
    {
        string value = new('9', 500);
        YamlException e = Fail(() => YamlSerializer.Deserialize<AppConfig>("name: x\nreplicas: " + value + "\n"));

        Assert.True(e.Message.Length < 300, e.Message);
        Assert.Contains("...", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_source_name_starts_the_message_for_every_kind_of_error()
    {
        var options = new YamlSerializerOptions { SourceName = "app.yaml" };

        YamlException binding = Fail(() => YamlSerializer.Deserialize<AppConfig>("replicas: x\n", options));
        YamlException syntax = Fail(() => YamlSerializer.Deserialize<AppConfig>("a: [1\n", options));
        YamlException encoding = Fail(() => YamlSerializer.Deserialize<AppConfig>(new byte[] { (byte)'a', (byte)':', (byte)' ', 0xFF, 0xFE }.AsSpan(), options));
        YamlException many = Fail(() => YamlSerializer.DeserializeAll<AppConfig>("replicas: x\n", options));

        foreach (YamlException e in new[] { binding, syntax, encoding, many })
        {
            Assert.StartsWith("app.yaml: ", e.Message, StringComparison.Ordinal);
            Assert.Equal("app.yaml", e.SourceName);
        }

        Assert.Null(Fail(() => YamlSerializer.Deserialize<AppConfig>("replicas: x\n")).SourceName);
    }

    [Fact]
    public void The_generated_path_reports_the_same_error()
    {
        var ctx = new ApiShapeContext();
        const string yaml = "name: x\nendpoints:\n  - weight: heavy\n";
        YamlException reflected = Fail(() => YamlSerializer.Deserialize<AppConfig>(yaml));
        YamlException generated = Fail(() => YamlSerializer.Deserialize(yaml, ctx.AppConfig));

        Assert.Equal(reflected.Message, generated.Message);
        Assert.Equal(reflected.Path, generated.Path);
    }

    // ---- enums ----

    [Fact]
    public void Enum_values_are_written_by_their_declared_names()
    {
        var c = new Container { Name = "api", ImagePullPolicy = PullPolicy.IfNotPresent, Protocol = Proto.TCP, Access = Access.Other };

        string yaml = YamlSerializer.Serialize(c);

        Assert.Contains("imagePullPolicy: IfNotPresent\n", yaml, StringComparison.Ordinal);
        Assert.Contains("protocol: TCP\n", yaml, StringComparison.Ordinal);
        Assert.Contains("access: Other\n", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void An_enum_naming_policy_renames_the_values_and_not_the_keys_again()
    {
        var options = new YamlSerializerOptions { EnumNamingPolicy = JsonNamingPolicy.KebabCaseLower };

        string yaml = YamlSerializer.Serialize(new Container { ImagePullPolicy = PullPolicy.IfNotPresent, Protocol = Proto.UDP }, options);

        Assert.Contains("imagePullPolicy: if-not-present\n", yaml, StringComparison.Ordinal);
        Assert.Contains("protocol: udp\n", yaml, StringComparison.Ordinal);
        Container back = YamlSerializer.Deserialize<Container>(yaml, options)!;
        Assert.Equal(PullPolicy.IfNotPresent, back.ImagePullPolicy);
        Assert.Equal(Proto.UDP, back.Protocol);
    }

    [Fact]
    public void The_property_naming_policy_no_longer_touches_enum_values()
    {
        var options = new YamlSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

        string yaml = YamlSerializer.Serialize(new Container { ImagePullPolicy = PullPolicy.Never }, options);

        Assert.Contains("image_pull_policy: Never\n", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Enum_member_attributes_name_the_value()
    {
        string yaml = YamlSerializer.Serialize(new Container { Access = Access.ReadWrite, Rights = Rights.Read | Rights.Write });

        Assert.Contains("access: rw\n", yaml, StringComparison.Ordinal);
        Assert.Contains("rights: r, Write\n", yaml, StringComparison.Ordinal);
#if NET9_0_OR_GREATER
        Assert.Contains("access: ro\n", YamlSerializer.Serialize(new Container { Access = Access.ReadOnly }), StringComparison.Ordinal);
#endif
    }

    [Fact]
    public void Reading_takes_the_attributed_name_the_declared_name_and_any_letter_case()
    {
        Assert.Equal(Access.ReadWrite, YamlSerializer.Deserialize<Container>("access: rw\n")!.Access);
        Assert.Equal(Access.ReadWrite, YamlSerializer.Deserialize<Container>("access: ReadWrite\n")!.Access);
        Assert.Equal(Access.ReadWrite, YamlSerializer.Deserialize<Container>("access: readwrite\n")!.Access);
        Assert.Equal(Rights.Read | Rights.Write, YamlSerializer.Deserialize<Container>("rights: r, Write\n")!.Rights);
        Assert.Equal(Rights.Read | Rights.Write, YamlSerializer.Deserialize<Container>("rights: Read, Write\n")!.Rights);
        Assert.Equal(PullPolicy.Never, YamlSerializer.Deserialize<Container>("imagePullPolicy: 2\n")!.ImagePullPolicy);
        Assert.Equal(PullPolicy.IfNotPresent, YamlSerializer.Deserialize<Container>("imagePullPolicy: ifNotPresent\n")!.ImagePullPolicy);
    }

    [Fact]
    public void The_generated_path_names_enum_values_as_reflection_does()
    {
        var options = new YamlSerializerOptions { EnumNamingPolicy = JsonNamingPolicy.SnakeCaseUpper };
        var ctx = new ApiShapeContext(options);
        var c = new Container { Name = "n", ImagePullPolicy = PullPolicy.IfNotPresent, Protocol = Proto.TCP, Access = Access.ReadWrite, Rights = Rights.Read | Rights.Write };

        string reflected = YamlSerializer.Serialize(c, options);
        string generated = YamlSerializer.Serialize(c, ctx.Container);

        Assert.Equal(reflected, generated);
        Assert.Contains("imagePullPolicy: IF_NOT_PRESENT\n", generated, StringComparison.Ordinal);
        Assert.Contains("access: rw\n", generated, StringComparison.Ordinal);
        Container back = YamlSerializer.Deserialize(generated, ctx.Container)!;
        Assert.Equal(c.ImagePullPolicy, back.ImagePullPolicy);
        Assert.Equal(c.Access, back.Access);
        Assert.Equal(c.Rights, back.Rights);
    }

    // ---- immutable collections ----

    private const string ImmutablesYaml = """
        array: [3, 1, 2]
        list: [b, a]
        hashSet: [1, 2, 3]
        sortedSet: [3, 1, 2]
        queue: [1, 2, 3]
        stack: [1, 2, 3]
        dictionary: {a: 1, b: 2}
        sortedDictionary: {b: 2, a: 1}
        iList: [1, 2]
        iSet: [5, 6]
        iQueue: [7, 8]
        iStack: [9, 10]
        iDictionary: {k: 1}
        """;

    private static void CheckImmutables(Immutables v)
    {
        Assert.Equal([3, 1, 2], v.Array);
        Assert.Equal(["b", "a"], v.List!);
        Assert.Equal(3, v.HashSet!.Count);
        Assert.Equal([1, 2, 3], v.SortedSet!);
        Assert.Equal([1, 2, 3], v.Queue!);
        // A stack enumerates from the top and is written that way, so reading restores the order written.
        Assert.Equal([1, 2, 3], v.Stack!.ToArray());
        Assert.Equal(2, v.Dictionary!["b"]);
        Assert.Equal(["a", "b"], v.SortedDictionary!.Keys);
        Assert.Equal([1, 2], v.IList!);
        Assert.True(v.ISet!.Contains(6));
        Assert.Equal([7, 8], v.IQueue!);
        Assert.Equal([9, 10], v.IStack!.ToArray());
        Assert.Equal(1, v.IDictionary!["k"]);
    }

    [Fact]
    public void Immutable_collections_read_and_write_by_reflection()
    {
        Immutables v = YamlSerializer.Deserialize<Immutables>(ImmutablesYaml)!;
        CheckImmutables(v);

        string written = YamlSerializer.Serialize(v);
        CheckImmutables(YamlSerializer.Deserialize<Immutables>(written)!);
        Assert.Equal(written, YamlSerializer.Serialize(YamlSerializer.Deserialize<Immutables>(written)));
    }

    [Fact]
    public void Immutable_collections_read_and_write_by_the_generator()
    {
        var ctx = new ApiShapeContext();
        Immutables v = YamlSerializer.Deserialize(ImmutablesYaml, ctx.Immutables)!;
        CheckImmutables(v);

        string written = YamlSerializer.Serialize(v, ctx.Immutables);
        Assert.Equal(YamlSerializer.Serialize(v), written);
        CheckImmutables(YamlSerializer.Deserialize(written, ctx.Immutables)!);
    }

    [Fact]
    public void An_uninitialised_immutable_array_is_written_as_an_empty_sequence()
    {
        var ctx = new ApiShapeContext();

        Assert.Equal("array: []\n", YamlSerializer.Serialize(new Immutables()).Split('\n')[0] + "\n");
        Assert.Equal("array: []\n", YamlSerializer.Serialize(new Immutables(), ctx.Immutables).Split('\n')[0] + "\n");
        Assert.True(YamlSerializer.Deserialize<ImmutableArray<int>>("[]").IsEmpty);
    }

    [Fact]
    public void Immutable_collections_work_at_the_root_and_as_values()
    {
        Assert.Equal([1, 2], YamlSerializer.Deserialize<ImmutableArray<int>>("[1,2]"));
        Assert.Equal("- 1\n- 2\n", YamlSerializer.Serialize(ImmutableArray.Create(1, 2)));
        Assert.Equal(1, YamlSerializer.Deserialize<ImmutableDictionary<string, ImmutableArray<int>>>("a: [1]")!["a"][0]);
        Assert.Equal(2, YamlSerializer.Deserialize<IImmutableList<int>>("[1, 2]")!.Count);
    }

    [Fact]
    public void A_type_that_cannot_be_described_says_which_type_and_member()
    {
        NotSupportedException onType = Assert.Throws<NotSupportedException>(() => YamlSerializer.Deserialize<StructBag>("[1]"));
        NotSupportedException onMember = Assert.Throws<NotSupportedException>(() => YamlSerializer.Deserialize<HoldsStructBag>("bag: [1]"));

        Assert.Contains(nameof(StructBag), onType.Message, StringComparison.Ordinal);
        Assert.Contains("HoldsStructBag.Bag", onMember.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("MakeGeneralCollection", onType.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("MakeGeneralCollection", onMember.Message, StringComparison.Ordinal);
    }

    // ---- several documents ----

    private static readonly Endpoint[] Docs =
    [
        new() { Path = "/a", Weight = 1 },
        new() { Path = "/b", Weight = 2 },
        new() { Path = "/c", Weight = 3 },
    ];

    private const string DocsYaml = "path: /a\nweight: 1\n---\npath: /b\nweight: 2\n---\npath: /c\nweight: 3\n";

    [Fact]
    public void Several_values_are_written_as_documents_of_one_stream()
    {
        Assert.Equal(DocsYaml, YamlSerializer.SerializeAll(Docs));
        Assert.Equal(DocsYaml, Encoding.UTF8.GetString(YamlSerializer.SerializeAllToUtf8Bytes(Docs)));
        Assert.Equal(string.Empty, YamlSerializer.SerializeAll(Array.Empty<Endpoint>()));

        var writer = new ArrayBufferWriter<byte>();
        YamlSerializer.SerializeAll(writer, Docs);
        Assert.Equal(DocsYaml, Encoding.UTF8.GetString(writer.WrittenSpan));

        using var stream = new MemoryStream();
        YamlSerializer.SerializeAll(stream, Docs);
        Assert.Equal(DocsYaml, Encoding.UTF8.GetString(stream.ToArray()));

        var text = new StringWriter();
        YamlSerializer.SerializeAll(text, Docs);
        Assert.Equal(DocsYaml, text.ToString());
    }

    [Fact]
    public async Task Several_values_are_written_to_a_stream_asynchronously()
    {
        using var sync = new MemoryStream();
        await YamlSerializer.SerializeAllAsync(sync, Docs, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(DocsYaml, Encoding.UTF8.GetString(sync.ToArray()));

        using var streamed = new MemoryStream();
        await YamlSerializer.SerializeAllAsync(streamed, ToAsync(Docs), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(DocsYaml, Encoding.UTF8.GetString(streamed.ToArray()));
    }

    private static async IAsyncEnumerable<Endpoint> ToAsync(IEnumerable<Endpoint> items)
    {
        foreach (Endpoint item in items)
        {
            await Task.Yield();
            yield return item;
        }
    }

    [Fact]
    public void The_generated_path_writes_several_documents_too()
    {
        var ctx = new ApiShapeContext();

        Assert.Equal(DocsYaml, YamlSerializer.SerializeAll(Docs, ctx.Endpoint));
        Assert.Equal(3, YamlSerializer.DeserializeAll(DocsYaml, ctx.Endpoint).Count);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(DocsYaml));
        Assert.Equal(3, YamlSerializer.DeserializeAll(stream, ctx.Endpoint).Count);
        Assert.Equal(3, YamlSerializer.DeserializeAll(new StringReader(DocsYaml), ctx.Endpoint).Count);
    }

    [Fact]
    public void Several_documents_are_read_from_a_stream_and_a_reader()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(DocsYaml));
        IReadOnlyList<Endpoint?> fromStream = YamlSerializer.DeserializeAll<Endpoint>(stream);
        IReadOnlyList<Endpoint?> fromReader = YamlSerializer.DeserializeAll<Endpoint>(new StringReader(DocsYaml));

        Assert.Equal(["/a", "/b", "/c"], fromStream.Select(e => e!.Path));
        Assert.Equal([1, 2, 3], fromReader.Select(e => e!.Weight));
    }

    [Fact]
    public async Task Several_documents_are_read_from_a_stream_asynchronously()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(DocsYaml));
        var paths = new List<string>();

        await foreach (Endpoint? e in YamlSerializer.DeserializeAllAsync<Endpoint>(stream, cancellationToken: TestContext.Current.CancellationToken))
        {
            paths.Add(e!.Path);
        }

        Assert.Equal(["/a", "/b", "/c"], paths);

        using var bad = new MemoryStream("path: /a\n---\nweight: x\n"u8.ToArray());
        await Assert.ThrowsAsync<YamlException>(async () =>
        {
            await foreach (Endpoint? e in YamlSerializer.DeserializeAllAsync<Endpoint>(bad, new YamlSerializerOptions { SourceName = "docs.yaml" }, TestContext.Current.CancellationToken))
            {
                Assert.NotNull(e);
            }
        });
    }

    // ---- scanner limit and frame stack ----

    [Fact]
    public void The_pending_token_limit_counts_every_closer_of_a_run()
    {
        // Each item is six tokens while a key is pending, whether the closers share a queue slot or not.
        static void ReadAll(int n)
        {
            byte[] text = Encoding.UTF8.GetBytes("{ [ " + string.Concat(Enumerable.Repeat("[[x]], ", n)) + "] : v }");
            var reader = new Parsing.YamlReader(text);
            try
            {
                while (reader.Read())
                {
                }
            }
            finally
            {
                reader.Dispose();
            }
        }

        ReadAll(100_000);
        YamlException at190 = Fail(() => ReadAll(190_000));
        YamlException at200 = Fail(() => ReadAll(200_000));

        Assert.Equal(YamlErrorCategory.Limit, at190.Category);
        Assert.Equal(YamlErrorCategory.Limit, at200.Category);
    }

    [Fact]
    public void A_deep_load_takes_no_heap_for_its_frame_directory_once_the_pool_is_warm()
    {
        const int Levels = 100;

        static void Run(Span<GenericFrame> first, int levels)
        {
            var stack = new FrameStack(first);
            try
            {
                for (int i = 0; i < levels; i++)
                {
                    stack.Push();
                }

                for (int i = 0; i < levels; i++)
                {
                    stack.Pop();
                }
            }
            finally
            {
                stack.Release();
            }
        }

        // The shared array pool drops what it holds when a collection runs, which other tests on a busy machine
        // can cause at any moment; the next rent then allocates once. Rounds that allocate are run again.
        SteadyState.AssertAllocatesNothing(
            () =>
            {
                var inline = default(InlineFrames);
                Run(inline, Levels);
            },
            200);
    }

    // ---- package ----

    [Fact]
    public void The_library_version_is_a_release_version()
    {
        string? version = typeof(YamlSerializer).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .Cast<System.Reflection.AssemblyInformationalVersionAttribute>()
            .Select(a => a.InformationalVersion)
            .FirstOrDefault();

        Assert.NotNull(version);
        // The informational version may carry build metadata after '+', never a prerelease suffix.
        string core = version!.Split('+')[0];
        Assert.Matches(@"^[1-9][0-9]*\.[0-9]+\.[0-9]+$", core);
    }

    // The metadata plumbing lives apart from the types a user works with.
    [Fact]
    public void The_generator_plumbing_is_in_its_own_namespace()
    {
        Assert.Equal("OneYaml.Serialization.Metadata", typeof(YamlMetadataServices).Namespace);
        Assert.Equal("OneYaml.Serialization.Metadata", typeof(YamlObjectInfoValues<>).Namespace);
        Assert.Equal("OneYaml.Serialization.Metadata", typeof(YamlPropertyInfoValues<,>).Namespace);
        Assert.Equal("OneYaml.Serialization.Metadata", typeof(YamlParameterInfo).Namespace);
        Assert.Equal("OneYaml.Serialization.Metadata", typeof(YamlRefSetter<,>).Namespace);
        Assert.Equal("OneYaml.Serialization.Metadata", typeof(YamlPropertyInfo<>).Namespace);
        Assert.Equal("OneYaml", typeof(YamlTypeInfo<>).Namespace);
    }
}
