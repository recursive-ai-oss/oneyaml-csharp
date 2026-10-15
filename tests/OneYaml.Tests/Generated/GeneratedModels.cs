using System.Text.Json.Serialization;

namespace OneYaml.Tests.Generated;

// Shapes beyond the binder tests' own models: init-only and required members, positional structs,
// inheritance, closed generics, defaults and the less common collection types.

internal sealed class InitOnly
{
    public required string Kind { get; init; }

    public int Count { get; init; }

    public List<string> Tags { get; init; } = [];

    public string? Note { get; set; }
}

// An init-only member declared on a base class closed over a type argument, and on a generic struct.
internal class GenericInitBase<T>
{
    public T? Value { get; init; }

    public string? Label { get; init; }
}

internal sealed class GenericInitDerived : GenericInitBase<int>
{
    public string? Extra { get; set; }
}

internal struct GenericInitStruct<T>
{
    public T? Item { get; init; }
}

// Only some keys are members; the others are skipped while reading.
internal sealed class FewMembers
{
    public string? Keep { get; set; }

    [YamlIgnore]
    public string? Hidden { get; set; }
}

internal struct InitStruct
{
    public string Name { get; init; }

    public int Size { get; set; }
}

internal readonly record struct Coord(int X, int Y);

internal sealed record Defaults(
    string Name,
    Color Tone = Color.Blue,
    double Ratio = 0.5,
    long Big = 5,
    char Letter = 'q',
    bool On = true,
    int? Opt = null,
    Guid Id = default,
    byte Small = 3,
    string? Label = "none");

internal class Animal
{
    public string Name { get; set; } = "";

    public virtual int Legs { get; set; } = 4;

    public string Shadowed { get; set; } = "base";

    public int Weight;
}

internal sealed class Dog : Animal
{
    [YamlPropertyName("good-boy")]
    public bool GoodBoy { get; set; }

    public new string Shadowed { get; set; } = "derived";

    public override int Legs { get; set; } = 4;

    [YamlIgnore]
    public int Secret { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Kept { get; set; }
}

internal sealed class Page<T>
{
    public int Number { get; set; }

    public List<T> Items { get; set; } = [];
}

internal sealed class Ordered
{
    [YamlPropertyOrder(2)]
    public int Second { get; set; }

    [JsonPropertyOrder(1)]
    public int First { get; set; }

    public int ReadOnlyValue { get; } = 9;

    public int WriteOnly { set => Captured = value; }

    public int Captured { get; private set; }
}

internal sealed class Words : List<string>
{
}

internal sealed class Counts : Dictionary<string, int>
{
}

internal sealed class Interfaces
{
    public IEnumerable<int> Numbers { get; set; } = [];

    public ISet<string> Names { get; set; } = new HashSet<string>();

    public IReadOnlySet<int> Unique { get; set; } = new HashSet<int>();

    public IReadOnlyCollection<string> Items { get; set; } = [];

    public IList<Person> People { get; set; } = [];

    public Words Words { get; set; } = [];

    public Counts Counts { get; set; } = [];

    public Dictionary<string, List<Person>> Groups { get; set; } = [];

    public Color[] Colors { get; set; } = [];

    public Perm? MaybePerm { get; set; }

    public Coord? MaybeCoord { get; set; }

    public int[][] Grid { get; set; } = [];

    public Dictionary<Color, int> ByColor { get; set; } = [];
}

public sealed class PublicModel
{
    public string Title { get; set; } = "";

    public List<PublicModel> Children { get; set; } = [];

    public Dictionary<string, PublicModel?> ByName { get; set; } = [];
}

internal sealed class Nested
{
    public Inner Value { get; set; } = new();

    public sealed class Inner
    {
        public int Depth { get; set; }
    }
}
