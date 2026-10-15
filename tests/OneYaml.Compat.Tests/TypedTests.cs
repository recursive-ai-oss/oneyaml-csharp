using System.Text;
using Xunit;
using Cm = global::OneYaml.Compat.YamlDotNet.Serialization;
using CmNaming = global::OneYaml.Compat.YamlDotNet.Serialization.NamingConventions;
using Rl = global::YamlDotNet.Serialization;
using RlNaming = global::YamlDotNet.Serialization.NamingConventions;

namespace OneYaml.Compat.Tests;

public class TypedTests
{
    private const string PersonYaml = """
        Name: Ada
        Age: 36
        Tags: [a, b]
        Nested:
          Name: Bo
          Age: 2
        Fld: 7
        Score: 1.5
        Active: yes
        Favourite: DarkBlue
        Optional: 3
        Money: 12.50
        Counts: {x: 1, y: 2}
        Numbers: [1, 2, 3]
        FirstName: Ada
        HTTPServerName: srv
        """;

    public static IEnumerable<object[]> Conventions() =>
    [
        ["null"], ["camel"], ["pascal"], ["underscored"], ["hyphenated"], ["lower"],
    ];

    private static (Rl.INamingConvention, Cm.INamingConvention) Pair(string name) => name switch
    {
        "camel" => (RlNaming.CamelCaseNamingConvention.Instance, CmNaming.CamelCaseNamingConvention.Instance),
        "pascal" => (RlNaming.PascalCaseNamingConvention.Instance, CmNaming.PascalCaseNamingConvention.Instance),
        "underscored" => (RlNaming.UnderscoredNamingConvention.Instance, CmNaming.UnderscoredNamingConvention.Instance),
        "hyphenated" => (RlNaming.HyphenatedNamingConvention.Instance, CmNaming.HyphenatedNamingConvention.Instance),
        "lower" => (RlNaming.LowerCaseNamingConvention.Instance, CmNaming.LowerCaseNamingConvention.Instance),
        _ => (RlNaming.NullNamingConvention.Instance, CmNaming.NullNamingConvention.Instance),
    };

