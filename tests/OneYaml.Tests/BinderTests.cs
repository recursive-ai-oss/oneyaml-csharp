using System.Numerics;
using System.Text.Json.Serialization;
using Xunit;

namespace OneYaml.Tests;

internal sealed record Config(int Replicas, string Script);

internal sealed class Person
{
    public string Name { get; set; } = "";

    public int Age { get; set; }

    public string? Nickname { get; set; }

    public List<string> Tags { get; set; } = [];
}

internal sealed class Names
{
    [YamlPropertyName("runs-on")]
    public string RunsOn { get; set; } = "";

    [JsonPropertyName("pull_request")]
    public string? PullRequest { get; set; }

    [YamlIgnore]
    public string Hidden { get; set; } = "kept";

    [JsonIgnore]
    public string AlsoHidden { get; set; } = "kept";

    public string PlainName { get; set; } = "";
}

internal sealed class Required
{
    [YamlRequired]
    public string Id { get; set; } = "";

    public required string Kind { get; init; }

    public int Optional { get; set; }
}

internal sealed record Point(int X, int Y = 7);

internal sealed record Mixed(string Name)
{
    public int Extra { get; init; }

    public string? Note { get; set; }
}

internal sealed class TwoConstructors
{
    public TwoConstructors()
    {
        Source = "default";
    }

    [YamlConstructor]
    public TwoConstructors(string source)
    {
        Source = source;
    }

    public string Source { get; }

    public int Extra { get; set; }
}

internal sealed class JsonMarked
{
    public JsonMarked()
    {
        Source = "default";
    }

    [JsonConstructor]
    public JsonMarked(string source)
    {
        Source = source;
    }

    public string Source { get; }
}

internal struct Pair
{
    public int Left { get; set; }

    public int Right;

    public static Pair Of(int left, int right) => new() { Left = left, Right = right };
}

internal sealed class Node
{
    public string Name { get; set; } = "";

    public List<Node> Children { get; set; } = [];
}

internal sealed class Holder
{
    public object? Value { get; set; }

    public Dictionary<string, object?> Bag { get; set; } = [];
}

internal enum Color
{
    Red,
    DarkGreen,
    Blue,
}

[Flags]
internal enum Perm
{
    None = 0,
    Read = 1,
    Write = 2,
}

internal sealed class Shapes
{
    public int[] Array { get; set; } = [];

    public IReadOnlyList<string> Roots { get; set; } = [];

    public HashSet<int> Set { get; set; } = [];

    public IDictionary<string, int> Map { get; set; } = new Dictionary<string, int>();

    public IReadOnlyDictionary<int, string> ByNumber { get; set; } = new Dictionary<int, string>();

    public SortedDictionary<string, bool> Sorted { get; set; } = [];

    public ICollection<double> Doubles { get; set; } = [];
}

internal sealed class Scalars
{
    public sbyte S8 { get; set; }

    public byte U8 { get; set; }

    public short S16 { get; set; }

    public ushort U16 { get; set; }

    public int S32 { get; set; }

    public uint U32 { get; set; }

    public long S64 { get; set; }

    public ulong U64 { get; set; }

    public BigInteger Big { get; set; }

    public decimal Dec { get; set; }

    public double Dbl { get; set; }

    public float Flt { get; set; }

    public bool Flag { get; set; }

    public char Ch { get; set; }

    public Guid Id { get; set; }

    public DateTime When { get; set; }

    public DateTimeOffset At { get; set; }

    public DateOnly Day { get; set; }

    public TimeOnly Clock { get; set; }

    public TimeSpan Span { get; set; }

    public Uri? Link { get; set; }

    public byte[]? Bytes { get; set; }

    public Color Color { get; set; }

    public int? Maybe { get; set; }

    public Perm Perm { get; set; }
}

public class BinderTests
{
    private static T Load<T>(string yaml, YamlSerializerOptions? options = null) => YamlSerializer.Deserialize<T>(yaml, options)!;

