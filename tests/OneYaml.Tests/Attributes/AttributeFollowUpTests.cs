using System.Globalization;
using Xunit;

namespace OneYaml.Tests.Attributes;

// Behaviour the converter, extension data and polymorphism attributes must share across reflection, generated metadata
// and a value bound from a YamlDocument.
public class AttributeFollowUpTests
{
    private static readonly AttributeContext s_context = new();

    // 0 = reflection, 1 = generated metadata, 2 = bound from a YamlDocument.
    public static TheoryData<int> Paths => new() { 0, 1, 2 };

    private static T? Read<T>(int path, string yaml, YamlSerializerOptions? options = null) => path switch
    {
        0 => YamlSerializer.Deserialize<T>(yaml, options),
        1 => YamlSerializer.Deserialize(yaml, (YamlTypeInfo<T>)(options is null ? s_context : new AttributeContext(options)).GetTypeInfo(typeof(T))!),
        _ => YamlDocument.Parse(yaml).Root.Deserialize<T>(options),
    };

    private static string Write<T>(int path, T value) =>
        path == 1 ? YamlSerializer.Serialize(value, (YamlTypeInfo<T>)s_context.GetTypeInfo(typeof(T))!) : YamlSerializer.Serialize(value);

    // ---- a repeated discriminator ----

    [Theory, MemberData(nameof(Paths))]
    public void A_repeated_discriminator_key_is_a_duplicate_key(int path)
    {
        YamlException ex = Assert.Throws<YamlException>(() => Read<Home>(path, "pet: {$type: cat, $type: dog}\n"));

        Assert.Equal(YamlErrorCategory.DuplicateKey, ex.Category);
        Assert.Equal(1, ex.Line);
        Assert.Equal(19, ex.Column);
    }

    [Theory, MemberData(nameof(Paths))]
    public void A_repeated_discriminator_key_is_not_collected_as_extension_data(int path)
    {
        YamlException ex = Assert.Throws<YamlException>(() => Read<Home>(path, "pet: {$type: wild, name: x, $type: cat}\n"));

        Assert.Equal(YamlErrorCategory.DuplicateKey, ex.Category);
    }

    // ---- a discriminator that is not the first key ----

    [Theory, MemberData(nameof(Paths))]
    public void A_discriminator_after_another_key_is_refused_on_a_base_that_can_be_created(int path)
    {
        YamlException ex = Assert.Throws<YamlException>(() => Read<Vehicle>(path, "wheels: 3\nkind: car\n"));

        Assert.Equal(YamlErrorCategory.Binding, ex.Category);
        Assert.Equal(2, ex.Line);
        Assert.Contains("kind", ex.Message, StringComparison.Ordinal);
        Assert.Equal(3, Read<Vehicle>(path, "wheels: 3\nother: car\n")!.Wheels);
    }

    // ---- the key a polymorphic type uses ----

    [Theory, MemberData(nameof(Paths))]
    public void The_YAML_attribute_names_the_key_even_when_the_System_Text_Json_one_names_another(int path)
    {
        Assert.IsType<MixA>(Read<Mixed>(path, "$type: a\ny: 1\n"));
        Assert.Equal(typeof(Mixed), Read<Mixed>(path, "kind: a\nx: 1\n")!.GetType());
    }

    [Theory, MemberData(nameof(Paths))]
    public void The_written_key_is_the_one_the_YAML_attribute_names(int path)
    {
        if (path == 2)
        {
            return;
        }

        Assert.Equal("$type: a\n'y': 2\nx: 0\n", Write<Mixed>(path, new MixA { Y = 2 }));
    }

    [Fact]
    public void Integer_discriminators_are_formatted_with_the_invariant_culture()
    {
        CultureInfo saved = CultureInfo.CurrentCulture;
        try
        {
            // Swedish writes the minus sign as U+2212.
            CultureInfo.CurrentCulture = new CultureInfo("sv-SE");
            Assert.SkipUnless(CultureInfo.CurrentCulture.NumberFormat.NegativeSign == "\u2212", "This host has no culture data that writes the minus sign differently.");

            // Fresh options, so that the metadata is made under this culture.
            Vehicle viaReflection = YamlSerializer.Deserialize<Vehicle>("kind: -3\nseats: 3\n", new YamlSerializerOptions())!;
            string reflected = YamlSerializer.Serialize<Vehicle>(new Bus { Seats = 1 }, new YamlSerializerOptions());
            string generated = YamlSerializer.Serialize<Vehicle>(new Bus { Seats = 1 }, (YamlTypeInfo<Vehicle>)s_context.GetTypeInfo(typeof(Vehicle))!);

            Assert.Equal(3, Assert.IsType<Bus>(viaReflection).Seats);
            Assert.Equal(generated, reflected);
            Assert.DoesNotContain('\u2212', reflected);
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    // ---- a tag that is not a derived type ----

    [Theory, MemberData(nameof(Paths))]
    public void An_unknown_tag_is_cut_in_the_message(int path)
    {
        string huge = new('a', 100_000);
        YamlException big = Assert.Throws<YamlException>(() => Read<Drawing>(path, "figures: [!<!" + huge + "> {}]\n"));
        YamlException forged = Assert.Throws<YamlException>(() => Read<Drawing>(path, "figures: [!<!a%0A2026-01-01%20INFO%20forged> {}]\n"));

        Assert.Equal(YamlErrorCategory.Binding, big.Category);
        Assert.True(big.Message.Length < 1_000, $"the message has {big.Message.Length} characters");
        Assert.DoesNotContain('\n', forged.Message);
    }

    // ---- extension data written beside members ----

    [Theory, MemberData(nameof(Paths))]
    public void Extension_data_does_not_repeat_a_key_a_member_or_the_discriminator_writes(int path)
    {
        if (path == 2)
        {
            return;
        }

        var settings = new Settings { Name = "nm", Port = 2 };
        settings.Extra["port"] = 9;
        settings.Extra["other"] = 1;
        string yaml = Write(path, settings);

        Assert.Equal("name: nm\nport: 2\nother: 1\n", yaml);
        Assert.Equal(1L, Read<Settings>(path, yaml)!.Extra["other"]);

        var home = new Home { Pet = new Wild { Name = "w", More = { ["$type"] = "cat", ["name"] = "x", ["z"] = 1 } } };
        string pets = Write(path, home);

        Assert.Equal("pet:\n  $type: wild\n  name: w\n  z: 1\nothers: []\n", pets);
        Assert.Equal(1L, Assert.IsType<Wild>(Read<Home>(path, pets)!.Pet).More["z"]);
    }

    // ---- mistakes in the attributes ----

    [Fact]
    public void Mistakes_in_the_attributes_are_not_supported_errors()
    {
        Assert.Throws<NotSupportedException>(() => YamlSerializer.Deserialize<DupBase>("a: 1\n"));
        Assert.Throws<NotSupportedException>(() => YamlSerializer.Deserialize<TwoExtensions>("a: 1\n"));
        Assert.Throws<NotSupportedException>(() => YamlSerializer.Deserialize<NotAConverter>("a: 1\n"));
    }
}
