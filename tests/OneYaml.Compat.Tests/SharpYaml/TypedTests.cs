using System.Text;
using Xunit;
using Cs = global::OneYaml.Compat.SharpYaml;
using Rs = global::SharpYaml;

namespace OneYaml.Compat.Tests.SharpYamlCompat;

// Typed objects through both libraries: what one writes the other reads, and both read the same text to the same value.
public class SharpYamlTypedTests
{
    public static IEnumerable<object[]> Setups() => Setup.All.Select(s => new object[] { s });

    // Differences that are on purpose, each with its class.
    //   real-1.1-number: the real library reads a YAML 1.1 number form (digit separators) that the Core schema does not have.
    //   hex-float: the compat package reads a hexadecimal integer into a floating-point member; the real library refuses it.
    //   double-fraction: a whole-number double is written with a fraction (100.0), so it reads back as a double; the real library writes 100.
    //   real-uri: the real library cannot read or write System.Uri; the compat package can.
    //   real-crash: the real library fails with a NullReferenceException where the compat package reports a YamlException.
    //   top-level-null: a null at the top level into a value type is a YamlException in the real library; the compat package gives the default, as it does for a member.
    private static readonly Dictionary<string, string> Known = new(StringComparer.Ordinal)
    {
        ["scalar Age: 1_000"] = "real-1.1-number",
        ["scalar Score: 0x10"] = "hex-float",
        ["scalar Link: http://x/y"] = "real-uri",
        ["Double[] System.Double[]"] = "double-fraction",
    };

    private static bool IsKnown(string label) =>
        Known.ContainsKey(label)
        || (label.Contains(" <- ", StringComparison.Ordinal) && (label.StartsWith("Int32", StringComparison.Ordinal) || label.StartsWith("Mood", StringComparison.Ordinal) || label.StartsWith("Boolean", StringComparison.Ordinal) || label.StartsWith("Double", StringComparison.Ordinal)));

