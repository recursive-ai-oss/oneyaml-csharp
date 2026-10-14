using Xunit;

namespace OneYaml.Tests;

// What the source generator is expected to emit for a record and a class, written by hand: metadata built
// only through YamlMetadataServices, with no reflection.
internal sealed class HandWrittenContext : YamlSerializerContext
{
    private YamlTypeInfo<int>? _int;
    private YamlTypeInfo<string>? _string;
    private YamlTypeInfo<List<string>>? _strings;
    private YamlTypeInfo<Config>? _config;
    private YamlTypeInfo<Person>? _person;

    public HandWrittenContext()
        : base(null)
    {
    }

    public YamlTypeInfo<int> Int32 => _int ??= YamlMetadataServices.CreateValueInfo(Options, YamlMetadataServices.GetBuiltInConverter<int>(Options));

    public YamlTypeInfo<string> String => _string ??= YamlMetadataServices.CreateValueInfo(Options, YamlMetadataServices.GetBuiltInConverter<string>(Options));

    public YamlTypeInfo<List<string>> ListString => _strings ??= YamlMetadataServices.CreateListInfo(Options, String);

    public YamlTypeInfo<Config> Config => _config ??= YamlMetadataServices.CreateObjectInfo(Options, new YamlObjectInfoValues<Config>
    {
        ConstructorCreator = args => new Config(args[0] is int replicas ? replicas : default, (string)args[1]!),
        ConstructorParameterMetadataInitializer = () =>
        [
            new YamlParameterInfo("Replicas", typeof(int)),
            new YamlParameterInfo("Script", typeof(string)),
        ],
        PropertyMetadataInitializer = _ =>
        [
            YamlMetadataServices.CreatePropertyInfo(new YamlPropertyInfoValues<Config, int> { Name = "Replicas", PropertyTypeInfo = Int32 }),
            YamlMetadataServices.CreatePropertyInfo(new YamlPropertyInfoValues<Config, string> { Name = "Script", PropertyTypeInfo = String }),
        ],
    });

    public YamlTypeInfo<Person> Person => _person ??= YamlMetadataServices.CreateObjectInfo(Options, new YamlObjectInfoValues<Person>
    {
        ObjectCreator = static () => new Person(),
        PropertyMetadataInitializer = _ =>
        [
            YamlMetadataServices.CreatePropertyInfo(new YamlPropertyInfoValues<Person, string>
            {
                Name = "Name",
                PropertyTypeInfo = String,
                Setter = static (o, v) => o.Name = v,
            }),
            YamlMetadataServices.CreatePropertyInfo(new YamlPropertyInfoValues<Person, int>
            {
                Name = "Age",
                PropertyTypeInfo = Int32,
                Setter = static (o, v) => o.Age = v,
            }),
            YamlMetadataServices.CreatePropertyInfo(new YamlPropertyInfoValues<Person, List<string>>
            {
                Name = "Tags",
                PropertyTypeInfo = ListString,
                Setter = static (o, v) => o.Tags = v,
            }),
        ],
    });

    public override YamlTypeInfo? GetTypeInfo(Type type) =>
        type == typeof(Config) ? Config
        : type == typeof(Person) ? Person
        : type == typeof(int) ? Int32
        : type == typeof(string) ? String
        : type == typeof(List<string>) ? ListString
        : null;
}

public class ContextTests
{
    [Fact]
    public void Generated_style_metadata_binds_a_record_without_reflection()
    {
        var context = new HandWrittenContext();

        Config? cfg = YamlSerializer.Deserialize("replicas: 3\nscript: make\nignored: 1\n", context.Config);

        Assert.Equal(new Config(3, "make"), cfg);
    }

    [Fact]
    public void Generated_style_metadata_binds_a_class_and_reports_errors_with_positions()
    {
        var context = new HandWrittenContext();

        Person? p = YamlSerializer.Deserialize("name: Ada\nage: 36\ntags: [a]\n", context.Person);
        YamlException ex = Assert.Throws<YamlException>(() => YamlSerializer.Deserialize("name: Ada\nage: old\n", context.Person));

        Assert.Equal(("Ada", 36, 1), (p!.Name, p.Age, p.Tags.Count));
        Assert.Equal(YamlErrorCategory.Binding, ex.Category);
        Assert.Equal(2, ex.Line);
        Assert.Equal(6, ex.Column);
    }

    [Fact]
    public void A_type_the_context_does_not_know_is_not_supported()
    {
        var context = new HandWrittenContext();

        Type unknown = typeof(Scalars);

        Assert.Throws<NotSupportedException>(() => context.Options.GetTypeInfo(unknown));
    }
}
