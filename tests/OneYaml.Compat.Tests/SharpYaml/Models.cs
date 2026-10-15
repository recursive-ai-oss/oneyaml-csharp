using System.Text.Json.Serialization;
using Cs = global::OneYaml.Compat.SharpYaml.Serialization;
using Rs = global::SharpYaml.Serialization;

namespace OneYaml.Compat.Tests.SharpYamlCompat;

// The models carry the attributes of both libraries, so one type goes through the real serializer and the compat package.

public enum Mood
{
    Calm,
    VeryHappy,
    Sad,
}

[Flags]
public enum Perm
{
    None = 0,
    Read = 1,
    Write = 2,
}

public class Plain
{
    public string? Name { get; set; }

    public int Age { get; set; }

    public double Score { get; set; }

    public bool Active { get; set; }

    public long Big { get; set; }

    public decimal Money { get; set; }

    public Mood Mood { get; set; }

    public int? Optional { get; set; }

    public string? Missing { get; set; }

    public List<string>? Tags { get; set; }

    public Dictionary<string, int>? Counts { get; set; }

    public int[]? Numbers { get; set; }

    public Plain? Child { get; set; }

    public List<Plain>? Children { get; set; }

    public HashSet<int>? Set { get; set; }

    public IList<int>? IList { get; set; }

    public IReadOnlyList<string>? ReadOnlyList { get; set; }

    public IDictionary<string, string>? IDict { get; set; }

    public Dictionary<Mood, int>? ByMood { get; set; }

    public Dictionary<int, string>? ByNumber { get; set; }

    public List<List<int>>? Grid { get; set; }

    public DateTime Stamp { get; set; }

    public Guid Id { get; set; }

    public TimeSpan Span { get; set; }

    public Uri? Link { get; set; }

    public DateOnly Day { get; set; }

    public char Letter { get; set; }

    public byte Small { get; set; }

    public float Single { get; set; }

    public Perm Perms { get; set; }

    public object? Anything { get; set; }
}

public class Attributed
{
    [Rs.YamlPropertyName("renamed-key")]
    [Cs.YamlPropertyName("renamed-key")]
    public int Renamed { get; set; }

    [JsonPropertyName("json-key")]
    public int ViaJson { get; set; }

    [Rs.YamlIgnore]
    [Cs.YamlIgnore]
    public int Ignored { get; set; }

    [JsonIgnore]
    public int JsonIgnored { get; set; }

    [Rs.YamlIgnore(Condition = global::SharpYaml.YamlIgnoreCondition.WhenWritingNull)]
    [Cs.YamlIgnore(Condition = global::OneYaml.Compat.SharpYaml.YamlIgnoreCondition.WhenWritingNull)]
    public string? SkippedWhenNull { get; set; }

    [Rs.YamlIgnore(Condition = global::SharpYaml.YamlIgnoreCondition.WhenWritingDefault)]
    [Cs.YamlIgnore(Condition = global::OneYaml.Compat.SharpYaml.YamlIgnoreCondition.WhenWritingDefault)]
    public int SkippedWhenDefault { get; set; }

    [Rs.YamlInclude]
    [Cs.YamlInclude]
    private int Hidden { get; set; }

    [Rs.YamlPropertyOrder(2)]
    [Cs.YamlPropertyOrder(2)]
    public int Second { get; set; }

    [Rs.YamlPropertyOrder(1)]
    [Cs.YamlPropertyOrder(1)]
    public int First { get; set; }

    public int PublicField;

    [Rs.YamlInclude]
    [Cs.YamlInclude]
    public int IncludedField;

    public int ReadOnlyComputed => Second + First;

    public int PrivateSet { get; private set; } = 5;

    public int GetHidden() => Hidden;

    public void SetHidden(int value) => Hidden = value;
}

public class Needs
{
    [Rs.YamlRequired]
    [Cs.YamlRequired]
    public int Must { get; set; }

    public int Optional { get; set; }
}

public record Point(int X, int Y);

public record Named(string Name, int Age = 7);

public record WithInit(string Name)
{
    public int Extra { get; init; }
}

public class InitOnly
{
    public int A { get; init; }

    public string? B { get; init; }
}

public class Picky
{
    public Picky(int a, string b)
    {
        A = a;
        B = b;
    }

    public int A { get; }

    public string B { get; }
}

public class Marked
{
    public Marked()
    {
        Source = "default";
    }

    [Rs.YamlConstructor]
    [Cs.YamlConstructor]
    public Marked(string source)
    {
        Source = source;
    }

    public string Source { get; set; }
}

public struct Pair
{
    public int Left { get; set; }

    public int Right { get; set; }
}

public class Inherited : Plain
{
    public string? Extra { get; set; }
}

public class Cycle
{
    public Cycle? Next { get; set; }

    public int Value { get; set; }
}
