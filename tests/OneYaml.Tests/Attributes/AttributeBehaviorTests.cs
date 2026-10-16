using Xunit;

namespace OneYaml.Tests.Attributes;

// Converter, extension data and polymorphism attributes, through reflection and through a generated context.
public class AttributeBehaviorTests
{
    private static readonly AttributeContext s_context = new();

    public static TheoryData<bool> Paths => new() { false, true };

    private static T? Read<T>(bool generated, string yaml, YamlSerializerOptions? options = null) =>
        generated
            ? YamlSerializer.Deserialize(yaml, (YamlTypeInfo<T>)(options is null ? s_context : new AttributeContext(options)).GetTypeInfo(typeof(T))!)
            : YamlSerializer.Deserialize<T>(yaml, options);

    private static string Write<T>(bool generated, T value) =>
        generated
            ? YamlSerializer.Serialize(value, (YamlTypeInfo<T>)s_context.GetTypeInfo(typeof(T))!)
            : YamlSerializer.Serialize(value);

    // ---- [YamlConverter] ----

    [Theory, MemberData(nameof(Paths))]
    public void A_converter_on_a_type_reads_and_writes_it_wherever_it_appears(bool generated)
    {
        Job job = Read<Job>(generated, "level: lv3\nname: build\nplain: x\n")!;

        Assert.Equal(new Level(3), job.Level);
        Assert.Equal("level: lv3\nname: build\nplain: x\n", Write(generated, new Job { Level = new Level(3), Name = "BUILD", Plain = "x" }));
    }

    [Theory, MemberData(nameof(Paths))]
    public void A_converter_on_a_member_applies_to_that_member_only(bool generated)
    {
        Job job = Read<Job>(generated, "name: build\nplain: build\n")!;

        Assert.Equal("BUILD", job.Name);
        Assert.Equal("build", job.Plain);
    }

    [Theory, MemberData(nameof(Paths))]
    public void A_converter_on_a_constructor_bound_member_is_used(bool generated)
    {
        Step step = Read<Step>(generated, "id: a\ntext: hi\n")!;

        Assert.Equal(new Step("a", "HI"), step);
        Assert.Equal("id: a\ntext: hi\n", Write(generated, new Step("a", "HI")));
    }

    [Theory, MemberData(nameof(Paths))]
    public void An_open_generic_converter_is_closed_over_the_type_arguments(bool generated)
    {
        Crate crate = Read<Crate>(generated, "content: stuff\n")!;

        Assert.Equal("<stuff>", crate.Content.Value);
        Assert.Equal("content: <stuff>\n", Write(generated, crate));
    }

