using System.Buffers;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json.Serialization;
using OneYaml.Tests.Generated;
using Xunit;

namespace OneYaml.Tests.Writing;

internal sealed class Unconditional
{
    public string? Name { get; set; }

    public int Count { get; set; }

    [YamlIgnore(Condition = YamlIgnoreCondition.WhenWritingNull)]
    public string? Note { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Level { get; set; }

    [YamlIgnore(Condition = YamlIgnoreCondition.Never)]
    public string? Always { get; set; }
}

internal sealed class Wide
{
    public Dictionary<string, int> Map { get; set; } = [];
}

internal sealed class Cyclic
{
    public string Name { get; set; } = "";

    public Cyclic? Next { get; set; }

    public List<object?> Items { get; set; } = [];
}

/// <summary>Exact output of the serializer and the emitter, one rule at a time.</summary>
public class WriterTests
{
    private static string Dump<T>(T value, YamlSerializerOptions? options = null) => YamlSerializer.Serialize(value, options);

    // ---- the defaults: block style, two spaces, single quotes, literal blocks ----

    [Fact]
    public void A_mapping_is_block_style_with_two_space_indent_and_a_trailing_line_break()
    {
        var person = new Person { Name = "Ada", Age = 36, Nickname = "ada", Tags = ["x", "z"] };

        Assert.Equal("name: Ada\nage: 36\nnickname: ada\ntags:\n  - x\n  - z\n", Dump(person));
    }

    [Fact]
    public void Nothing_marks_the_start_of_a_single_document()
    {
        Assert.Equal("1\n", Dump(1));
        Assert.Equal("text\n", Dump("text"));
        Assert.Equal("null\n", Dump<string?>(null));
    }

    [Fact]
    public void Strings_are_plain_when_safe_and_single_quoted_otherwise()
    {
        var tags = new List<string> { "plain words", "yes", "12", "1.5", "", " lead", "trail ", "a: b", "a #b", "- x", "it's", "~", "null", "0x1F", "1_000", "-", "---", "@at", "a:b" };

        Assert.Equal(
            """
            - plain words
            - 'yes'
            - '12'
            - '1.5'
            - ''
            - ' lead'
            - 'trail '
            - 'a: b'
            - 'a #b'
            - '- x'
            - it's
            - '~'
            - 'null'
            - '0x1F'
            - '1_000'
            - '-'
            - '---'
            - '@at'
            - a:b

            """.ReplaceLineEndings("\n"),
            Dump(tags));
    }

    [Fact]
    public void Double_quotes_are_used_only_when_an_escape_is_needed()
    {
        var tags = new List<string> { "bell\a", "nul\0", "del\u007f", "nel\u0085", "ls\u2028", "bom\uFEFF", "lone\r" };

        Assert.Equal(
            "- \"bell\\a\"\n- \"nul\\0\"\n- \"del\\x7F\"\n- \"nel\\N\"\n- \"ls\\L\"\n- \"bom\\uFEFF\"\n- \"lone\\r\"\n",
            Dump(tags));
    }

    [Fact]
    public void Multi_line_strings_are_literal_blocks_with_the_chomping_that_keeps_the_value()
    {
        var holder = new Dictionary<string, string>
        {
            ["one"] = "a\nb\n",
            ["strip"] = "a\nb",
            ["keep"] = "a\nb\n\n",
            ["blank"] = "a\n\nb\n",
            ["indented"] = "  a\nb\n",
        };

        string yaml = Dump(holder);

        Assert.Equal(
            "one: |\n  a\n  b\nstrip: |-\n  a\n  b\nkeep: |+\n  a\n  b\n\nblank: |\n  a\n\n  b\nindented: |2\n    a\n  b\n",
            yaml);
        Assert.Equal(holder, YamlSerializer.Deserialize<Dictionary<string, string>>(yaml));
    }

    [Fact]
    public void A_literal_block_in_a_sequence_and_at_the_root_keeps_its_value()
    {
        string seq = Dump(new List<string> { "a\nb\n", "c" });
        Assert.Equal("- |\n  a\n  b\n- c\n", seq);

        string root = Dump("a\nb\n");
        Assert.Equal("|\n  a\n  b\n", root);
        Assert.Equal("a\nb\n", YamlSerializer.Deserialize<string>(root));
    }

