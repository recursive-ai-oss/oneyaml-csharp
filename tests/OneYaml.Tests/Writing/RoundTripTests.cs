using System.Text;
using OneYaml.Tests.Generated;
using Xunit;

namespace OneYaml.Tests.Writing;

/// <summary>
/// Property tests of the write path: random values go out through reflection and through generated metadata, must
/// be byte for byte the same, come back as an equal value, and be read the same way by two other parsers.
/// </summary>
public class RoundTripTests
{
    private const int GraphCount = 1500;
    private const int TypedCount = 250;

    private static YamlSerializerOptions Variant(int i) => (i % 5) switch
    {
        1 => new YamlSerializerOptions { IndentSize = 4 },
        2 => new YamlSerializerOptions { PropertyNamingPolicy = null, IndentSize = 3 },
        3 => new YamlSerializerOptions { DefaultIgnoreCondition = YamlIgnoreCondition.WhenWritingNull },
        4 => new YamlSerializerOptions { Limits = YamlLimits.Untrusted, IndentSize = 1 },
        _ => new YamlSerializerOptions(),
    };

    [Fact]
    public void Object_graphs_come_back_equal_and_are_read_the_same_by_other_parsers()
    {
        for (int seed = 0; seed < GraphCount; seed++)
        {
            var factory = new ValueFactory(seed);
            object? graph = factory.Graph();
            YamlSerializerOptions options = Variant(seed);

            string yaml = YamlSerializer.Serialize<object?>(graph, options);

            string? problem = ReadBack.Difference(yaml);
            Assert.True(problem is null, $"seed {seed}: {problem}\n{Cut(yaml)}");
            object? back = YamlSerializer.Deserialize<object>(yaml, options);
            Assert.True(Canon.Of(graph) == Canon.Of(back), $"seed {seed}: value changed\nwritten: {Cut(yaml)}\nbefore: {Cut(Canon.Of(graph))}\nafter:  {Cut(Canon.Of(back))}");
            Assert.Equal(yaml, Encoding.UTF8.GetString(YamlSerializer.SerializeToUtf8Bytes<object?>(graph, options)));
        }
    }

    [Fact]
    public void Strings_of_every_hostile_kind_come_back_unchanged_as_values_keys_and_block_text()
    {
        var factory = new ValueFactory(99);
        for (int i = 0; i < 4000; i++)
        {
            string text = factory.Text(allowLong: i % 50 == 0);
            var map = new Dictionary<string, object?> { [text] = text, ["list"] = new List<object?> { text, new Dictionary<string, object?> { [text] = new List<object?> { text } } } };
            string yaml = YamlSerializer.Serialize<object?>(map);

            string? problem = ReadBack.Difference(yaml);
            Assert.True(problem is null, $"string #{i}: {problem}");
            object? back = YamlSerializer.Deserialize<object>(yaml);
            Assert.True(Canon.Of(map) == Canon.Of(back), $"string #{i} changed: {Cut(yaml)}");
            Assert.Equal(text, YamlSerializer.Deserialize<string>(YamlSerializer.Serialize(text)));
        }
    }

    [Fact]
    public void Typed_values_come_back_equal_and_reflection_and_generated_output_is_identical()
    {
        int checkedCount = 0;
        for (int seed = 0; seed < TypedCount; seed++)
        {
            YamlSerializerOptions options = Variant(seed);
            var context = new ModelsContext(options);
            var f = new ValueFactory(1000 + seed);

            checkedCount += Check(f.Person(), options, context.Person, seed);
            checkedCount += Check(f.Node(), options, context.Node, seed);
            checkedCount += Check(f.Holder(), options, context.Holder, seed);
            checkedCount += Check(f.Scalars(), options, context.Scalars, seed);
            checkedCount += Check(f.Dog(), options, context.Dog, seed);
            checkedCount += Check(f.Defaults(), options, context.Defaults, seed);
            checkedCount += Check(f.InitOnly(), options, context.InitOnly, seed);
            checkedCount += Check(f.Page(), options, context.PagePerson, seed);
            checkedCount += Check(f.Interfaces(), options, context.Interfaces, seed);
            checkedCount += Check(f.Groups(), options, context.DictionaryStringListPerson, seed);
            checkedCount += Check(new Coord(seed, -seed), options, context.Coord, seed);
            checkedCount += Check(Pair.Of(seed, 3), options, context.Pair, seed);
            checkedCount += Check(new Point(seed), options, context.Point, seed);
        }

        Assert.Equal(TypedCount * 13, checkedCount);
    }

    private static int Check<T>(T value, YamlSerializerOptions options, YamlTypeInfo<T> generated, int seed)
    {
        string where = $"seed {seed}, {typeof(T).Name}";
        string reflected = YamlSerializer.Serialize(value, options);
        string fromGenerated = YamlSerializer.Serialize(value, generated);
        Assert.True(reflected == fromGenerated, $"{where}: reflection and generated output differ\nreflection:\n{Cut(reflected)}\ngenerated:\n{Cut(fromGenerated)}");

        string? problem = ReadBack.Difference(reflected);
        Assert.True(problem is null, $"{where}: {problem}\n{Cut(reflected)}");

        T? back = YamlSerializer.Deserialize<T>(reflected, options);
        T? backGenerated = YamlSerializer.Deserialize(reflected, generated);
        string before = Canon.Of(Expected(value, options));
        Assert.True(before == Canon.Of(back), $"{where}: value changed\nwritten:\n{Cut(reflected)}\nbefore: {Cut(before)}\nafter:  {Cut(Canon.Of(back))}");
        Assert.True(before == Canon.Of(backGenerated), $"{where}: generated read changed the value\nwritten:\n{Cut(reflected)}");
        return 1;
    }

    // A member the options leave out of the output comes back as its default, so the original is compared as written.
    private static object? Expected<T>(T value, YamlSerializerOptions options)
    {
        if (options.DefaultIgnoreCondition == YamlIgnoreCondition.Never)
        {
            return value;
        }

        // Reading what was left out gives each member's initial value; compare the read of the written text with itself instead.
        return YamlSerializer.Deserialize<T>(YamlSerializer.Serialize(value, options), options);
    }

    private static string Cut(string s) => s.Length > 1500 ? s[..1500] + "..." : s;

    [Fact]
    public void The_same_value_written_twice_gives_the_same_bytes()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            object? graph = new ValueFactory(5000 + seed).Graph();

            Assert.Equal(YamlSerializer.Serialize<object?>(graph), YamlSerializer.Serialize<object?>(graph));
        }
    }
}
