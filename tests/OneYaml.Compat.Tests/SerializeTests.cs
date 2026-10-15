using System.Text;
using Xunit;
using Cm = global::OneYaml.Compat.YamlDotNet.Serialization;
using CmCore = global::OneYaml.Compat.YamlDotNet.Core;
using Rl = global::YamlDotNet.Serialization;
using RlCore = global::YamlDotNet.Core;

namespace OneYaml.Compat.Tests;

public class SerializeTests
{
    private enum Mode { Plain, Indented, OmitNull, OmitDefaults, OmitEmpty, Json, Quoting, DoubleQuoted, NoAliases }

    private static (Rl.ISerializer, Cm.ISerializer) For(Mode mode) => mode switch
    {
        Mode.Indented => Both.Serializers(r => r.WithIndentedSequences(), c => c.WithIndentedSequences()),
        Mode.OmitNull => Both.Serializers(r => r.ConfigureDefaultValuesHandling(Rl.DefaultValuesHandling.OmitNull), c => c.ConfigureDefaultValuesHandling(Cm.DefaultValuesHandling.OmitNull)),
        Mode.OmitDefaults => Both.Serializers(r => r.ConfigureDefaultValuesHandling(Rl.DefaultValuesHandling.OmitDefaults), c => c.ConfigureDefaultValuesHandling(Cm.DefaultValuesHandling.OmitDefaults)),
        Mode.OmitEmpty => Both.Serializers(r => r.ConfigureDefaultValuesHandling(Rl.DefaultValuesHandling.OmitEmptyCollections), c => c.ConfigureDefaultValuesHandling(Cm.DefaultValuesHandling.OmitEmptyCollections)),
        Mode.Json => Both.Serializers(r => r.JsonCompatible(), c => c.JsonCompatible()),
        Mode.Quoting => Both.Serializers(r => r.WithQuotingNecessaryStrings(), c => c.WithQuotingNecessaryStrings()),
        Mode.DoubleQuoted => Both.Serializers(r => r.WithDefaultScalarStyle(RlCore.ScalarStyle.DoubleQuoted), c => c.WithDefaultScalarStyle(CmCore.ScalarStyle.DoubleQuoted)),
        Mode.NoAliases => Both.Serializers(r => r.DisableAliases(), c => c.DisableAliases()),
        _ => Both.Serializers(),
    };

    private static Person Sample() => new()
    {
        Name = "Ada",
        Age = 36,
        Tags = ["a", "b"],
        Nested = new Person { Name = "Bo", Age = 2 },
        Fld = 7,
        Score = 1.5,
        Active = true,
        Favourite = Color.DarkBlue,
        Optional = 3,
        Money = 12.50m,
        Counts = new Dictionary<string, int> { ["x"] = 1, ["z"] = 2 },
        Numbers = [1, 2, 3],
    };