    [Fact]
    public void Lines_of_blanks_and_leading_tabs_fall_back_to_double_quotes()
    {
        Assert.Equal("a: \"x\\n  \\ny\"\n", Dump(new Dictionary<string, string> { ["a"] = "x\n  \ny" }));
        Assert.Equal("a: \"x\\n\\ty\"\n", Dump(new Dictionary<string, string> { ["a"] = "x\n\ty" }));
    }

    [Fact]
    public void Long_text_is_never_wrapped()
    {
        string line = string.Join(' ', Enumerable.Repeat("word", 400));

        Assert.Equal("text: " + line + "\n", Dump(new Dictionary<string, string> { ["text"] = line }));
    }

    [Fact]
    public void Scalars_are_null_true_false_and_numbers_in_their_shortest_form()
    {
        var s = new Scalars
        {
            S8 = -128,
            U8 = 255,
            S16 = -32768,
            U16 = 65535,
            S32 = int.MinValue,
            U32 = uint.MaxValue,
            S64 = long.MinValue,
            U64 = ulong.MaxValue,
            Big = BigInteger.Parse("123456789012345678901234567890", CultureInfo.InvariantCulture),
            Dec = 12.50m,
            Dbl = 0.1,
            Flt = 1.5f,
            Flag = true,
            Ch = 'x',
        };

        string yaml = Dump(s);

        Assert.StartsWith("s8: -128\nu8: 255\ns16: -32768\nu16: 65535\ns32: -2147483648\nu32: 4294967295\ns64: -9223372036854775808\nu64: 18446744073709551615\nbig: 123456789012345678901234567890\ndec: 12.50\ndbl: 0.1\nflt: 1.5\nflag: true\nch: x\n", yaml);
    }

    [Theory]
    [InlineData(1.0, "1.0")]
    [InlineData(-0.0, "-0.0")]
    [InlineData(100.0, "100.0")]
    [InlineData(0.5, "0.5")]
    [InlineData(1e-7, "1.0E-07")]
    [InlineData(double.NaN, ".nan")]
    [InlineData(double.PositiveInfinity, ".inf")]
    [InlineData(double.NegativeInfinity, "-.inf")]
    [InlineData(123456789.125, "123456789.125")]
    public void Doubles_always_show_a_point_or_an_exponent(double value, string expected)
    {
        string yaml = Dump(value);

        Assert.Equal(expected + "\n", yaml);
        Assert.Equal(BitConverter.DoubleToInt64Bits(value), BitConverter.DoubleToInt64Bits(YamlSerializer.Deserialize<double>(yaml)));
    }

    [Fact]
    public void Big_and_small_doubles_read_back_exactly()
    {
        foreach (double value in new[] { 1e16, 1e300, 5e-324, double.MaxValue, double.Epsilon, 1.7976931348623157E+308, 123456789012345678.0, 0.1 + 0.2 })
        {
            string yaml = Dump(value);
            Assert.Equal(value, YamlSerializer.Deserialize<double>(yaml));
            Assert.Equal(value, YamlSerializer.Deserialize<object>(yaml));
        }
    }

    [Fact]
    public void Empty_collections_are_written_in_flow_form()
    {
        Assert.Equal("[]\n", Dump(new List<int>()));
        Assert.Equal("{}\n", Dump(new Dictionary<string, int>()));
        Assert.Equal("tags: []\nname: ''\nmap: {}\nnested:\n  - []\n  - {}\n", Dump(new Dictionary<string, object?>
        {
            ["tags"] = new List<object?>(),
            ["name"] = "",
            ["map"] = new Dictionary<string, object?>(),
            ["nested"] = new List<object?> { new List<object?>(), new Dictionary<string, object?>() },
        }));
    }

    [Fact]
    public void Entries_keep_insertion_order_and_output_is_deterministic()
    {
        var map = new Dictionary<string, int> { ["z"] = 1, ["a"] = 2, ["m"] = 3 };

        string first = Dump(map);

        Assert.Equal("z: 1\na: 2\nm: 3\n", first);
        Assert.Equal(first, Dump(map));
        Assert.Equal(first, Dump(new Dictionary<string, int> { ["z"] = 1, ["a"] = 2, ["m"] = 3 }));
    }

    // ---- layout of nested collections ----

