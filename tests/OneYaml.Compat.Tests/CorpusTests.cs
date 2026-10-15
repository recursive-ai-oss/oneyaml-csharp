using System.Text;
using Xunit;
using Cm = global::OneYaml.Compat.YamlDotNet.RepresentationModel;
using CmS = global::OneYaml.Compat.YamlDotNet.Serialization;
using Rl = global::YamlDotNet.RepresentationModel;
using RlS = global::YamlDotNet.Serialization;

namespace OneYaml.Compat.Tests;

// Runs the shared conformance inputs and the benchmark workloads through both libraries and compares what each makes of them.
// A split between the two (one throws, the other does not) is listed, with its class, in the table below; any other split fails.
public class CorpusTests
{
    private const string SuiteVariable = "ONEYAML_YTS_DIR";
    private const string WorkloadVariable = "ONEYAML_WORKLOADS";

    // Cases where the libraries differ on purpose, each with the class of the difference.
    //   rejects-valid: YamlDotNet refuses YAML the specification allows; the compat package reads it.
    //   crash: YamlDotNet throws ArgumentException (not a YAML error) on this input; the compat package reads it.
    //   block-scalar: YamlDotNet drops or adds a line break in a block scalar that keeps trailing lines (spec 8.1.1.2).
    //   empty-tagged-str: YamlDotNet reads an empty scalar tagged !!str as null; the specification says the empty string.
    //   empty-key: YamlDotNet accepts an empty key in one flow-mapping shape and refuses it everywhere else; compat refuses all.
    private static readonly Dictionary<string, string> Known = new(StringComparer.Ordinal)
    {
        ["4MUZ/02"] = "rejects-valid",
        ["57H4"] = "rejects-valid",
        ["6JWB"] = "rejects-valid",
        ["DK95/04"] = "rejects-valid",
        ["EHF6"] = "rejects-valid",
        ["VJP3/01"] = "rejects-valid",
        ["WZ62"] = "rejects-valid",
        ["M2N8/00"] = "crash",
        ["UKK6/00"] = "crash",
        ["JEF9/01"] = "block-scalar",
        ["JEF9/02"] = "block-scalar",
        ["L24T/01"] = "block-scalar",
        ["LE5A"] = "empty-tagged-str",
        ["FRK4"] = "empty-key",
    };

    public static IEnumerable<object[]> SuiteCases()
    {
        string? dir = Environment.GetEnvironmentVariable(SuiteVariable);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            // xunit rejects an empty data set; the test returns at once for this entry.
            yield return [string.Empty];
            yield break;
        }

        foreach (string caseDir in Directory.EnumerateDirectories(dir).OrderBy(d => d, StringComparer.Ordinal))
        {
            if (File.Exists(Path.Combine(caseDir, "in.yaml")))
            {
                if (!File.Exists(Path.Combine(caseDir, "error")))
                {
                    yield return [Path.GetFileName(caseDir)];
                }

                continue;
            }

            foreach (string sub in Directory.EnumerateDirectories(caseDir).OrderBy(d => d, StringComparer.Ordinal))
            {
                if (File.Exists(Path.Combine(sub, "in.yaml")) && !File.Exists(Path.Combine(sub, "error")))
                {
                    yield return [Path.GetFileName(caseDir) + "/" + Path.GetFileName(sub)];
                }
            }
        }
    }

    public static IEnumerable<object[]> WorkloadCases()
    {
        string? dir = Environment.GetEnvironmentVariable(WorkloadVariable);
        var names = new SortedSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
        {
            string nested = Path.Combine(dir, "workloads");
            foreach (string w in Directory.Exists(nested) ? Directory.EnumerateDirectories(nested) : [])
            {
                if (File.Exists(Path.Combine(w, "input.yaml")))
                {
                    names.Add(Path.GetFileName(w));
                }
            }

            foreach (string f in Directory.EnumerateFiles(dir, "*.yaml"))
            {
                names.Add(Path.GetFileNameWithoutExtension(f));
            }
        }

        if (names.Count == 0)
        {
            yield return [string.Empty];
        }

        foreach (string name in names)
        {
            yield return [name];
        }
    }

    [Theory]
    [MemberData(nameof(SuiteCases))]
    public void Suite_case_reads_alike(string id)
    {
        if (id.Length == 0)
        {
            return;
        }

        string text = File.ReadAllText(Path.Combine(Environment.GetEnvironmentVariable(SuiteVariable)!, id, "in.yaml"));
        Compare(id, text);
    }

    [Theory]
    [Trait("Category", "Slow")]
    [MemberData(nameof(WorkloadCases))]
    public void Workload_reads_alike(string name)
    {
        if (name.Length == 0)
        {
            return;
        }

        string root = Environment.GetEnvironmentVariable(WorkloadVariable)!;
        string folder = Path.Combine(root, "workloads", name, "input.yaml");
        string text = File.ReadAllText(File.Exists(folder) ? folder : Path.Combine(root, name + ".yaml"));
        Compare("workload:" + name, text);
    }

    private static void Compare(string id, string text)
    {
        var problems = new StringBuilder();

        string rs = Outcome(() =>
        {
            var s = new Rl.YamlStream();
            s.Load(new StringReader(text));
            return RepresentationTests.Shape(s);
        });
        string cs = Outcome(() =>
        {
            var s = new Cm.YamlStream();
            s.Load(new StringReader(text));
            return RepresentationTests.Shape(s);
        });
        if (rs != cs)
        {
            problems.AppendLine("stream: real=" + Trim(rs) + "\n        compat=" + Trim(cs));
        }

        // The untyped read takes one document; both refuse several, which is part of what is compared.
        string rv = Outcome(() => new RlS.DeserializerBuilder().Build().Deserialize<object>(text));
        string cv = Outcome(() => new CmS.DeserializerBuilder().Build().Deserialize<object>(text));
        if (rv != cv)
        {
            problems.AppendLine("object: real=" + Trim(rv) + "\n        compat=" + Trim(cv));
        }

        if (problems.Length == 0)
        {
            Assert.DoesNotContain(id, Known.Keys);
            return;
        }

        Assert.True(Known.ContainsKey(id), id + "\n" + problems);
    }

    private static string Trim(string s) => s.Length <= 300 ? s.Replace("\n", "\\n", StringComparison.Ordinal) : s[..300].Replace("\n", "\\n", StringComparison.Ordinal) + "…";

    private static string Outcome(Func<object> f)
    {
        try
        {
            object v = f();
            return v as string ?? Differ.Dump(v);
        }
        catch (Exception e)
        {
            return "EX " + Differ.Kind(e);
        }
    }
}