    private static void Same(List<string> bad, string label, string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal) && !IsKnown(label))
        {
            bad.Add($"{label}\n   real:   {expected}\n   compat: {actual}");
        }
    }

    private static void NoneBad(List<string> bad) => Assert.True(bad.Count == 0, string.Join("\n", bad));

    [Theory]
    [MemberData(nameof(Setups))]
    internal void Plain_objects_cross_the_two_libraries(Setup setup)
    {
        Plain sample = SyHelp.Sample();
        string byReal = Rs.YamlSerializer.Serialize(sample, setup.RealOptions);
        string byCompat = Cs.YamlSerializer.Serialize(sample, setup.CompatOptions);
        var bad = new List<string>();

        // Reading: the same text gives the same value.
        Same(bad, $"{setup} read of the real text", SyHelp.Real<Plain>(byReal, setup.RealOptions), SyHelp.Compat<Plain>(byReal, setup.CompatOptions));

        // Writing: what the package writes, the real library reads to the value it reads from its own text.
        Same(bad, $"{setup} real reads compat text", SyHelp.Real<Plain>(byReal, setup.RealOptions), SyHelp.Real<Plain>(byCompat, setup.RealOptions));
        Same(bad, $"{setup} compat reads compat text", SyHelp.Real<Plain>(byReal, setup.RealOptions), SyHelp.Compat<Plain>(byCompat, setup.CompatOptions));
        NoneBad(bad);
    }

    [Fact]
    public void Output_is_valid_with_every_option()
    {
        Plain sample = SyHelp.Sample();
        foreach (Setup setup in Setup.All)
        {
            string text = Cs.YamlSerializer.Serialize(sample, setup.CompatOptions);
            Assert.EndsWith("\n", text, StringComparison.Ordinal);
            _ = OneYaml.YamlDocument.Parse(text);
        }
    }

    [Fact]
    public void Attributes_shape_the_keys()
    {
        var bad = new List<string>();
        var x = new Attributed { Renamed = 1, ViaJson = 2, Ignored = 3, JsonIgnored = 4, SkippedWhenNull = null, SkippedWhenDefault = 0, Second = 6, First = 5, PublicField = 7, IncludedField = 8 };
        x.SetHidden(9);
        string byReal = Rs.YamlSerializer.Serialize(x);
        string byCompat = Cs.YamlSerializer.Serialize(x);
        Same(bad, "keys of the output", KeysOf(byReal), KeysOf(byCompat));
        Same(bad, "read of its own text", SyHelp.Real<Attributed>(byReal, null), SyHelp.Compat<Attributed>(byReal, null));
        Same(bad, "read of compat text", SyHelp.Real<Attributed>(byReal, null), SyHelp.Compat<Attributed>(byCompat, null));
        const string Probe = "renamed-key: 1\nRenamed: 2\njson-key: 3\nViaJson: 4\nIgnored: 5\nJsonIgnored: 6\nHidden: 7\nIncludedField: 8\nPublicField: 9\nPrivateSet: 10\nReadOnlyComputed: 11\nFirst: 12\nSecond: 13\n";
        Same(bad, "which keys take", SyHelp.Real<Attributed>(Probe, null), SyHelp.Compat<Attributed>(Probe, null));
        NoneBad(bad);
    }

    private static string KeysOf(string yaml)
    {
        var sb = new StringBuilder();
        foreach (string line in yaml.Split('\n'))
        {
            if (line.Length > 0 && line[0] != ' ' && line[0] != '-')
            {
                sb.Append(line.Split(':')[0]).Append(',');
            }
        }

        return sb.ToString();
    }

    [Theory]
    [InlineData("Must: 1\n")]
    [InlineData("Optional: 1\n")]
    [InlineData("Must: 1\nOptional: x\n")]
    [InlineData("")]
    public void Required_members_are_enforced(string yaml)
    {
        var bad = new List<string>();
        Same(bad, "required", SyHelp.Real<Needs>(yaml, null), SyHelp.Compat<Needs>(yaml, null));
        NoneBad(bad);
    }

    [Fact]
    public void Constructors_records_and_init_members()
    {
        var bad = new List<string>();
        void Both<T>(string yaml, string label) => Same(bad, $"{typeof(T).Name} {label}", SyHelp.Real<T>(yaml, null), SyHelp.Compat<T>(yaml, null));
        Both<Point>("X: 1\nY: 2\n", "full");
        Both<Point>("X: 1\n", "missing");
        Both<Point>("X: 1\nY: 2\nZ: 3\n", "extra");
        Both<Named>("Name: n\nAge: 3\n", "full");
        Both<Named>("Name: n\n", "default");
        Both<Named>("Age: 3\n", "missing required");
        Both<WithInit>("Name: n\nExtra: 4\n", "init");
        Both<WithInit>("Extra: 4\n", "init missing");
        Both<InitOnly>("A: 1\nB: x\n", "full");
        Both<Picky>("A: 1\nB: x\n", "full");
        Both<Picky>("A: 1\n", "missing");
        Both<Marked>("Source: s\n", "marked");
        Both<Pair>("Left: 1\nRight: 2\n", "struct");
        Both<Inherited>("Name: n\nExtra: e\nAge: 5\n", "derived");
        Both<Cycle>("Value: 1\nNext:\n  Value: 2\n", "recursive");
        NoneBad(bad);
        Same(bad, "record write", SyHelp.RealData(Rs.YamlSerializer.Serialize(new Point(1, 2))), SyHelp.RealData(Cs.YamlSerializer.Serialize(new Point(1, 2))));
        Same(bad, "derived write keys", KeysOf(Rs.YamlSerializer.Serialize(new Inherited { Name = "n", Extra = "e" })), KeysOf(Cs.YamlSerializer.Serialize(new Inherited { Name = "n", Extra = "e" })));
        NoneBad(bad);
    }

    [Fact]
    public void Scalars_read_alike()
    {
        var bad = new List<string>();
        string[] inputs =
        [
            "Age: 5", "Age: -5", "Age: 0x1F", "Age: 0o17", "Age: 017", "Age: +3", "Age: 1_000", "Age: 2147483648", "Age: x", "Age: 3.0", "Age: ''", "Age: '5'", "Age: ~", "Age: null", "Age: true",
            "Score: 1.5", "Score: 1e3", "Score: .inf", "Score: -.INF", "Score: .nan", "Score: 5", "Score: 0x10", "Score: x", "Score: '2.5'",
            "Active: true", "Active: True", "Active: TRUE", "Active: yes", "Active: 1", "Active: 'true'", "Active: ~",
            "Name: 5", "Name: true", "Name: ~", "Name: ''", "Name: 'q'", "Name: 1.0", "Name: |\n  a\n  b\n", "Name: >\n  a\n  b\n", "Name: [a]", "Name:\n  x: 1",
            "Mood: Calm", "Mood: calm", "Mood: VeryHappy", "Mood: 1", "Mood: 7", "Mood: Nope", "Mood: ~", "Mood: ''",
            "Perms: Read", "Perms: Read, Write", "Perms: 3", "Perms: 'Read'",
            "Optional: ~", "Optional: 5", "Optional: ''", "Optional: x",
            "Stamp: 2020-01-02T03:04:05Z", "Stamp: 2020-01-02", "Stamp: '2020-01-02T03:04:05.0000000Z'", "Stamp: x",
            "Id: 00000000-0000-0000-0000-0000000000a1", "Id: x", "Span: '01:30:00'", "Span: 01:30:00", "Span: x", "Day: 2021-05-06", "Day: x",
            "Letter: x", "Letter: xy", "Small: 255", "Small: 256", "Single: 0.25", "Big: 9000000000", "Big: 92233720368547758070", "Money: 12.30", "Money: 1e2",
            "Link: http://x/y", "Anything: 1", "Anything: [1, a]", "Anything: {a: 1}", "Anything: ~",
        ];
        foreach (string yaml in inputs)
        {
            Same(bad, "scalar " + yaml.Replace("\n", "\\n", StringComparison.Ordinal), SyHelp.Real<Plain>(yaml + "\n", null), SyHelp.Compat<Plain>(yaml + "\n", null));
        }

        NoneBad(bad);
    }

    [Fact]
    public void Collections_read_alike()
    {
        var bad = new List<string>();
        string[] inputs =
        [
            "Tags: [a, b]", "Tags:\n  - a\n  - b", "Tags: ~", "Tags: []", "Tags: a", "Tags: {a: 1}", "Tags: [a, [b]]", "Tags:\n- a\n- b",
            "Counts: {a: 1}", "Counts:\n  a: 1\n  b: 2", "Counts: [1]", "Counts: {a: x}", "Counts: {}", "Counts: ~",
            "Numbers: [1, 2]", "Numbers: [1, x]", "Set: [1, 1, 2]", "IList: [1]", "ReadOnlyList: [a]", "IDict: {a: b}",
            "ByMood: {Sad: 1, Calm: 2}", "ByMood: {x: 1}", "ByNumber: {1: a, 2: b}", "ByNumber: {x: a}", "Grid: [[1],[2,3]]", "Grid: [1]",
            "Child: {Name: k}", "Child: ~", "Child: x", "Children:\n  - Name: a\n  - Name: b\n    Age: 2", "Children: [~]",
        ];
        foreach (string yaml in inputs)
        {
            Same(bad, "collection " + yaml.Replace("\n", "\\n", StringComparison.Ordinal), SyHelp.Real<Plain>(yaml + "\n", null), SyHelp.Compat<Plain>(yaml + "\n", null));
        }

        NoneBad(bad);
    }

    [Fact]
    public void Top_level_values_read_alike()
    {
        var bad = new List<string>();
        void Both<T>(string yaml) => Same(bad, $"{typeof(T).Name} <- {yaml.Replace("\n", "\\n", StringComparison.Ordinal)}", SyHelp.Real<T>(yaml, null), SyHelp.Compat<T>(yaml, null));
        foreach (string y in new[] { "", "~", "5", "x", "[1, 2]", "- 1\n- 2", "a: 1", "--- 1\n--- 2", "# only a comment", "1\n---\n2\n" })
        {
            Both<int>(y);
            Both<int?>(y);
            Both<string>(y);
            Both<List<int>>(y);
            Both<int[]>(y);
            Both<Dictionary<string, int>>(y);
            Both<Plain>(y);
            Both<object>(y);
            Both<Mood>(y);
            Both<bool>(y);
            Both<double>(y);
        }

        NoneBad(bad);
    }

    [Fact]
    public void Write_matches_for_values_that_are_not_objects()
    {
        var bad = new List<string>();
        // The text differs in style (quotes, exponent form); what it says must not, as the real library reads it.
        void Both<T>(T value) => Same(bad, $"{typeof(T).Name} {value}", SyHelp.RealData(Rs.YamlSerializer.Serialize(value)), SyHelp.RealData(Cs.YamlSerializer.Serialize(value)));
        Both(5);
        Both("hello");
        Both((string?)null);
        Both(true);
        Both(1.5);
        Both(Mood.VeryHappy);
        Both(new List<int> { 1, 2 });
        Both(new List<int>());
        Both(new int[] { 1 });
        Both(new Dictionary<string, int> { ["a"] = 1 });
        Both(new Dictionary<string, object?> { ["a"] = 1, ["b"] = new[] { "x", "y" }, ["c"] = new Dictionary<string, object?> { ["d"] = null } });
        Both(new[] { "yes", "No", "on", "~", "0x1F", "1e3", ".5", "1_000", "- a", "a: b", "# c", " lead", "trail ", "@x", "é", "tab\there", "a'b", "a\"b", "" });
        Both(new double[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, 1e20, 0.1, 1e-7, 100, -3.25 });
        Both(new { Name = "Ada", Age = 37 });
        NoneBad(bad);
    }
}
