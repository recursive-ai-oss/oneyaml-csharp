using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using Xunit;

// The inputs name their target as a Type, so the non-generic overloads are the point of these tests.
#pragma warning disable CA2263

namespace OneYaml.Tests.Generated;

/// <summary>
/// Reads the same input through the reflection resolver and through a generated context and requires the
/// same result: the whole value, or the same error with the same category, position and message.
/// </summary>
public class EquivalenceTests
{
    private const string AllShapes = """
        array: [1, 2, 3]
        roots: [a, b]
        set: [3, 1, 2]
        map: {x: 1, y: 2}
        byNumber: {1: one, 2: two}
        sorted: {b: true, a: false}
        doubles: [1.5, .inf, -0.0]
        """;

    private const string AllScalars = """
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
        """;

    public static TheoryData<Type, string> Inputs()
    {
        var data = new TheoryData<Type, string>();
        void Add(Type type, params string[] inputs)
        {
            foreach (string yaml in inputs)
            {
                data.Add(type, yaml);
            }
        }

        Add(typeof(Person), "name: Ada\nage: 36\nnickname: ~\ntags: [a, b]\nextra: ignored\n", "Name: Ada", "name: Ada\nage: 1\nname: b\n", "name: Ada\nage: old\n", "", "~", "5", "[a]", "name: [x]\n", "tags: {a: b}\n", "tags: [[x]]\n", "name: &n Ada\nnickname: *n\n", "name: !!str 5\n", "{name: Ada, age: 3}", "? [a]\n: b\n");
        Add(typeof(Names), "runs-on: ubuntu\npull_request: x\nhidden: no\nalsoHidden: no\nplainName: p\n", "runsOn: nope\n");
        Add(typeof(Required), "id: a\nkind: k", "kind: k", "id: a", "id: a\nkind: k\noptional: 3", "{}");
        Add(typeof(Point), "x: 1\ny: 2", "x: 1", "{}", "x: one", "x: 1\nx: 2");
        Add(typeof(Mixed), "name: n\nextra: 4\nnote: hi\nother: 1", "extra: 4", "name: [n]");
        Add(typeof(TwoConstructors), "source: s\nextra: 2", "extra: 1");
        Add(typeof(JsonMarked), "source: j", "");
        Add(typeof(Pair), "left: 3\nright: 4", "left: x", "right: 4");
        Add(typeof(Node), "name: r\nchildren:\n  - name: a\n  - name: b\n    children: [{name: c}]\n", "children: [1]", "children: {a: b}");
        Add(typeof(Holder), "value: 5\nbag: {a: 1, b: [x, {y: z}], c: null, d: 2.5, e: true}", "value: [1, 2]", "bag: [1]", "value: !!str 1");
        Add(typeof(Shapes), AllShapes, "array: x", "set: [1, 1]", "sorted: [a]", "byNumber: {a: b}", "map: {x: y}");
        Add(typeof(Scalars), AllScalars, "color: purple", "perm: Read, Bogus", "maybe: ~", "maybe: x", "color: 1", "bytes: '***'", "id: nope", "when: yesterday", "link: 'not a uri at all ::'", "ch: xy", "span: 1:2:3:4:5");
        foreach (string bad in new[] { "s8: 128", "u8: -1", "s16: 32768", "u16: 65536", "s32: 2147483648", "u32: 4294967296", "s64: 9223372036854775808", "u64: 18446744073709551616", "u64: -1", "s32: '5'", "s32: five", "s32: 1.5", "s32: [1]", "flag: yes", "flag: 'true'", "dbl: abc", "u8: ~", "s32: null", "s32:" })
        {
            Add(typeof(Scalars), "flag: true\n" + bad);
        }

        Add(typeof(InitOnly), "kind: k\ncount: 3\ntags: [a]\nnote: n", "count: 3", "kind: k", "kind: [k]");
        Add(typeof(InitStruct), "name: n\nsize: 2", "size: x", "");
        Add(typeof(Coord), "x: 1\ny: 2", "x: 1", "y: z");
        Add(typeof(Defaults), "name: n", "name: n\ntone: red\nratio: 2\nbig: 9\nletter: z\non: false\nopt: 4\nid: 4f2a8a1e-6b9d-4a39-9b5c-0d1e2f3a4b5c\nsmall: 7\nlabel: ~", "tone: Blue", "name: n\ntone: nope");
        Add(typeof(Animal), "name: a\nlegs: 3\nshadowed: s\nweight: 9");
        Add(typeof(Dog), "name: rex\ngood-boy: true\nshadowed: d\nlegs: 3\nweight: 2\nsecret: 5\nkept: k", "goodBoy: true");
        Add(typeof(Page<Person>), "number: 2\nitems:\n  - name: a\n  - name: b\n    age: 3\n", "items: [1]");
        Add(typeof(Ordered), "first: 1\nsecond: 2\nreadOnlyValue: 5\nwriteOnly: 4\ncaptured: 8");
        Add(typeof(Interfaces), "numbers: [1, 2]\nnames: [a, b, a]\nunique: [1, 2]\nitems: [x]\npeople: [{name: p}]\nwords: [w1, w2]\ncounts: {a: 1}\ngroups: {g: [{name: q}]}\ncolors: [red, blue, 2]\nmaybePerm: Read\nmaybeCoord: {x: 1, y: 2}\ngrid: [[1], [2, 3]]\nbyColor: {red: 1, darkGreen: 2}", "maybePerm: ~\nmaybeCoord: ~", "numbers: x", "grid: [1]", "byColor: {nope: 1}");
        Add(typeof(PublicModel), "title: t\nchildren: [{title: c, children: []}]\nbyName: {a: {title: x}, b: ~}");
        Add(typeof(Nested), "value: {depth: 4}", "value: 4");
        Add(typeof(int), "5", "0x1f", "'5'", "", "x", "!!int '5'", "!custom 5");
        Add(typeof(string), "x", "~", "!!str 5", "[a]", "");
        Add(typeof(object), "a: 1\nb: [x, {y: z}]\nc: &a [1]\nd: *a\n", "[1, 2.5, true, null, ~, '1']", "5", "");
        Add(typeof(List<Person>), "- name: a\n- name: b\n", "[]", "x");
        Add(typeof(Dictionary<string, object>), "a: 1\nb: [x]\n", "a: 1\na: 2\n", "[1]");
        Add(typeof(Color), "darkGreen", "Blue", "9", "nope");
        Add(typeof(Perm), "Read, Write", "3");
        return data;
    }

