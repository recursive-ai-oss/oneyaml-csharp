using System.Text.Json;
using Cs = global::OneYaml.Compat.SharpYaml;
using Rs = global::SharpYaml;

namespace OneYaml.Compat.Tests.SharpYamlCompat;

// The same option set on both sides, so a test says what to configure once.
internal sealed record Setup(string Name, Func<Rs.YamlSerializerOptions> Real, Func<Cs.YamlSerializerOptions> Compat)
{
    internal static readonly Setup Default = new("default", () => new Rs.YamlSerializerOptions(), () => new Cs.YamlSerializerOptions());

    internal static readonly Setup Camel = new(
        "camel",
        () => new Rs.YamlSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase },
        () => new Cs.YamlSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

    internal static readonly Setup CaseInsensitive = new(
        "case-insensitive",
        () => new Rs.YamlSerializerOptions { PropertyNameCaseInsensitive = true },
        () => new Cs.YamlSerializerOptions { PropertyNameCaseInsensitive = true });

    internal static readonly Setup SnakeNoNulls = new(
        "snake-no-nulls",
        () => new Rs.YamlSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = Rs.YamlIgnoreCondition.WhenWritingNull },
        () => new Cs.YamlSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = Cs.YamlIgnoreCondition.WhenWritingNull });

    internal static readonly Setup NoDefaults = new(
        "no-defaults",
        () => new Rs.YamlSerializerOptions { DefaultIgnoreCondition = Rs.YamlIgnoreCondition.WhenWritingDefault },
        () => new Cs.YamlSerializerOptions { DefaultIgnoreCondition = Cs.YamlIgnoreCondition.WhenWritingDefault });

    internal static readonly Setup Strict = new(
        "strict",
        () => new Rs.YamlSerializerOptions { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow },
        () => new Cs.YamlSerializerOptions { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow });

    internal static readonly Setup Indent4 = new(
        "indent-4",
        // The real library writes malformed YAML at an indent other than 2, so its own text is the baseline at the default.
        () => new Rs.YamlSerializerOptions(),
        () => new Cs.YamlSerializerOptions { IndentSize = 4 });

    internal static readonly Setup Sorted = new(
        "sorted",
        () => new Rs.YamlSerializerOptions { MappingOrder = Rs.YamlMappingOrderPolicy.Sorted },
        () => new Cs.YamlSerializerOptions { MappingOrder = Cs.YamlMappingOrderPolicy.Sorted });

    internal static IEnumerable<Setup> All => [Default, Camel, CaseInsensitive, SnakeNoNulls, NoDefaults, Strict, Indent4, Sorted];

    internal Rs.YamlSerializerOptions RealOptions => Real();

    internal Cs.YamlSerializerOptions CompatOptions => Compat();

    public override string ToString() => Name;
}

internal static class SyHelp
{
    // The first line of a failure, which is the class of the error and is what code catches on.
    internal static string Outcome(Func<object?> call) => Differ.Outcome(call);

    internal static string Real<T>(string yaml, Rs.YamlSerializerOptions? options) => Differ.Outcome(() => Rs.YamlSerializer.Deserialize<T>(yaml, options));

    internal static string Compat<T>(string yaml, Cs.YamlSerializerOptions? options) => Differ.Outcome(() => Cs.YamlSerializer.Deserialize<T>(yaml, options));

    // What the real library reads from a text, as an untyped value.
    internal static string RealData(string yaml) => Differ.Outcome(() => Rs.YamlSerializer.Deserialize<object>(yaml));

    internal static Plain Sample() => new()
    {
        Name = "Ada: the \"first\"",
        Age = 37,
        Score = 1.5,
        Active = true,
        Big = 9_000_000_000,
        Money = 12.30m,
        Mood = Mood.VeryHappy,
        Optional = 3,
        Tags = ["a", "b c", "", "true", "123", "null", "- x", "a: b", "# c", " lead", "trail "],
        Counts = new() { ["k"] = 1, ["z z"] = 2 },
        Numbers = [1, 2, 3],
        Child = new Plain { Name = "kid", Age = 3 },
        Children = [new Plain { Name = "one" }, new Plain { Name = "two", Tags = ["t"] }],
        Set = [3, 1, 2],
        IList = [4, 5],
        ReadOnlyList = ["r", "s"],
        IDict = new Dictionary<string, string> { ["x"] = "y" },
        ByMood = new() { [Mood.Sad] = 1, [Mood.Calm] = 2 },
        ByNumber = new() { [1] = "one", [20] = "twenty" },
        Grid = [[1, 2], [3]],
        Stamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc),
        Id = Guid.Parse("00000000-0000-0000-0000-0000000000a1"),
        Span = TimeSpan.FromSeconds(90),
        Day = new DateOnly(2021, 5, 6),
        Letter = 'q',
        Small = 7,
        Single = 0.25f,
        Perms = Perm.Read | Perm.Write,
        Anything = new Dictionary<string, object?> { ["n"] = 1L, ["l"] = new List<object?> { "s", 2.5, null } },
    };
}
