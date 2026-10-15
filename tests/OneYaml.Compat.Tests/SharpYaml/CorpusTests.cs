using System.Text;
using Xunit;
using Cs = global::OneYaml.Compat.SharpYaml;
using Rs = global::SharpYaml;

namespace OneYaml.Compat.Tests.SharpYamlCompat;

// Runs the shared conformance inputs and the benchmark workloads through the real library and the compat package,
// as an untyped read, and compares what each makes of them. A difference must be one of the classes in Classify;
// a difference that has no class fails.
public class SharpYamlCorpusTests
{
    private const string SuiteVariable = "ONEYAML_YTS_DIR";
    private const string WorkloadVariable = "ONEYAML_WORKLOADS";
    private const string ReportVariable = "ONEYAML_SY_REPORT";

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

    [Theory]
    [Trait("Category", "Slow")]
    [InlineData("gha-workflow")]
    [InlineData("records-64k")]
    public void Typed_workload_reads_and_writes_alike(string name)
    {
        string? root = Environment.GetEnvironmentVariable(WorkloadVariable);
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            return;
        }

        string file = Path.Combine(root, "workloads", name, "input.yaml");
        string text = File.ReadAllText(File.Exists(file) ? file : Path.Combine(root, name + ".yaml"));
        var real = new Rs.YamlSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };
        var compat = new Cs.YamlSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };
        if (name == "gha-workflow")
        {
            Typed<Workflow>(text, real, compat);
        }
        else
        {
            Typed<List<Record>>(text, real, compat);
        }
    }

    private static void Typed<T>(string text, Rs.YamlSerializerOptions real, Cs.YamlSerializerOptions compat)
    {
        T expected = Rs.YamlSerializer.Deserialize<T>(text, real)!;
        T actual = Cs.YamlSerializer.Deserialize<T>(text, compat)!;
        Assert.Equal(Differ.Dump(expected), Differ.Dump(actual));

        // What the package writes, the real library reads to the same value.
        string written = Cs.YamlSerializer.Serialize(actual, compat);
        Assert.Equal(Differ.Dump(expected), Differ.Dump(Rs.YamlSerializer.Deserialize<T>(written, real)!));
        Assert.Equal(Differ.Dump(expected), Differ.Dump(Cs.YamlSerializer.Deserialize<T>(written, compat)!));
    }

    private static void Compare(string id, string text)
    {
        string real = SyHelp.Outcome(() => Rs.YamlSerializer.Deserialize<object>(text));
        string compat = SyHelp.Outcome(() => Cs.YamlSerializer.Deserialize<object>(text));
        if (real == compat)
        {
            return;
        }

        string? kind = SyClassify.Classify(id, text, real, compat);
        string? report = Environment.GetEnvironmentVariable(ReportVariable);
        if (!string.IsNullOrEmpty(report))
        {
            File.AppendAllText(report, $"{id}\t{kind ?? "UNCLASSIFIED"}\n   real:   {Trim(real)}\n   compat: {Trim(compat)}\n");
        }

        Assert.True(kind is not null, $"{id}: no class for this difference\n   real:   {Trim(real)}\n   compat: {Trim(compat)}");
    }

    private static string Trim(string s) => (s.Length <= 400 ? s : s[..400] + "…").Replace("\n", "\\n", StringComparison.Ordinal);
}

// The classes of difference between the real library and the compat package on an untyped read.
//   rejects-valid: the real library refuses YAML the specification allows; the compat package reads it.
//   alias-refused: an untyped read in the real library refuses an alias (unless references are preserved); the compat package follows it, within the limits.
//   plain-scalar-indicator: the real library takes a plain scalar that starts with or ends in an indicator character as something else.
//   block-scalar-keep: the real library drops or adds a line break in a block scalar that keeps trailing lines (spec 8.1.1.2).
//   anchor-name: the real library cuts an anchor name at a colon, which the specification allows inside a name.
internal static class SyClassify
{
    private static readonly Dictionary<string, string> Named = new(StringComparer.Ordinal)
    {
        ["652Z"] = "plain-scalar-indicator",
        ["HM87/01"] = "plain-scalar-indicator",
        ["4ABK"] = "plain-scalar-indicator",
        ["JEF9/02"] = "block-scalar-keep",
        ["L24T/01"] = "block-scalar-keep",
        ["Y2GN"] = "anchor-name",
    };

    internal static string? Classify(string id, string text, string real, string compat)
    {
        if (Named.TryGetValue(id, out string? kind))
        {
            return kind;
        }

        if (!real.StartsWith("EX ", StringComparison.Ordinal) || compat.StartsWith("EX ", StringComparison.Ordinal))
        {
            return null;
        }

        // An untyped read refuses every alias unless references are preserved; the compat package follows them within the limits.
        return text.Contains('&', StringComparison.Ordinal) && text.Contains('*', StringComparison.Ordinal) ? "alias-refused" : "rejects-valid";
    }
}