    private static IEnumerable<(string Label, object? Value, Mode Mode)> Cases()
    {
        foreach (Mode mode in Enum.GetValues<Mode>())
        {
            yield return ("person/" + mode, Sample(), mode);
            yield return ("holder/" + mode, new Holder { Items = ["a", "b"], Bag = new Dictionary<string, object> { ["k"] = 1, ["j"] = new List<object> { "x", 2 } }, Numbers = [1, 2], Set = ["q"], Anything = new Dictionary<string, object> { ["a"] = new List<int> { 1 } }, People = [new Person { Name = "p" }], Lists = new Dictionary<string, List<int>> { ["a"] = [1, 2] } }, mode);
            yield return ("attributed/" + mode, new Attributed { Plain = 1, Aliased = 2, First = 3, Hidden = 4, Quoted = "q", Single = "s", Block = "line1\nline2", OmittedWhenZero = 0, WithDefault = 5 }, mode);
            yield return ("derived/" + mode, new Derived { BaseValue = "b", DerivedValue = 2 }, mode);
        }

        yield return ("string", "hello", Mode.Plain);
        yield return ("int", 5, Mode.Plain);
        yield return ("null", null, Mode.Plain);
        yield return ("true", true, Mode.Plain);
        yield return ("double 1", 1.0, Mode.Plain);
        yield return ("double small", 1.5e-7, Mode.Plain);
        yield return ("double big", 1e20, Mode.Plain);
        yield return ("inf", double.PositiveInfinity, Mode.Plain);
        yield return ("nan", double.NaN, Mode.Plain);
        yield return ("float", 1.1f, Mode.Plain);
        yield return ("decimal", 1.10m, Mode.Plain);
        yield return ("long", long.MaxValue, Mode.Plain);
        yield return ("ulong", ulong.MaxValue, Mode.Plain);
        yield return ("empty string", string.Empty, Mode.Plain);
        yield return ("colon", "a: b", Mode.Plain);
        yield return ("hash", "a #b", Mode.Plain);
        yield return ("lead space", " a", Mode.Plain);
        yield return ("trail space", "a ", Mode.Plain);
        yield return ("multi", "a\nb", Mode.Plain);
        yield return ("multi trailing", "a\nb\n", Mode.Plain);
        yield return ("unicode", "h\u00e9llo \u2603", Mode.Plain);
        yield return ("emoji", "x \ud83d\ude00", Mode.Plain);
        yield return ("control", "a\tb\u0001", Mode.Plain);
        yield return ("dash", "- a", Mode.Plain);
        yield return ("apostrophe", "it's", Mode.Plain);
        yield return ("dquote", "say \"x\"", Mode.Plain);
        yield return ("star", "*a", Mode.Plain);
        yield return ("percent", "%a", Mode.Plain);
        yield return ("datetime", new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc), Mode.Plain);
        yield return ("guid", Guid.Empty, Mode.Plain);
        yield return ("timespan", TimeSpan.FromSeconds(90), Mode.Plain);
        yield return ("enum", Color.DarkBlue, Mode.Plain);
        yield return ("char", 'c', Mode.Plain);
        yield return ("uri", new Uri("http://a/b"), Mode.Plain);
        yield return ("bytes", new byte[] { 1, 2, 3 }, Mode.Plain);
        yield return ("empty list", new List<int>(), Mode.Plain);
        yield return ("empty dict", new Dictionary<string, int>(), Mode.Plain);
        yield return ("list mix", new List<object?> { 1, "a", null, true, new List<int> { 1, 2 }, new Dictionary<string, object> { ["k"] = 1 } }, Mode.Plain);
        yield return ("dict mix", new Dictionary<string, object> { ["b"] = 1, ["a"] = new List<int> { 1 }, ["c"] = new Dictionary<string, int> { ["x"] = 1 }, ["d"] = new List<object>() }, Mode.Plain);
        yield return ("int keys", new Dictionary<int, string> { [1] = "a", [2] = "b" }, Mode.Plain);
        yield return ("nested seq", new int[][] { [1], [2, 3] }, Mode.Plain);
        yield return ("anonymous", new { A = 1, B = "x" }, Mode.Plain);
        yield return ("kvp", new KeyValuePair<string, int>("a", 1), Mode.Plain);
        yield return ("point", new Point { X = 1, Y = 2 }, Mode.Plain);
        yield return ("hashset", new HashSet<int> { 1, 2 }, Mode.Plain);
        var shared = new List<int> { 1 };
        yield return ("alias", new Dictionary<string, object> { ["a"] = shared, ["b"] = shared }, Mode.Plain);
        yield return ("alias off", new Dictionary<string, object> { ["a"] = shared, ["b"] = shared }, Mode.NoAliases);
        var cycle = new Dictionary<string, object>();
        cycle["self"] = cycle;
        yield return ("cycle", cycle, Mode.Plain);
        yield return ("strings json", new List<string> { "a", "b" }, Mode.Json);
        yield return ("string dq", "hello", Mode.DoubleQuoted);
    }

    // The known ways the text differs, each reading back as the same value. A difference outside these fails the test,
    // and a class that no longer occurs fails it too, so the list stays true.
    private static readonly (string Name, Func<string, string, string, bool> Matches)[] Classes =
    [
        // Characters above U+FFFF are escaped by YamlDotNet and written as they are here.
        ("non-BMP characters are written raw", (label, a, b) => label == "emoji"),
        // A tab inside double quotes is written as \t here and raw by YamlDotNet.
        ("tab escaped in double quotes", (label, a, b) => label == "control"),
        // Y and N read as booleans in YAML 1.1; they are quoted here and plain in YamlDotNet.
        ("YAML 1.1 booleans are quoted", (label, a, b) => label == "point" && a.Replace("Y:", "'Y':") == b),
        // YamlDotNet drops empty strings when empty collections are omitted, because a string is an enumerable of characters.
        ("empty strings are kept when omitting empty collections", (label, a, b) => label.EndsWith("/OmitEmpty", StringComparison.Ordinal) && DropEmptyStrings(b) == a),
    ];

    private static string DropEmptyStrings(string text) =>
        string.Join("\n", text.Split('\n').Where(l => !(l.TrimStart().StartsWith("FirstName: ''", StringComparison.Ordinal) || l.TrimStart().StartsWith("HTTPServerName: ''", StringComparison.Ordinal))));

    [Fact]
    public void Output_matches_the_real_serializer_text()
    {
        var diffs = new StringBuilder();
        var seen = new HashSet<string>();
        foreach ((string label, object? value, Mode mode) in Cases())
        {
            var (real, compat) = For(mode);
            string a = Run(() => real.Serialize(value!));
            string b = Run(() => compat.Serialize(value!));
            if (a == b)
            {
                continue;
            }

            var known = Classes.FirstOrDefault(c => c.Matches(label, a, b));
            if (known.Name is null)
            {
                diffs.AppendLine($"## {label}\n--real--\n{a}\n--compat--\n{b}");
            }
            else
            {
                seen.Add(known.Name);
            }
        }

        foreach (var c in Classes)
        {
            Assert.True(seen.Contains(c.Name), "The difference class no longer occurs: " + c.Name);
        }

        Assert.True(diffs.Length == 0, diffs.ToString());
    }

    private static string Run(Func<string> f)
    {
        try
        {
            return f().Replace(" \n", "~\n");
        }
        catch (Exception e)
        {
            return "EX " + Differ.Kind(e);
        }
    }
}