    private static YamlSerializerOptions Options(int variant) => variant switch
    {
        1 => new YamlSerializerOptions { PropertyNamingPolicy = null },
        2 => new YamlSerializerOptions { PropertyNameCaseInsensitive = true },
        3 => new YamlSerializerOptions { UnmappedMemberHandling = YamlUnmappedMemberHandling.Disallow },
        4 => new YamlSerializerOptions { MergeKeys = true },
        5 => new YamlSerializerOptions { Limits = YamlLimits.Untrusted },
        _ => new YamlSerializerOptions(),
    };

    [Theory]
    [MemberData(nameof(Inputs))]
    public void Generated_and_reflection_metadata_read_the_same_value(Type type, string yaml)
    {
        for (int variant = 0; variant <= 5; variant++)
        {
            string reflected = Run(() => YamlSerializer.Deserialize(yaml, type, Options(variant)));
            var context = new ModelsContext(Options(variant));
            string generated = Run(() => YamlSerializer.Deserialize(yaml, context.GetTypeInfo(type)!));

            Assert.True(reflected == generated, $"{type.Name} variant {variant}\ninput: {yaml}\nreflection: {reflected}\ngenerated:  {generated}");
        }
    }

    [Theory]
    [MemberData(nameof(Inputs))]
    public void Generated_and_reflection_metadata_write_the_same_text_for_what_they_read(Type type, string yaml)
    {
        for (int variant = 0; variant <= 5; variant++)
        {
            object? value;
            try
            {
                value = YamlSerializer.Deserialize(yaml, type, Options(variant));
            }
            catch (YamlException)
            {
                // The input does not bind; the read tests above cover how it fails.
                return;
            }

            var context = new ModelsContext(Options(variant));
            string reflected = YamlSerializer.Serialize(value, type, Options(variant));
            string generated = YamlSerializer.Serialize(value, context.GetTypeInfo(type)!);

            Assert.True(reflected == generated, $"{type.Name} variant {variant}\ninput: {yaml}\nreflection:\n{reflected}\ngenerated:\n{generated}");
        }
    }