    [Fact]
    public void Nested_collections_are_laid_out_in_block_form()
    {
        var value = new Dictionary<string, object?>
        {
            ["list"] = new List<object?> { 1L, new List<object?> { 2L, 3L }, new Dictionary<string, object?> { ["a"] = 1L, ["b"] = new List<object?> { "x" } } },
            ["map"] = new Dictionary<string, object?> { ["inner"] = new Dictionary<string, object?> { ["deep"] = true } },
            ["last"] = null,
        };

        Assert.Equal(
            """
            list:
              - 1
              - - 2
                - 3
              - a: 1
                b:
                  - x
            map:
              inner:
                deep: true
            last: null

            """.ReplaceLineEndings("\n"),
            Dump<object?>(value));
    }

    [Fact]
    public void The_indent_size_applies_to_every_level()
    {
        var value = new Dictionary<string, object?>
        {
            ["a"] = new Dictionary<string, object?> { ["b"] = new List<object?> { "x", new Dictionary<string, object?> { ["c"] = "y\nz\n" } } },
        };

        string yaml = Dump<object?>(value, new YamlSerializerOptions { IndentSize = 4 });

        Assert.Equal("a:\n    b:\n        - x\n        - c: |\n              y\n              z\n", yaml);
        Assert.Equivalent(value, YamlSerializer.Deserialize<object>(yaml), strict: true);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public void The_indent_size_is_between_one_and_eight(int size)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new YamlSerializerOptions { IndentSize = size });
    }

    // ---- keys ----

    [Fact]
    public void Keys_that_would_read_back_as_something_else_are_quoted()
    {
        var map = new Dictionary<string, int> { ["plain"] = 1, ["yes"] = 2, ["12"] = 3, [""] = 4, ["a b"] = 5, ["- x"] = 6, ["k: v"] = 7, ["multi\nline"] = 8 };

        string yaml = Dump(map);

        Assert.Equal("plain: 1\n'yes': 2\n'12': 3\n'': 4\na b: 5\n'- x': 6\n'k: v': 7\n\"multi\\nline\": 8\n", yaml);
        Assert.Equal(map, YamlSerializer.Deserialize<Dictionary<string, int>>(yaml));
    }

    [Fact]
    public void Keys_of_other_types_are_written_as_they_are_read()
    {
        var byNumber = new Dictionary<int, string> { [1] = "one", [-2] = "minus two" };
        var byColor = new Dictionary<Color, int> { [Color.DarkGreen] = 1, [Color.Red] = 2 };
        var byFlag = new Dictionary<bool, string> { [true] = "t" };
        var byDouble = new Dictionary<double, string> { [1.5] = "x", [2.0] = "z" };
        var byGuid = new Dictionary<Guid, int> { [Guid.Parse("00000000-0000-0000-0000-000000000001")] = 1 };

        Assert.Equal("1: one\n-2: minus two\n", Dump(byNumber));
        Assert.Equal("DarkGreen: 1\nRed: 2\n", Dump(byColor));
        Assert.Equal("true: t\n", Dump(byFlag));
        Assert.Equal("1.5: x\n2.0: z\n", Dump(byDouble));
        Assert.Equal(byNumber, YamlSerializer.Deserialize<Dictionary<int, string>>(Dump(byNumber)));
        Assert.Equal(byColor, YamlSerializer.Deserialize<Dictionary<Color, int>>(Dump(byColor)));
        Assert.Equal(byFlag, YamlSerializer.Deserialize<Dictionary<bool, string>>(Dump(byFlag)));
        Assert.Equal(byDouble, YamlSerializer.Deserialize<Dictionary<double, string>>(Dump(byDouble)));
        Assert.Equal(byGuid, YamlSerializer.Deserialize<Dictionary<Guid, int>>(Dump(byGuid)));
    }

    [Fact]
    public void A_key_longer_than_the_implicit_limit_uses_the_explicit_form()
    {
        string longKey = new string('k', 1100);
        string shortKey = new string('k', 1000);
        var map = new Dictionary<string, int> { [shortKey] = 1, [longKey] = 2, ["after"] = 3 };

        string yaml = Dump(map);

        Assert.Equal(shortKey + ": 1\n? " + longKey + "\n: 2\nafter: 3\n", yaml);
        Assert.Equal(map, YamlSerializer.Deserialize<Dictionary<string, int>>(yaml));
    }