    private static Person SamplePerson() => new()
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
        Counts = new Dictionary<string, int> { ["x"] = 1, ["yy"] = 2 },
        Numbers = [1, 2, 3],
        FirstName = "Ada",
        HTTPServerName = "srv",
    };

    [Theory]
    [MemberData(nameof(Conventions))]
    public void Naming_convention_round_trips_like_the_real_one(string convention)
    {
        var (rn, cn) = Pair(convention);
        var (rs, cs) = Both.Serializers(r => r.WithNamingConvention(rn), c => c.WithNamingConvention(cn));
        var (rd, cd) = Both.Deserializers(r => r.WithNamingConvention(rn), c => c.WithNamingConvention(cn));
        Person p = SamplePerson();
        string real = rs.Serialize(p);
        string compat = cs.Serialize(p);
        Assert.Equal(real, compat);

        // Both read the real library's text to the same value, under the same convention.
        string a = Differ.Dump(rd.Deserialize<Person>(real));
        string b = Differ.Dump(cd.Deserialize<Person>(real));
        Assert.Equal(a, b);
    }

    [Fact]
    public void Person_reads_the_same()
    {
        var (rd, cd) = Both.Deserializers();
        Assert.Equal(Differ.Dump(rd.Deserialize<Person>(PersonYaml)), Differ.Dump(cd.Deserialize<Person>(PersonYaml)));
    }

    [Fact]
    public void Structure_cases_read_the_same()
    {
        var (rd, cd) = Both.Deserializers();
        var diffs = new StringBuilder();
        var cases = new (string Yaml, Type Type)[]
        {
            ("Name: x\nZip: 1", typeof(Person)),
            ("name: x", typeof(Person)),
            ("Name: x\nName: y", typeof(Person)),
            ("- a", typeof(Person)),
            ("abc", typeof(Person)),
            ("null", typeof(Person)),
            ("~", typeof(Person)),
            ("", typeof(Person)),
            ("Name:\nAge:", typeof(Person)),
            ("Age: null", typeof(Person)),
            ("Tags: ~", typeof(Person)),
            ("Tags: a", typeof(Person)),
            ("Counts: [1]", typeof(Person)),
            ("Numbers: {a: 1}", typeof(Person)),
            ("Favourite: red", typeof(Person)),
            ("Favourite: 2", typeof(Person)),
            ("Favourite: Purple", typeof(Person)),
            ("Optional: ~", typeof(Person)),
            ("BaseValue: x\nDerivedValue: 3", typeof(Derived)),
            ("Plain: 1\nrenamed: 2\nFirst: 3\nQuoted: q\nHidden: 5", typeof(Attributed)),
            ("Plain: 1\nAliased: 2", typeof(Attributed)),
            ("Items: [a, b]\nBag: {k: 1, j: [x]}\nNumbers: [1, 2]\nSet: [a, a, b]\nAnything: [1, {a: b}]\nPeople:\n- Name: p\n- Name: q\nLists: {a: [1, 2]}", typeof(Holder)),
            ("X: 1\nY: 2", typeof(Point)),
            ("a: 1\na: 2", typeof(Dictionary<string, int>)),
            ("1: a\n2: b", typeof(Dictionary<int, string>)),
            ("[1, 2]", typeof(int[])),
            ("[1, 2]", typeof(List<long>)),
            ("[1, 2]", typeof(IEnumerable<int>)),
            ("[1, 2]", typeof(ICollection<int>)),
            ("[1, 2]", typeof(HashSet<int>)),
            ("[1, 2]", typeof(Queue<int>)),
            ("[a, a]", typeof(ISet<string>)),
            ("{a: 1}", typeof(IDictionary<string, int>)),
            ("{a: 1}", typeof(IReadOnlyDictionary<string, int>)),
            ("{a: 1}", typeof(SortedDictionary<string, int>)),
            ("{a: 1}", typeof(Dictionary<string, object>)),
            ("[[1, 2], [3]]", typeof(List<List<int>>)),
            ("[[1, 2], [3]]", typeof(int[][])),
            ("a: &x [1]\nb: *x", typeof(Dictionary<string, List<int>>)),
            ("a: &x {p: 1}\nb: *x", typeof(Dictionary<string, Dictionary<string, int>>)),
            ("a: *zz", typeof(object)),
            ("a: [1", typeof(object)),
            ("a: b: c", typeof(object)),
            ("a:\n\t- b", typeof(object)),
            ("a: 1\n---\nb: 2", typeof(object)),
            ("--- a\n--- b", typeof(object)),
            ("a: &x 1\nb: *x", typeof(object)),
            ("!!int 5", typeof(object)),
            ("!!str 5", typeof(object)),
            ("!!float 1", typeof(object)),
            ("!!bool yes", typeof(object)),
            ("!!null x", typeof(object)),
            ("!foo bar", typeof(object)),
            ("!!binary aGk=", typeof(object)),
            ("!!set {a, b}", typeof(object)),
            ("? [1]\n: 2", typeof(object)),
            ("{a: 1, b: 2}", typeof(object)),
            ("[a, 'b', \"c\", 1, true, null, ~, 2020-01-01]", typeof(object)),
            ("|\n x\n y\n", typeof(object)),
            (">\n x\n y\n", typeof(object)),
            ("a: |-\n  x\n  y\nb: >\n  p\n  q\n", typeof(object)),
            ("<<: {a: 1}", typeof(object)),
            ("base: &b {x: 1}\nderived:\n  <<: *b\n  y: 2", typeof(object)),
            ("\"a\\tb\\u00e9\"", typeof(object)),
            ("'it''s'", typeof(object)),
            ("a: 1 # comment\nb: 2", typeof(object)),
            ("%YAML 1.1\n---\na: 1", typeof(object)),
        };
        foreach ((string yaml, Type type) in cases)
        {
            string a = Differ.Outcome(() => rd.Deserialize(yaml, type));
            string b = Differ.Outcome(() => cd.Deserialize(yaml, type));
            // Intended: these two collection targets are read here and refused by the real library.
            bool intended = (type == typeof(ISet<string>) || type == typeof(IReadOnlyDictionary<string, int>)) && a.StartsWith("EX ", StringComparison.Ordinal);
            if (a != b && !intended)
            {
                diffs.AppendLine($"{type.Name} <- [{yaml.Replace("\n", "\\n")}]\n    real={a}\n    comp={b}");
            }
        }

        Assert.True(diffs.Length == 0, diffs.ToString());
    }
}
