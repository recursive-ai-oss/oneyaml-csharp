using System.Collections.Immutable;
using System.Numerics;

namespace OneYaml.Fuzzing;

// Models the typed-binding target reads mutated input into, through reflection and through the generated context.
// They cover the shapes the binder treats differently: settable members, constructors, init-only and required
// members, immutable collections, enums, nullable and wide scalars, and nesting.

internal enum Hue
{
    Red,
    DarkGreen,
    Blue,
}

[Flags]
internal enum Mode
{
    None = 0,
    Read = 1,
    Write = 2,
    Exec = 4,
}

internal sealed class Person
{
    public string Name { get; set; } = "";

    public int Age { get; set; }

    public string? Nickname { get; set; }

    public List<string> Tags { get; set; } = [];
}

internal sealed class Tree
{
    public string Name { get; set; } = "";

    public List<Tree> Children { get; set; } = [];

    public Dictionary<string, Tree?> ByName { get; set; } = [];
}

internal sealed class Kinds
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

    public Hue Color { get; set; }

    public Mode Perm { get; set; }

    public int? Maybe { get; set; }

    public object? Anything { get; set; }
}

internal sealed class Collections
{
    public ImmutableArray<int> Arr { get; set; }

    public ImmutableList<string> List { get; set; } = [];

    public ImmutableDictionary<string, int> Dict { get; set; } = ImmutableDictionary<string, int>.Empty;

    public ImmutableHashSet<string> Set { get; set; } = [];

    public ImmutableSortedDictionary<string, int> Sorted { get; set; } = ImmutableSortedDictionary<string, int>.Empty;

    public ImmutableSortedSet<int> SortedSet { get; set; } = [];

    public IReadOnlyList<Person> People { get; set; } = [];

    public Dictionary<Hue, int> ByHue { get; set; } = [];

    public HashSet<int> Numbers { get; set; } = [];

    public int[][] Grid { get; set; } = [];

    public Dictionary<int, List<string>> ByNumber { get; set; } = [];
}

internal sealed record Config(int Replicas, string Script, Hue Tone = Hue.Blue, ImmutableArray<string> Tags = default);

internal sealed record Wire(string Host, int Port = 80, List<string>? Aliases = null, Person? Owner = null);

internal readonly record struct Pt(int X, int Y);

internal struct Loose
{
    public string? S { get; set; }

    public int N;
}

internal sealed class Initial
{
    public required string Kind { get; init; }

    public int Count { get; init; }

    public List<string> Tags { get; init; } = [];

    public Pt Where { get; init; }
}

[YamlSerializable(typeof(Person))]
[YamlSerializable(typeof(Tree))]
[YamlSerializable(typeof(Kinds))]
[YamlSerializable(typeof(Collections))]
[YamlSerializable(typeof(Config))]
[YamlSerializable(typeof(Wire))]
[YamlSerializable(typeof(Pt))]
[YamlSerializable(typeof(Loose))]
[YamlSerializable(typeof(Initial))]
[YamlSerializable(typeof(object))]
[YamlSerializable(typeof(List<Person>))]
[YamlSerializable(typeof(Dictionary<string, object?>))]
internal sealed partial class FuzzContext : YamlSerializerContext
{
}