    [Fact]
    public void A_key_whose_escapes_pass_the_limit_uses_the_explicit_form_by_written_length()
    {
        // 200 bytes of control characters are 800 characters in double quotes; 300 of them are 1200.
        string escapes200 = new string('\u0001', 200);
        string escapes300 = new string('\u0001', 300);
        var map = new Dictionary<string, int> { [escapes200] = 1, [escapes300] = 2 };

        string yaml = Dump(map);

        Assert.StartsWith("\"" + string.Concat(Enumerable.Repeat("\\x01", 200)) + "\": 1\n? \"", yaml, StringComparison.Ordinal);
        Assert.Contains("\n: 2\n", yaml, StringComparison.Ordinal);
        Assert.Equal(map, YamlSerializer.Deserialize<Dictionary<string, int>>(yaml));
    }

    [Fact]
    public void A_collection_in_key_position_is_refused()
    {
        var map = new Dictionary<object, int> { [new List<object?> { 1L }] = 1 };

        YamlException ex = Assert.Throws<YamlException>(() => Dump(map));
        Assert.Equal(YamlErrorCategory.Binding, ex.Category);
    }

    // ---- members ----

    [Fact]
    public void Member_names_follow_attributes_the_policy_and_order()
    {
        Assert.Equal("runs-on: ubuntu\npull_request: null\nplainName: p\n", Dump(new Names { RunsOn = "ubuntu", PlainName = "p" }));
        Assert.Equal("runs-on: ubuntu\npull_request: null\nPlainName: p\n", Dump(new Names { RunsOn = "ubuntu", PlainName = "p" }, new YamlSerializerOptions { PropertyNamingPolicy = null }));
        Assert.Equal("first: 0\nsecond: 0\n", Dump(new Ordered()));
    }