    private static YamlException Fails<T>(string yaml, YamlSerializerOptions? options = null) =>
        Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<T>(yaml, options));

    [Fact]
    public void Quick_start_typed_load()
    {
        // The typed-load line of the quick start in the README.
        string src = """
            # Build settings
            script: |
              make build
            replicas: 2      # the CI job scales this
            """;

        var cfg = YamlSerializer.Deserialize<Config>(src);

        Assert.Equal(new Config(2, "make build\n"), cfg);
    }

    [Fact]
    public void Properties_bind_by_camel_case_name_and_ignore_unmapped_keys()
    {
        var p = Load<Person>("name: Ada\nage: 36\nnickname: ~\ntags: [a, b]\nextra: ignored\n");

        Assert.Equal("Ada", p.Name);
        Assert.Equal(36, p.Age);
        Assert.Null(p.Nickname);
        Assert.Equal(["a", "b"], p.Tags);
    }

    [Fact]
    public void Names_follow_the_naming_policy_case_option_and_attributes()
    {
        Assert.Equal("Ada", Load<Person>("Name: Ada", new YamlSerializerOptions { PropertyNamingPolicy = null }).Name);
        Assert.Equal("", Load<Person>("Name: Ada").Name);
        Assert.Equal("Ada", Load<Person>("NAME: Ada", new YamlSerializerOptions { PropertyNameCaseInsensitive = true }).Name);

        var n = Load<Names>("runs-on: ubuntu\npull_request: x\nhidden: no\nalsoHidden: no\nplainName: p\n");
        Assert.Equal("ubuntu", n.RunsOn);
        Assert.Equal("x", n.PullRequest);
        Assert.Equal("kept", n.Hidden);
        Assert.Equal("kept", n.AlsoHidden);
        Assert.Equal("p", n.PlainName);
    }

    [Fact]
    public void Unmapped_keys_can_be_refused_with_their_position()
    {
        YamlException ex = Fails<Person>("name: Ada\nbogus: 1\n", new YamlSerializerOptions { UnmappedMemberHandling = YamlUnmappedMemberHandling.Disallow });

        Assert.Equal(YamlErrorCategory.Binding, ex.Category);
        Assert.Equal(2, ex.Line);
        Assert.Equal(1, ex.Column);
    }

    [Fact]
    public void Required_members_must_be_present()
    {
        Assert.Equal("k", Load<Required>("id: a\nkind: k").Kind);

        YamlException missingAttr = Fails<Required>("kind: k");
        YamlException missingKeyword = Fails<Required>("id: a");

        Assert.Equal(YamlErrorCategory.Binding, missingAttr.Category);
        Assert.Contains("id", missingAttr.Message, StringComparison.Ordinal);
        Assert.Contains("kind", missingKeyword.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_member_keys_are_an_error_at_the_second_key()
    {
        YamlException ex = Fails<Person>("name: a\nage: 1\nname: b\n");

        Assert.Equal(YamlErrorCategory.DuplicateKey, ex.Category);
        Assert.Equal(3, ex.Line);
    }

    [Fact]
    public void Records_and_constructor_parameters_bind_with_defaults_and_extra_members()
    {
        Assert.Equal(new Point(1, 2), Load<Point>("x: 1\ny: 2"));
        Assert.Equal(new Point(1, 7), Load<Point>("x: 1"));
        Assert.Equal(new Point(0, 7), Load<Point>("{}"));

        var m = Load<Mixed>("name: n\nextra: 4\nnote: hi\nother: 1");
        Assert.Equal("n", m.Name);
        Assert.Equal(4, m.Extra);
        Assert.Equal("hi", m.Note);
    }

    [Fact]
    public void A_marked_constructor_is_the_one_used()
    {
        var t = Load<TwoConstructors>("source: s\nextra: 2");

        Assert.Equal(("s", 2), (t.Source, t.Extra));
        Assert.Equal("j", Load<JsonMarked>("source: j").Source);
        Assert.Null(Load<TwoConstructors>("extra: 1").Source);
    }

    [Fact]
    public void Structs_bind_properties_and_fields()
    {
        var p = Load<Pair>("left: 3\nright: 4");

        Assert.Equal(Pair.Of(3, 4), p);
    }

    [Fact]
    public void Recursive_types_bind()
    {
        var root = Load<Node>("name: r\nchildren:\n  - name: a\n  - name: b\n    children: [{name: c}]\n");

        Assert.Equal("r", root.Name);
        Assert.Equal(["a", "b"], root.Children.Select(c => c.Name));
        Assert.Equal("c", root.Children[1].Children[0].Name);
    }

    [Fact]
    public void Deep_input_for_a_recursive_type_ends_in_a_limit_error_not_a_crash()
    {
        string deep = string.Concat(Enumerable.Repeat("{children: [", 5000)) + string.Concat(Enumerable.Repeat("]}", 5000));
        var options = new YamlSerializerOptions { Limits = YamlLimits.None };

        Exception? caught = null;
        var t = new Thread(
            () =>
            {
                try
                {
                    YamlSerializer.Deserialize<Node>(deep, options);
                }
                catch (Exception e)
                {
                    caught = e;
                }
            },
            1024 * 1024);
        t.Start();
        t.Join();

        YamlException ex = Assert.IsType<YamlException>(caught);
        Assert.Equal(YamlErrorCategory.Limit, ex.Category);
    }

    [Fact]
    public void Scalars_bind_to_their_types()
    {
        var s = Load<Scalars>("""
            s8: -128
            u8: 255
            s16: -32768
            u16: 65535
            s32: 0x7fffffff
            u32: 0o37777777777
            s64: -9223372036854775808
            u64: 18446744073709551615
            big: 123456789012345678901234567890
            dec: 12.5
            dbl: .inf
            flt: 1.5e2
            flag: true
            ch: x
            id: 4f2a8a1e-6b9d-4a39-9b5c-0d1e2f3a4b5c
            when: 2001-12-14T21:59:43Z
            at: 2001-12-14 21:59:43 +05:00
            day: 2001-12-14
            clock: "21:59:43"
            span: 1.02:03:04
            link: https://example.com/x
            bytes: aGVsbG8=
            color: darkGreen
            maybe: 5
            perm: Read, Write
            """, new YamlSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });

        Assert.Equal(-128, s.S8);
        Assert.Equal(255, s.U8);
        Assert.Equal(-32768, s.S16);
        Assert.Equal(65535, s.U16);
        Assert.Equal(int.MaxValue, s.S32);
        Assert.Equal(uint.MaxValue, s.U32);
        Assert.Equal(long.MinValue, s.S64);
        Assert.Equal(ulong.MaxValue, s.U64);
        Assert.Equal(BigInteger.Parse("123456789012345678901234567890", System.Globalization.CultureInfo.InvariantCulture), s.Big);
        Assert.Equal(12.5m, s.Dec);
        Assert.True(double.IsPositiveInfinity(s.Dbl));
        Assert.Equal(150f, s.Flt);
        Assert.True(s.Flag);
        Assert.Equal('x', s.Ch);
        Assert.Equal(Guid.Parse("4f2a8a1e-6b9d-4a39-9b5c-0d1e2f3a4b5c"), s.Id);
        Assert.Equal(new DateTime(2001, 12, 14, 21, 59, 43, DateTimeKind.Utc), s.When);
        Assert.Equal(new DateTimeOffset(2001, 12, 14, 21, 59, 43, TimeSpan.FromHours(5)), s.At);
        Assert.Equal(new DateOnly(2001, 12, 14), s.Day);
        Assert.Equal(new TimeOnly(21, 59, 43), s.Clock);
        Assert.Equal(new TimeSpan(1, 2, 3, 4), s.Span);
        Assert.Equal(new Uri("https://example.com/x"), s.Link);
        Assert.Equal("hello"u8.ToArray(), s.Bytes);
        Assert.Equal(Color.DarkGreen, s.Color);
        Assert.Equal(5, s.Maybe);
        Assert.Equal(Perm.Read | Perm.Write, s.Perm);
    }

    [Theory]
    [InlineData("s8: 128")]
    [InlineData("u8: -1")]
    [InlineData("s16: 32768")]
    [InlineData("u16: 65536")]
    [InlineData("s32: 2147483648")]
    [InlineData("u32: 4294967296")]
    [InlineData("s64: 9223372036854775808")]
    [InlineData("u64: 18446744073709551616")]
    [InlineData("u64: -1")]
    public void Integer_overflow_is_a_binding_error_at_the_value(string yaml)
    {
        YamlException ex = Fails<Scalars>("flag: true\n" + yaml);

        Assert.Equal(YamlErrorCategory.Binding, ex.Category);
        Assert.Equal(2, ex.Line);
        Assert.Contains("range", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("s32: '5'")]
    [InlineData("s32: five")]
    [InlineData("s32: 1.5")]
    [InlineData("s32: [1]")]
    [InlineData("flag: yes")]
    [InlineData("flag: 'true'")]
    [InlineData("dbl: abc")]
    [InlineData("u8: ~")]
    [InlineData("s32: null")]
    [InlineData("s32:")]
    public void Values_of_the_wrong_kind_are_binding_errors(string yaml)
    {
        YamlException ex = Fails<Scalars>(yaml);

        Assert.Equal(YamlErrorCategory.Binding, ex.Category);
        Assert.Equal(1, ex.Line);
    }

    [Fact]
    public void Null_is_allowed_for_nullable_and_reference_types()
    {
        var s = Load<Scalars>("maybe: ~\nlink: null\nbytes:\n");
        Assert.Null(s.Maybe);
        Assert.Null(s.Link);
        Assert.Null(s.Bytes);
        Assert.Null(YamlSerializer.Deserialize<string>("~"));
        Assert.Null(YamlSerializer.Deserialize<Person>(""));
        Assert.Equal(YamlErrorCategory.Binding, Fails<int>("").Category);
    }

    [Fact]
    public void Explicit_tags_decide_the_type_and_unknown_tags_are_ignored()
    {
        Assert.Equal("5", Load<string>("!!str 5"));
        Assert.Equal(5, Load<int>("!!int '5'"));
        Assert.Equal(YamlErrorCategory.Binding, Fails<int>("!!str 5").Category);
        Assert.Equal(5, Load<int>("!custom 5"));
        Assert.Equal("x", Load<string>("!!python/object:os.system x"));
        Assert.Equal(1.0, Load<double>("!!float 1"));
        Assert.Equal("aGVsbG8=", Load<string>("!!binary aGVsbG8="));
        Assert.Equal("hello"u8.ToArray(), Load<byte[]>("!!binary |\n  aGVs\n  bG8=\n"));
    }

    [Fact]
    public void Enums_bind_by_name_by_policy_name_ignoring_case_and_by_number()
    {
        Assert.Equal(Color.Blue, Load<Color>("Blue"));
        Assert.Equal(Color.Blue, Load<Color>("blue"));
        Assert.Equal(Color.DarkGreen, Load<Color>("darkGreen"));
        Assert.Equal(Color.DarkGreen, Load<Color>("1"));
        Assert.Equal(YamlErrorCategory.Binding, Fails<Color>("purple").Category);
    }

    [Fact]
    public void Collections_bind_in_all_their_shapes()
    {
        var s = Load<Shapes>("""
            array: [1, 2, 3]
            roots: [a, b]
            set: [1, 2, 2]
            map: {x: 1, y: 2}
            byNumber: {1: one, 0x2: two}
            sorted: {b: true, a: false}
            doubles: [1, 2.5, .nan]
            """);

        Assert.Equal([1, 2, 3], s.Array);
        Assert.Equal(["a", "b"], s.Roots);
        Assert.Equal([1, 2], s.Set.Order());
        Assert.Equal(2, s.Map["y"]);
        Assert.Equal("two", s.ByNumber[2]);
        Assert.Equal(["a", "b"], s.Sorted.Keys);
        Assert.Equal(3, s.Doubles.Count);
    }

    [Fact]
    public void Dictionary_keys_equal_after_resolution_are_duplicates()
    {
        YamlException ex = Fails<Dictionary<int, string>>("1: a\n0x1: b\n");

        Assert.Equal(YamlErrorCategory.DuplicateKey, ex.Category);
        Assert.Equal(2, ex.Line);
    }

    [Fact]
    public void Object_members_take_the_generic_model()
    {
        var h = Load<Holder>("value: [1, {a: x}, 2.5, true, ~]\nbag: {k: 1, n: [1]}\n");

        var list = Assert.IsType<List<object?>>(h.Value);
        Assert.Equal(1L, list[0]);
        Assert.Equal("x", Assert.IsType<Dictionary<string, object?>>(list[1])["a"]);
        Assert.Equal(2.5, list[2]);
        Assert.Equal(true, list[3]);
        Assert.Null(list[4]);
        Assert.Equal(1L, h.Bag["k"]);
    }

    [Fact]
    public void Aliases_are_followed_in_typed_binding_and_count_against_the_budget()
    {
        var p = Load<List<Person>>("- &a {name: Ada, age: 36, tags: [x, y]}\n- *a\n- {<<: *a, name: Bob}\n", new YamlSerializerOptions { MergeKeys = true });

        Assert.Equal("Ada", p[1].Name);
        Assert.Equal(["x", "y"], p[1].Tags);
        Assert.Equal("Bob", p[2].Name);
        Assert.Equal(36, p[2].Age);

        string bomb = "a: &a [x,x,x,x,x,x,x,x,x]\nb: &b [*a,*a,*a,*a,*a,*a,*a,*a,*a]\nc: &c [*b,*b,*b,*b,*b,*b,*b,*b,*b]\nd: &d [*c,*c,*c,*c,*c,*c,*c,*c,*c]\ne: &e [*d,*d,*d,*d,*d,*d,*d,*d,*d]\nf: &f [*e,*e,*e,*e,*e,*e,*e,*e,*e]\ng: [*f,*f,*f,*f,*f,*f,*f,*f,*f]\n";
        YamlException ex = Fails<Dictionary<string, object?>>(bomb);
        Assert.Equal(YamlErrorCategory.Limit, ex.Category);
    }

    [Fact]
    public void An_undefined_alias_is_a_syntax_error()
    {
        Assert.Equal(YamlErrorCategory.Syntax, Fails<List<int>>("[*nope]").Category);
        Assert.Equal(YamlErrorCategory.Syntax, Fails<List<int>>("- *a\n- &a 1\n").Category);
    }

    [Fact]
    public void More_than_one_document_is_refused_by_deserialize_and_read_by_deserialize_all()
    {
        YamlException ex = Fails<int>("1\n---\n2\n");
        Assert.Equal(YamlErrorCategory.Syntax, ex.Category);
        Assert.Equal([1, 2], YamlSerializer.DeserializeAll<int>("1\n---\n2\n"));
    }

    [Fact]
    public void Merge_keys_are_ordinary_keys_unless_enabled()
    {
        var plain = Load<Dictionary<string, object?>>("a: &x {k: 1}\nb:\n  <<: *x\n  j: 2\n");
        var b = Assert.IsType<Dictionary<string, object?>>(plain["b"]);
        Assert.True(b.ContainsKey("<<"));

        var merged = Load<Dictionary<string, object?>>("a: &x {k: 1, j: 0}\nb:\n  <<: *x\n  j: 2\n", new YamlSerializerOptions { MergeKeys = true });
        var mb = Assert.IsType<Dictionary<string, object?>>(merged["b"]);
        Assert.Equal(1L, mb["k"]);
        Assert.Equal(2L, mb["j"]);
        Assert.False(mb.ContainsKey("<<"));
    }

    [Fact]
    public void All_input_forms_read_the_same_value()
    {
        const string yaml = "name: Ada\nage: 36\n";
        Type target = typeof(Person);
        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(yaml);
        byte[] utf16 = System.Text.Encoding.Unicode.GetPreamble().Concat(System.Text.Encoding.Unicode.GetBytes(yaml)).ToArray();

        Assert.Equal("Ada", YamlSerializer.Deserialize<Person>(yaml.AsSpan())!.Name);
        Assert.Equal("Ada", YamlSerializer.Deserialize<Person>(utf8.AsSpan())!.Name);
        Assert.Equal("Ada", YamlSerializer.Deserialize<Person>(utf16.AsSpan())!.Name);
        Assert.Equal("Ada", YamlSerializer.Deserialize<Person>(new MemoryStream(utf8))!.Name);
        Assert.Equal("Ada", YamlSerializer.Deserialize<Person>(new StringReader(yaml))!.Name);
        Assert.Equal("Ada", Assert.IsType<Person>(YamlSerializer.Deserialize(yaml, target)).Name);
    }

    [Fact]
    public async Task Streams_read_asynchronously()
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("name: Ada\n"));

        Person? p = await YamlSerializer.DeserializeAsync<Person>(stream, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("Ada", p!.Name);
    }

    [Fact]
    public void Arguments_are_checked()
    {
        Assert.Throws<ArgumentNullException>(() => YamlSerializer.Deserialize<int>((string)null!));
        Assert.Throws<ArgumentNullException>(() => YamlSerializer.Deserialize<int>((Stream)null!));
        Assert.Throws<ArgumentNullException>(() => YamlSerializer.Deserialize("x", (Type)null!));
    }

    [Fact]
    public void Options_cannot_change_converters_once_used()
    {
        var options = new YamlSerializerOptions();
        YamlSerializer.Deserialize<int>("1", options);

        Assert.Throws<InvalidOperationException>(() => options.Converters.Add(new ShoutConverter()));
    }

    private sealed class ShoutConverter : YamlConverter<string>
    {
        public override string? Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options) =>
            reader.GetString().ToUpperInvariant();

        public override void Write(Utf8YamlEmitter writer, string value, YamlSerializerOptions options) =>
            writer.WriteStringValue(value.ToUpperInvariant());
    }

    [Fact]
    public void Custom_converters_read_their_type_and_see_no_nulls()
    {
        var options = new YamlSerializerOptions { Converters = { new ShoutConverter() } };

        var p = YamlSerializer.Deserialize<Person>("name: ada\nnickname: ~\n", options)!;

        Assert.Equal("ADA", p.Name);
        Assert.Null(p.Nickname);
    }

    [Fact]
    public void Limits_apply_to_depth_scalars_and_input()
    {
        string deep = new string('[', 200) + new string(']', 200);
        Assert.Equal(YamlErrorCategory.Limit, Fails<object>(deep, new YamlSerializerOptions { Limits = YamlLimits.Untrusted }).Category);
        Assert.Equal(YamlErrorCategory.Limit, Fails<object>("abc", new YamlSerializerOptions { Limits = new YamlLimits { MaxInputBytes = 2 } }).Category);
    }
}