    [Fact]
    public void Merge_keys_read_the_same_through_both()
    {
        const string yaml = "name: base\nage: 1\n<<: {age: 9, nickname: n}\n";
        var options = new YamlSerializerOptions { MergeKeys = true };
        var context = new ModelsContext(options);

        string reflected = Run(() => YamlSerializer.Deserialize(yaml, typeof(Person), options));
        string generated = Run(() => YamlSerializer.Deserialize(yaml, context.Person));

        Assert.StartsWith("ok", reflected, StringComparison.Ordinal);
        Assert.Equal(reflected, generated);
    }

    [Fact]
    public void Documents_with_many_nested_levels_end_in_the_same_limit_error()
    {
        string deep = string.Concat(Enumerable.Repeat("{children: [", 5000)) + string.Concat(Enumerable.Repeat("]}", 5000));
        var options = new YamlSerializerOptions { Limits = YamlLimits.None };
        var context = new ModelsContext(options);

        string reflected = OnSmallStack(() => YamlSerializer.Deserialize(deep, typeof(Node), options));
        string generated = OnSmallStack(() => YamlSerializer.Deserialize(deep, context.Node));

        // The depth at which the stack guard fires depends on the stack left, which differs by a few frames between
        // the two paths, so only the error kind is compared here.
        Assert.StartsWith("error Limit", reflected, StringComparison.Ordinal);
        Assert.StartsWith("error Limit", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void Generic_load_of_object_is_the_same_through_a_context()
    {
        var context = new ModelsContext();
        foreach (string yaml in new[] { "a: 1\nb: [1, 2.5, true, ~, x]\n", "- &a [1]\n- *a\n", "--- 1\n--- two\n", "a: 1\na: 2\n", "? [a]\n: b\n" })
        {
            string reflected = Run(() => YamlSerializer.DeserializeAll<object>(yaml));
            string generated = Run(() => YamlSerializer.DeserializeAll(yaml, context.Object));
            Assert.Equal(reflected, generated);
        }
    }

    [Fact]
    public void Suite_and_kit_inputs_read_the_same_through_a_context()
    {
        string? suite = Corpus.SuiteDirectory;
        if (suite is null)
        {
            Assert.Skip($"{Corpus.SuiteVariable} is not set");
        }

        var context = new ModelsContext();
        int compared = 0;
        foreach (string id in Corpus.SuiteCaseIds())
        {
            string file = Path.Combine(suite, id.Replace('/', Path.DirectorySeparatorChar), "in.yaml");
            byte[] input = File.ReadAllBytes(file);
            string reflected = Run(() => YamlSerializer.DeserializeAll<object>(input));
            string generated = Run(() => YamlSerializer.DeserializeAll<object>(input, context.Options));
            Assert.True(reflected == generated, $"{id}\nreflection: {reflected}\ngenerated: {generated}");
            compared++;
        }

        string? kit = Corpus.KitDirectory;
        if (kit is not null)
        {
            foreach (string file in Directory.EnumerateFiles(Path.Combine(kit, "suites", "own", "cases"), "*.y*ml", SearchOption.AllDirectories))
            {
                byte[] input = File.ReadAllBytes(file);
                string reflected = Run(() => YamlSerializer.DeserializeAll<object>(input));
                string generated = Run(() => YamlSerializer.DeserializeAll<object>(input, context.Options));
                Assert.True(reflected == generated, $"{file}\nreflection: {reflected}\ngenerated: {generated}");
                compared++;
            }
        }

        Assert.True(compared > 100, $"only {compared} inputs were compared");
    }

    [Fact]
    public void A_custom_converter_takes_precedence_over_generated_metadata()
    {
        var options = new YamlSerializerOptions { Converters = { new ShoutConverter() } };
        var context = new ModelsContext(options);

        Person? p = YamlSerializer.Deserialize("name: ada\nnickname: ~\n", context.Person);

        Assert.Equal("ADA", p!.Name);
        Assert.Null(p.Nickname);
    }

    [Fact]
    public void Init_only_and_required_members_are_assigned_without_reflection_support()
    {
        var context = new ModelsContext();

        InitOnly? value = YamlSerializer.Deserialize("kind: k\ncount: 3\ntags: [a, b]\n", context.InitOnly);
        InitStruct s = YamlSerializer.Deserialize("name: n\nsize: 2", context.InitStruct);

        Assert.Equal(("k", 3, 2), (value!.Kind, value.Count, value.Tags.Count));
        Assert.Equal(("n", 2), (s.Name, s.Size));
    }

    [Fact]
    public void Types_outside_the_context_are_not_found_and_reflection_is_not_used_to_find_them()
    {
        var context = new ModelsContext();

        Assert.Null(context.GetTypeInfo(typeof(Guid[])));
        Assert.Throws<NotSupportedException>(() => context.Options.GetTypeInfo(typeof(HashSet<Guid>)));
    }

    [Fact]
    public void The_default_context_and_member_metadata_are_available()
    {
        YamlTypeInfo<Person> info = ModelsContext.Default.Person;

        Assert.Same(info, ModelsContext.Default.GetTypeInfo(typeof(Person)));
        Assert.Equal(["Name", "Age", "Nickname", "Tags"], info.Properties.Select(p => p.ClrName));
        Assert.Equal(["name", "age", "nickname", "tags"], info.Properties.Select(p => p.Name));
    }

    private sealed class ShoutConverter : YamlConverter<string>
    {
        public override string? Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options) =>
            reader.GetString().ToUpperInvariant();

        public override void Write(Utf8YamlEmitter writer, string value, YamlSerializerOptions options) =>
            writer.WriteStringValue(value.ToUpperInvariant());
    }

    // ---- Running and describing ----

    private static string OnSmallStack(Func<object?> read)
    {
        string result = string.Empty;
        var thread = new Thread(() => result = Run(read), 1024 * 1024);
        thread.Start();
        thread.Join();
        return result;
    }

    private static string Run(Func<object?> read)
    {
        try
        {
            return "ok " + Dump(read());
        }
        catch (YamlException ex)
        {
            return $"error {ex.Category} line {ex.Line} column {ex.Column} offset {ex.Offset}: {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"other {ex.GetType().Name}: {ex.Message}";
        }
    }

    private static string Dump(object? value)
    {
        var sb = new StringBuilder();
        Write(sb, value, 0);
        return sb.ToString();
    }

    // A structural text of a value: the type, then every public property and field in name order.
    private static void Write(StringBuilder sb, object? value, int depth)
    {
        if (depth > 40)
        {
            sb.Append("...");
            return;
        }

        switch (value)
        {
            case null:
                sb.Append("null");
                return;
            case string s:
                sb.Append('"').Append(s).Append('"');
                return;
            case byte[] bytes:
                sb.Append("bytes:").Append(Convert.ToHexString(bytes));
                return;
            case IFormattable f when value.GetType().IsPrimitive || value is decimal or DateTime or DateTimeOffset or Guid or TimeSpan or DateOnly or TimeOnly or System.Numerics.BigInteger:
                sb.Append(value.GetType().Name).Append(':').Append(f.ToString(null, CultureInfo.InvariantCulture));
                return;
            case Enum e:
                sb.Append(e.GetType().Name).Append(':').Append(Convert.ToInt64(e, CultureInfo.InvariantCulture));
                return;
            case Uri uri:
                sb.Append("uri:").Append(uri.OriginalString);
                return;
            case IDictionary dictionary:
                sb.Append(value.GetType().Name).Append('{');
                var entries = new List<string>();
                foreach (DictionaryEntry entry in dictionary)
                {
                    var inner = new StringBuilder();
                    Write(inner, entry.Key, depth + 1);
                    inner.Append('=');
                    Write(inner, entry.Value, depth + 1);
                    entries.Add(inner.ToString());
                }

                sb.Append(string.Join(",", entries)).Append('}');
                return;
            case IEnumerable sequence:
                sb.Append(value.GetType().Name).Append('[');
                bool first = true;
                foreach (object? item in sequence)
                {
                    if (!first)
                    {
                        sb.Append(',');
                    }

                    first = false;
                    Write(sb, item, depth + 1);
                }

                sb.Append(']');
                return;
            default:
                break;
        }

        Type type = value.GetType();
        sb.Append(type.Name).Append('(');
        var members = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (PropertyInfo p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length == 0 && p.GetMethod is not null)
            {
                members[p.Name] = p.GetValue(value);
            }
        }

        foreach (FieldInfo f in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            members[f.Name] = f.GetValue(value);
        }

        bool firstMember = true;
        foreach (KeyValuePair<string, object?> member in members)
        {
            if (!firstMember)
            {
                sb.Append(';');
            }

            firstMember = false;
            sb.Append(member.Key).Append('=');
            Write(sb, member.Value, depth + 1);
        }

        sb.Append(')');
    }
}