    [Fact]
    public void Members_without_a_public_setter_are_not_written_because_they_could_not_be_read_back()
    {
        string yaml = Dump(new Ordered());

        Assert.DoesNotContain("readOnlyValue", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("writeOnly", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Ignore_conditions_apply_per_member_and_by_default()
    {
        Assert.Equal("name: null\ncount: 0\nalways: null\n", Dump(new Unconditional()));
        Assert.Equal("name: nm\ncount: 1\nnote: x\nlevel: 3\nalways: a\n", Dump(new Unconditional { Name = "nm", Count = 1, Note = "x", Level = 3, Always = "a" }));
        Assert.Equal("count: 0\nalways: null\n", Dump(new Unconditional(), new YamlSerializerOptions { DefaultIgnoreCondition = YamlIgnoreCondition.WhenWritingNull }));
        Assert.Equal("always: null\n", Dump(new Unconditional(), new YamlSerializerOptions { DefaultIgnoreCondition = YamlIgnoreCondition.WhenWritingDefault }));
        Assert.Throws<ArgumentException>(() => new YamlSerializerOptions { DefaultIgnoreCondition = YamlIgnoreCondition.Always });
    }

    [Fact]
    public void Ignored_members_and_marked_members_are_left_out_or_renamed()
    {
        string yaml = Dump(new Names { RunsOn = "r", Hidden = "h", AlsoHidden = "a" });

        Assert.DoesNotContain("hidden", yaml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("runs-on: r", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Records_structs_and_fields_are_written_by_their_bound_members()
    {
        Assert.Equal("x: 1\n'y': 7\n", Dump(new Point(1)));
        Assert.Equal("left: 3\nright: 4\n", Dump(Pair.Of(3, 4)));
        Assert.Equal("name: nm\nextra: 2\nnote: null\n", Dump(new Mixed("nm") { Extra = 2 }));
        Assert.Equal("replicas: 3\nscript: |\n  make\n", Dump(new Config(3, "make\n")));
    }

    [Fact]
    public void Enums_are_written_by_declared_name_or_through_the_enum_policy_and_flags_by_their_plain_names()
    {
        var camel = new YamlSerializerOptions { EnumNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };
        Assert.Equal("DarkGreen\n", Dump(Color.DarkGreen));
        Assert.Equal("darkGreen\n", Dump(Color.DarkGreen, camel));
        Assert.Equal("DarkGreen\n", Dump(Color.DarkGreen, new YamlSerializerOptions { PropertyNamingPolicy = null }));
        Assert.Equal("Read\n", Dump(Perm.Read));
        Assert.Equal("read\n", Dump(Perm.Read, camel));
        Assert.Equal("Read, Write\n", Dump(Perm.Read | Perm.Write).Replace("'", string.Empty, StringComparison.Ordinal));
        Assert.Equal("7\n", Dump((Color)7));
        Assert.Equal("None\n", Dump(Perm.None));
        Assert.Equal(Perm.Read | Perm.Write, YamlSerializer.Deserialize<Perm>(Dump(Perm.Read | Perm.Write)));
        Assert.Equal((Color)7, YamlSerializer.Deserialize<Color>(Dump((Color)7)));
    }

    [Fact]
    public void Dates_guids_uris_and_bytes_are_written_in_forms_the_reader_accepts()
    {
        var s = new Scalars
        {
            Id = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e"),
            When = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc),
            At = new DateTimeOffset(2024, 5, 6, 7, 8, 9, TimeSpan.FromHours(2)),
            Day = new DateOnly(2024, 5, 6),
            Clock = new TimeOnly(7, 8, 9),
            Span = TimeSpan.FromSeconds(90),
            Link = new Uri("https://example.com/a?b=c"),
            Bytes = [1, 2, 3, 250],
            Maybe = 5,
        };

        string yaml = Dump(s);

        Assert.Contains("id: 0f8fad5b-d9cb-469f-a165-70867728950e\n", yaml, StringComparison.Ordinal);
        Assert.Contains("link: https://example.com/a?b=c\n", yaml, StringComparison.Ordinal);
        Assert.Contains("bytes: !!binary AQID+g==\n", yaml, StringComparison.Ordinal);
        Assert.Contains("maybe: 5\n", yaml, StringComparison.Ordinal);
        Scalars back = YamlSerializer.Deserialize<Scalars>(yaml)!;
        Assert.Equal(s.Id, back.Id);
        Assert.Equal(s.When, back.When);
        Assert.Equal(DateTimeKind.Utc, back.When.Kind);
        Assert.Equal(s.At, back.At);
        Assert.Equal(s.Day, back.Day);
        Assert.Equal(s.Clock, back.Clock);
        Assert.Equal(s.Span, back.Span);
        Assert.Equal(s.Link, back.Link);
        Assert.Equal(s.Bytes, back.Bytes);
    }

    [Fact]
    public void Collections_of_every_supported_shape_are_written_as_sequences_and_mappings()
    {
        var shapes = new Shapes
        {
            Array = [1, 2],
            Roots = ["a"],
            Set = [3],
            Map = new Dictionary<string, int> { ["k"] = 1 },
            ByNumber = new Dictionary<int, string> { [1] = "one" },
            Sorted = new SortedDictionary<string, bool> { ["b"] = true, ["a"] = false },
            Doubles = [1.5],
        };

        string yaml = Dump(shapes);

        Assert.Equal("array:\n  - 1\n  - 2\nroots:\n  - a\nset:\n  - 3\nmap:\n  k: 1\nbyNumber:\n  1: one\nsorted:\n  a: false\n  b: true\ndoubles:\n  - 1.5\n", yaml);
    }

    [Fact]
    public void Object_members_are_written_by_their_run_time_type()
    {
        var holder = new Holder
        {
            Value = new Person { Name = "Ada", Tags = ["t"] },
            Bag = new Dictionary<string, object?> { ["nn"] = 1, ["color"] = Color.Blue, ["when"] = new DateOnly(2024, 1, 2), ["p"] = new Point(1, 2), ["bytes"] = new byte[] { 1 } },
        };

        string yaml = Dump(holder);

        Assert.Equal(
            "value:\n  name: Ada\n  age: 0\n  nickname: null\n  tags:\n    - t\nbag:\n  nn: 1\n  color: Blue\n  when: '2024-01-02'\n  p:\n    x: 1\n    'y': 2\n  bytes: !!binary AQ==\n",
            yaml);
    }

    // ---- error contract ----

    [Fact]
    public void A_value_that_contains_itself_is_a_clean_error()
    {
        var node = new Cyclic { Name = "a" };
        node.Next = node;

        YamlException ex = Assert.Throws<YamlException>(() => Dump(node));

        Assert.Equal(YamlErrorCategory.Binding, ex.Category);
        Assert.Contains("itself", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_collection_that_contains_itself_is_a_clean_error_with_every_limit_setting()
    {
        var list = new List<object?>();
        list.Add(list);
        var map = new Dictionary<string, object?>();
        map["self"] = map;

        foreach (YamlLimits limits in new[] { YamlLimits.Default, YamlLimits.Untrusted, YamlLimits.None })
        {
            var options = new YamlSerializerOptions { Limits = limits };
            Assert.Equal(YamlErrorCategory.Binding, Assert.Throws<YamlException>(() => Dump<object?>(list, options)).Category);
            Assert.Equal(YamlErrorCategory.Binding, Assert.Throws<YamlException>(() => Dump<object?>(map, options)).Category);
        }
    }

    [Fact]
    public void A_cycle_through_typed_members_and_objects_is_found_at_any_depth()
    {
        var a = new Cyclic { Name = "a" };
        var b = new Cyclic { Name = "b" };
        a.Items.Add(b);
        b.Items.Add(a);

        Assert.Equal(YamlErrorCategory.Binding, Assert.Throws<YamlException>(() => Dump(a, new YamlSerializerOptions { Limits = YamlLimits.None })).Category);
    }

    [Fact]
    public void Nesting_deeper_than_the_limit_is_a_limit_error_and_the_same_value_shared_twice_is_fine()
    {
        object? deep = "x";
        for (int i = 0; i < 1500; i++)
        {
            deep = new List<object?> { deep };
        }

        Assert.Equal(YamlErrorCategory.Limit, Assert.Throws<YamlException>(() => Dump(deep)).Category);
        Assert.Equal("- - x\n", Dump<object?>(new List<object?> { new List<object?> { "x" } }));

        var shared = new List<object?> { 1L };
        string twice = Dump<object?>(new List<object?> { shared, shared });
        Assert.Equal("- - 1\n- - 1\n", twice);
    }

    [Fact]
    public void Very_deep_values_are_written_without_stack_growth_when_the_limit_allows_it()
    {
        object? deep = "x";
        for (int i = 0; i < 50_000; i++)
        {
            deep = new List<object?> { deep };
        }

        string yaml = OnSmallStack(() => Dump(deep, new YamlSerializerOptions { Limits = YamlLimits.None }));

        Assert.Equal(50_000 * 2 + 2, yaml.Length);
        Assert.StartsWith("- - - ", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Deep_typed_nesting_ends_in_a_limit_error_on_a_small_stack()
    {
        Cyclic? chain = null;
        for (int i = 0; i < 200_000; i++)
        {
            chain = new Cyclic { Next = chain };
        }

        YamlException ex = Assert.IsType<YamlException>(OnSmallStackThrows(() => Dump(chain, new YamlSerializerOptions { Limits = YamlLimits.None })));
        Assert.Equal(YamlErrorCategory.Limit, ex.Category);
    }

    [Fact]
    public void An_unpaired_surrogate_has_no_utf8_form_and_is_refused()
    {
        YamlException ex = Assert.Throws<YamlException>(() => Dump("bad\uD800text"));
        Assert.Equal(YamlErrorCategory.Binding, ex.Category);
        Assert.Throws<YamlException>(() => Dump(new Dictionary<string, int> { ["\uDC00"] = 1 }));
    }

    [Fact]
    public void Arguments_are_checked()
    {
        Assert.Throws<ArgumentNullException>(() => YamlSerializer.Serialize(1, (YamlTypeInfo<int>)null!));
        Assert.Throws<ArgumentNullException>(() => YamlSerializer.Serialize((Stream)null!, 1));
        Assert.Throws<ArgumentNullException>(() => YamlSerializer.Serialize((TextWriter)null!, 1));
        Assert.Throws<ArgumentNullException>(() => YamlSerializer.Serialize((IBufferWriter<byte>)null!, 1));
        Assert.Throws<ArgumentException>(() => YamlSerializer.Serialize("text", typeof(int)));
        Assert.Throws<ArgumentNullException>(() => YamlSerializer.Serialize(1, (Type)null!));
    }

    // ---- entry points ----

    [Fact]
    public async Task Every_entry_point_writes_the_same_text_as_utf8_without_a_byte_order_mark()
    {
        var person = new Person { Name = "Zoë 日本", Age = 3, Tags = ["a"] };
        string expected = Dump(person);

        Assert.Equal(expected, YamlSerializer.Serialize((object?)person, typeof(Person)));
        Assert.Equal(expected, Encoding.UTF8.GetString(YamlSerializer.SerializeToUtf8Bytes(person)));
        Assert.Equal(expected, ModelsContext.Default.Person is { } info ? YamlSerializer.Serialize(person, info) : null);
        Assert.Equal(expected, YamlSerializer.Serialize((object?)person, (YamlTypeInfo)ModelsContext.Default.Person));

        using var stream = new MemoryStream();
        YamlSerializer.Serialize(stream, person);
        Assert.Equal(Encoding.UTF8.GetBytes(expected), stream.ToArray());

        using var asyncStream = new MemoryStream();
        await YamlSerializer.SerializeAsync(asyncStream, person, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(Encoding.UTF8.GetBytes(expected), asyncStream.ToArray());

        var text = new StringWriter();
        YamlSerializer.Serialize(text, person);
        Assert.Equal(expected, text.ToString());

        var buffer = new ArrayBufferWriter<byte>();
        YamlSerializer.Serialize(buffer, person);
        Assert.Equal(Encoding.UTF8.GetBytes(expected), buffer.WrittenSpan.ToArray());

        Assert.Equal(expected, YamlSerializer.Serialize(person, ModelsContext.Default.Person));
        Assert.Equal(Encoding.UTF8.GetBytes(expected), YamlSerializer.SerializeToUtf8Bytes(person, ModelsContext.Default.Person));
    }

    [Fact]
    public void Output_that_spans_many_buffer_segments_is_complete()
    {
        var big = Enumerable.Range(0, 20_000).Select(i => new Person { Name = "name " + i, Age = i, Tags = ["a", "b"] }).ToList();

        string yaml = Dump(big);

        Assert.True(yaml.Length > 800_000);
        Assert.Equal(20_000, YamlSerializer.Deserialize<List<Person>>(yaml)!.Count);
    }

    // ---- converters and the emitter ----

    private sealed class UpperConverter : YamlConverter<string>
    {
        public override string? Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options) => reader.GetString();

        public override void Write(Utf8YamlEmitter writer, string value, YamlSerializerOptions options) => writer.WriteStringValue(value.ToUpperInvariant());
    }

    private sealed class PointConverter : YamlConverter<Point>
    {
        public override Point? Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options) => throw new NotSupportedException();

        public override void Write(Utf8YamlEmitter writer, Point value, YamlSerializerOptions options)
        {
            writer.WriteStartSequence();
            writer.WriteNumberValue(value.X);
            writer.WriteNumberValue(value.Y);
            writer.WriteEndSequence();
        }
    }

    private sealed class NullAwareConverter : YamlConverter<Person>
    {
        public override bool HandleNull => true;

        public override Person? Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options) => null;

        public override void Write(Utf8YamlEmitter writer, Person value, YamlSerializerOptions options) => writer.WriteStringValue(value is null ? "none" : "some");
    }

    [Fact]
    public void A_custom_converter_writes_its_type_wherever_it_appears()
    {
        var options = new YamlSerializerOptions { Converters = { new UpperConverter(), new PointConverter() } };

        Assert.Equal("name: ADA\nage: 0\nnickname: null\ntags:\n  - X\n", Dump(new Person { Name = "Ada", Tags = ["x"] }, options));
        Assert.Equal("- - 1\n  - 2\n- - 3\n  - 4\n", Dump(new List<Point> { new(1, 2), new(3, 4) }, options));
        Assert.Equal("- A\n- 1\n- - 1\n  - 2\n", Dump<object?>(new List<object?> { "a", 1L, new Point(1, 2) }, options));
        Assert.Equal("HELLO: 1\n", Dump(new Dictionary<string, int> { ["hello"] = 1 }, options));
    }

    [Fact]
    public void A_converter_sees_a_null_only_when_it_asks_for_it()
    {
        var options = new YamlSerializerOptions { Converters = { new NullAwareConverter() } };

        Assert.Equal("none\n", Dump<Person?>(null, options));
        Assert.Equal("some\n", Dump<Person?>(new Person(), options));
        Assert.Equal("null\n", Dump<Person?>(null));
    }

    private static string Emit(Action<Utf8YamlEmitter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var emitter = new Utf8YamlEmitter(buffer);
        write(emitter);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    [Fact]
    public void The_emitter_writes_a_document_from_calls()
    {
        string yaml = Emit(e =>
        {
            e.WriteStartMapping();
            e.WritePropertyName("name");
            e.WriteStringValue("emit");
            e.WritePropertyName("list");
            e.WriteStartSequence();
            e.WriteNumberValue(1);
            e.WriteNumberValue(2.5);
            e.WriteBooleanValue(false);
            e.WriteNullValue();
            e.WriteBinaryValue([1, 2, 3]);
            e.WriteEndSequence();
            e.WriteEndMapping();
        });

        Assert.Equal("name: emit\nlist:\n  - 1\n  - 2.5\n  - false\n  - null\n  - !!binary AQID\n", yaml);
    }

    [Fact]
    public void The_emitter_refuses_calls_in_the_wrong_order()
    {
        Assert.Throws<YamlException>(() => Emit(e => { e.WriteStartMapping(); e.WriteStartSequence(); }));
        Assert.Throws<YamlException>(() => Emit(e => { e.WriteStartMapping(); e.WritePropertyName("a"); e.WritePropertyName("b"); }));
        Assert.Throws<YamlException>(() => Emit(e => { e.WriteStartMapping(); e.WritePropertyName("a"); e.WriteEndMapping(); }));
        Assert.Throws<YamlException>(() => Emit(e => { e.WriteStartSequence(); e.WriteEndMapping(); }));
        Assert.Throws<YamlException>(() => Emit(e => { e.WriteEndSequence(); }));
        Assert.Throws<YamlException>(() => Emit(e => { e.WriteNullValue(); e.WriteNullValue(); }));
        Assert.Throws<YamlException>(() => Emit(e => { e.WriteStartSequence(); e.WritePropertyName("a"); }));
        Assert.Throws<YamlException>(() => Emit(e => { e.WriteStartMapping(); e.WriteBinaryValue([1]); }));
        Assert.Throws<ArgumentNullException>(() => new Utf8YamlEmitter(null!));
        Assert.Throws<ArgumentNullException>(() => Emit(e => e.WritePropertyName((string)null!)));
    }

    [Fact]
    public void Bytes_that_are_not_utf8_are_written_as_binary_data()
    {
        string yaml = Emit(e => e.WriteStringValue(new byte[] { 0xFF, 0xFE, 0x41 }));

        Assert.Equal("!!binary //5B\n", yaml);
        string text = Emit(e => e.WriteStringValue("é"u8));
        Assert.Equal("é\n", text);
    }

    [Fact]
    public void A_scalar_in_key_position_is_the_key()
    {
        string yaml = Emit(e =>
        {
            e.WriteStartMapping();
            e.WriteNumberValue(7L);
            e.WriteStringValue("seven");
            e.WriteBooleanValue(true);
            e.WriteNumberValue(1.5);
            e.WriteNullValue();
            e.WriteNumberValue(3);
            e.WriteStringValue("007");
            e.WriteStringValue("x");
            e.WriteEndMapping();
        });

        Assert.Equal("7: seven\ntrue: 1.5\nnull: 3\n'007': x\n", yaml);
    }

    [Fact]
    public void An_options_object_in_use_keeps_its_settings_for_the_emitter()
    {
        var options = new YamlSerializerOptions { IndentSize = 3 };

        string yaml = Emit(e => { });
        Assert.Equal(string.Empty, yaml);

        var buffer = new ArrayBufferWriter<byte>();
        var emitter = new Utf8YamlEmitter(buffer, options);
        emitter.WriteStartMapping();
        emitter.WritePropertyName("a");
        emitter.WriteStartMapping();
        emitter.WritePropertyName("b");
        emitter.WriteNumberValue(1);
        emitter.WriteEndMapping();
        emitter.WriteEndMapping();

        Assert.Equal("a:\n   b: 1\n", Encoding.UTF8.GetString(buffer.WrittenSpan));
        Assert.Same(options, emitter.Options);
        Assert.Equal(0, emitter.CurrentDepth);
    }

    // ---- helpers ----

    private static string OnSmallStack(Func<string> run)
    {
        string result = string.Empty;
        Exception? failure = null;
        var thread = new Thread(
            () =>
            {
                try
                {
                    result = run();
                }
                catch (Exception e)
                {
                    failure = e;
                }
            },
            1024 * 1024);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new InvalidOperationException("The write failed on a small stack.", failure);
        }

        return result;
    }

    private static Exception? OnSmallStackThrows(Func<string> run)
    {
        Exception? failure = null;
        var thread = new Thread(
            () =>
            {
                try
                {
                    run();
                }
                catch (Exception e)
                {
                    failure = e;
                }
            },
            1024 * 1024);
        thread.Start();
        thread.Join();
        return failure;
    }
}