    [Theory, MemberData(nameof(Paths))]
    public void An_error_from_a_converter_carries_the_position_and_path(bool generated)
    {
        YamlException ex = Assert.Throws<YamlException>(() => Read<Job>(generated, "plain: x\nlevel: nope\n"));

        Assert.Equal(YamlErrorCategory.Binding, ex.Category);
        Assert.Equal(2, ex.Line);
        Assert.Contains("lv3", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_converter_in_the_options_wins_over_the_one_on_the_type()
    {
        var options = new YamlSerializerOptions { Converters = { new ShoutConverter2() } };

        Assert.Equal(new Level(9), YamlSerializer.Deserialize<Level>("nine", options));
    }

    private sealed class ShoutConverter2 : YamlConverter<Level>
    {
        public override Level Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options) => new(9);

        public override void Write(Utf8YamlEmitter writer, Level value, YamlSerializerOptions options) => writer.WriteStringValue("n");
    }

    // ---- [YamlExtensionData] ----

    [Theory, MemberData(nameof(Paths))]
    public void Extension_data_collects_the_keys_no_member_takes(bool generated)
    {
        Settings s = Read<Settings>(generated, "name: a\nport: 80\ncolour: red\nwidth: 3\ntags: [x, y]\nnested: {k: v}\nnothing: ~\n")!;

        Assert.Equal("a", s.Name);
        Assert.Equal(80, s.Port);
        Assert.Equal(["colour", "width", "tags", "nested", "nothing"], s.Extra.Keys);
        Assert.Equal("red", s.Extra["colour"]);
        Assert.Equal(3L, s.Extra["width"]);
        Assert.Equal(new List<object?> { "x", "y" }, Assert.IsType<List<object?>>(s.Extra["tags"]));
        Assert.Equal("v", Assert.IsType<Dictionary<string, object?>>(s.Extra["nested"])["k"]);
        Assert.Null(s.Extra["nothing"]);
    }

    [Theory, MemberData(nameof(Paths))]
    public void Extension_data_is_written_after_the_members(bool generated)
    {
        var s = new Settings { Name = "a", Port = 80 };
        s.Extra["colour"] = "red";
        s.Extra["tags"] = new List<object?> { "x", 2L };

        Assert.Equal("name: a\nport: 80\ncolour: red\ntags:\n  - x\n  - 2\n", Write(generated, s));
    }

    [Theory, MemberData(nameof(Paths))]
    public void A_dictionary_is_made_when_the_member_holds_none(bool generated)
    {
        Lazy v = Read<Lazy>(generated, "name: a\nz: 1\n")!;

        Assert.Equal(1L, v.Rest!["z"]);
        Assert.Null(Read<Lazy>(generated, "name: a\n")!.Rest);
    }

    [Theory, MemberData(nameof(Paths))]
    public void The_System_Text_Json_attribute_is_honoured_on_a_dictionary_of_objects(bool generated)
    {
        FromJson v = Read<FromJson>(generated, "name: a\nextra: 1\n")!;

        Assert.Equal(1L, v.Other["extra"]);
    }

    [Theory, MemberData(nameof(Paths))]
    public void Extension_data_works_with_a_constructor(bool generated)
    {
        Labelled v = Read<Labelled>(generated, "extra: 1\nname: a\nsize: 2\nmore: x\n")!;

        Assert.Equal("a", v.Name);
        Assert.Equal(2, v.Size);
        Assert.Equal(["extra", "more"], v.Extra.Keys);
    }

    [Theory, MemberData(nameof(Paths))]
    public void A_repeated_key_is_still_an_error_when_it_goes_to_extension_data(bool generated)
    {
        YamlException ex = Assert.Throws<YamlException>(() => Read<Settings>(generated, "a: 1\nname: x\na: 2\n"));

        Assert.Equal(YamlErrorCategory.DuplicateKey, ex.Category);
        Assert.Equal(3, ex.Line);
    }

    [Theory, MemberData(nameof(Paths))]
    public void A_merged_entry_gives_way_to_a_key_the_mapping_has(bool generated)
    {
        Settings s = Read<Settings>(generated, "base: &b {colour: red, size: 1}\nname: n\n<<: *b\ncolour: blue\n", new YamlSerializerOptions { MergeKeys = true })!;

        Assert.Equal("blue", s.Extra["colour"]);
        Assert.Equal(1L, s.Extra["size"]);
    }

    // ---- [YamlPolymorphic], key discriminator ----

    [Theory, MemberData(nameof(Paths))]
    public void A_key_discriminator_picks_the_derived_type(bool generated)
    {
        Home home = Read<Home>(generated, "pet:\n  $type: cat\n  name: Tom\n  lives: 7\nothers:\n- $type: dog\n  name: Rex\n  good: false\n")!;

        Cat cat = Assert.IsType<Cat>(home.Pet);
        Assert.Equal("Tom", cat.Name);
        Assert.Equal(7, cat.Lives);
        Hound hound = Assert.IsType<Hound>(Assert.Single(home.Others));
        Assert.False(hound.Good);
    }

    [Theory, MemberData(nameof(Paths))]
    public void A_key_discriminator_is_written_first_when_the_declared_type_is_the_base(bool generated)
    {
        var home = new Home { Pet = new Cat { Name = "Tom", Lives = 7 }, Others = [new Hound { Name = "Rex" }] };

        Assert.Equal("pet:\n  $type: cat\n  lives: 7\n  name: Tom\nothers:\n  - $type: dog\n    good: true\n    name: Rex\n", Write(generated, home));
    }

    [Theory, MemberData(nameof(Paths))]
    public void An_unknown_discriminator_is_a_binding_error(bool generated)
    {
        YamlException ex = Assert.Throws<YamlException>(() => Read<Home>(generated, "pet:\n  $type: fish\n  name: Nemo\n"));

        Assert.Equal(YamlErrorCategory.Binding, ex.Category);
        Assert.Equal(2, ex.Line);
        Assert.Contains("fish", ex.Message, StringComparison.Ordinal);
    }

    [Theory, MemberData(nameof(Paths))]
    public void An_abstract_base_with_no_discriminator_is_an_error(bool generated)
    {
        YamlException ex = Assert.Throws<YamlException>(() => Read<Home>(generated, "pet:\n  name: Nemo\n"));

        Assert.Equal(YamlErrorCategory.Binding, ex.Category);
    }

    [Theory, MemberData(nameof(Paths))]
    public void A_key_discriminator_must_come_first(bool generated)
    {
        YamlException ex = Assert.Throws<YamlException>(() => Read<Home>(generated, "pet:\n  name: Tom\n  $type: cat\n"));

        Assert.Equal(YamlErrorCategory.Binding, ex.Category);
        Assert.Contains("$type", ex.Message, StringComparison.Ordinal);
    }

    [Theory, MemberData(nameof(Paths))]
    public void A_root_value_of_the_derived_type_is_written_without_the_discriminator(bool generated)
    {
        Assert.Equal("lives: 9\nname: Tom\n", Write(generated, new Cat { Name = "Tom" }));
        Assert.Equal("lives: 9\nname: Tom\n", Write<Cat>(generated, new Cat { Name = "Tom" }));
    }

    [Theory, MemberData(nameof(Paths))]
    public void The_System_Text_Json_attributes_name_the_key_and_a_creatable_base_reads_as_itself(bool generated)
    {
        Assert.Equal("Red", Assert.IsType<Car>(Read<Vehicle>(generated, "kind: car\ncolour: Red\n")).Colour);
        Assert.Equal(3, Assert.IsType<Truck>(Read<Vehicle>(generated, "kind: 7\nload: 3\n")).Load);
        Vehicle plain = Read<Vehicle>(generated, "wheels: 6\n")!;
        Assert.Equal(typeof(Vehicle), plain.GetType());
        Assert.Equal(6, plain.Wheels);
    }

    [Theory, MemberData(nameof(Paths))]
    public void Constructor_types_work_as_derived_types_and_as_a_base_read_as_itself(bool generated)
    {
        Assert.Equal(new Memo("a", 2), Read<Note>(generated, "$type: memo\ntitle: a\npriority: 2\n"));
        Assert.Equal(new Note("b"), Read<Note>(generated, "title: b\n"));
        Assert.Equal("$type: memo\npriority: 2\ntitle: a\n", Write<Note>(generated, new Memo("a", 2)));
    }

    // ---- [YamlPolymorphic], tag discriminator ----

    [Theory, MemberData(nameof(Paths))]
    public void A_tag_discriminator_picks_the_derived_type_including_scalars_and_constructor_types(bool generated)
    {
        Drawing d = Read<Drawing>(generated, "figures:\n- !circle {radius: 2}\n- !square\n  side: 3\n- !label hello\n")!;

        Assert.Equal(new Circle(2), d.Figures[0]);
        Assert.Equal(3, Assert.IsType<Square>(d.Figures[1]).Side);
        Assert.Equal("hello", Assert.IsType<Label>(d.Figures[2]).Text);
    }

    [Theory, MemberData(nameof(Paths))]
    public void A_tag_discriminator_is_written_as_a_tag(bool generated)
    {
        var d = new Drawing { Figures = [new Circle(2), new Square { Side = 3 }, new Label { Text = "hello" }] };

        string yaml = Write(generated, d);

        Assert.Equal("figures:\n  - !circle\n    radius: 2.0\n  - !square\n    side: 3.0\n  - !label hello\n", yaml);
        Drawing back = Read<Drawing>(generated, yaml)!;
        Assert.Equal(new Circle(2), back.Figures[0]);
    }

    [Theory, MemberData(nameof(Paths))]
    public void A_missing_or_unknown_tag_is_a_binding_error(bool generated)
    {
        YamlException none = Assert.Throws<YamlException>(() => Read<Drawing>(generated, "figures:\n- {radius: 2}\n"));
        YamlException unknown = Assert.Throws<YamlException>(() => Read<Drawing>(generated, "figures:\n- !blob {radius: 2}\n"));

        Assert.Equal(YamlErrorCategory.Binding, none.Category);
        Assert.Equal(YamlErrorCategory.Binding, unknown.Category);
        Assert.Contains("!blob", unknown.Message, StringComparison.Ordinal);
    }

    // ---- reader and emitter ----

    [Fact]
    public void The_reader_reports_the_tag_and_anchor_of_a_node()
    {
        TagProbe probe = YamlSerializer.Deserialize<TagProbe>("a: !t &x 1\nb: !!str 2\nc: 3\nd: !<tag:example.com,2000:y> 4\n")!;

        Assert.Equal("!t|x|!t", probe.A);
        Assert.Equal("tag:yaml.org,2002:str||!!str", probe.B);
        Assert.Equal("||", probe.C);
        Assert.Equal("tag:example.com,2000:y||!<tag:example.com,2000:y>", probe.D);
    }

    [Fact]
    public void The_emitter_writes_tags_anchors_and_aliases()
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        var writer = new Utf8YamlEmitter(buffer);
        {
            writer.WriteStartMapping();
            writer.WritePropertyName("a");
            writer.WriteTag("!t");
            writer.WriteAnchor("x");
            writer.WriteNumberValue(1);
            writer.WritePropertyName("b");
            writer.WriteAlias("x");
            writer.WritePropertyName("c");
            writer.WriteTag("tag:yaml.org,2002:str");
            writer.WriteStringValue("5");
            writer.WriteEndMapping();
        }

        string yaml = System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);

        Assert.Equal("a: !t &x 1\nb: *x\nc: !!str '5'\n", yaml);
    }
}
