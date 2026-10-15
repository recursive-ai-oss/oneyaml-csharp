using System.Text;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using SharpYaml.Syntax;

namespace OneYaml.Benchmarks;

/// <summary>
/// The lossless document against SharpYaml's syntax tree: parse, write back unchanged, and parse plus
/// a small edit script followed by the write. The SharpYaml edit row stops at parse and write, because
/// the library has no path or value edit; it is therefore a lower bound for what an edit costs there.
/// </summary>
[MemoryDiagnoser]
[Config(typeof(DocumentConfig))]
public class DocumentBenchmarks
{
    private static readonly YamlSyntaxOptions Trivia = new() { IncludeTrivia = true };

    private byte[] _bytes = [];
    private string _text = string.Empty;
    private (string Op, string Path, object? Value)[] _ops = [];
    private YamlDocument? _doc;
    private YamlSyntaxTree? _tree;
    private string _setPath = string.Empty;
    private int _setCount;

    [Params("reference-a", "records-4k", "openapi-petstore", "helm-ingress-nginx", "compose-merge-2k", "openapi-100k", "anchors-32k", "helm-values-64k")]
    public string Workload { get; set; } = string.Empty;

    [GlobalSetup]
    public void Setup()
    {
        string dir = Environment.GetEnvironmentVariable("ONEYAML_WORKLOADS")
            ?? throw new InvalidOperationException("Set ONEYAML_WORKLOADS to the directory holding the workload files.");
        string folder = Path.Combine(dir, "workloads", Workload);
        string flat = Path.Combine(dir, Workload + ".yaml");
        _bytes = File.ReadAllBytes(File.Exists(flat) ? flat : Path.Combine(folder, "input.yaml"));
        _text = Encoding.UTF8.GetString(_bytes);
        string script = Path.Combine(folder, "edit.json");
        _ops = File.Exists(script) ? ReadOps(File.ReadAllText(script)) : [];
        _doc = YamlDocument.Parse(_bytes);
        _tree = YamlSyntaxTree.Parse(_text, Trivia);
        _setPath = FirstTextLeaf(_doc.Root, string.Empty) ?? string.Empty;
    }

    // A path to the last text value in the file, for the single-edit row.
    private static string? FirstTextLeaf(YamlNode node, string path)
    {
        if (node.Kind == YamlNodeKind.Scalar && path.Length > 0 && node.TryGetValue<string>(out string? s) && s is { Length: > 0 } && !char.IsDigit(s[0]) && s is not ("true" or "false" or "null"))
        {
            return path;
        }

        string? found = null;
        if (node.Kind == YamlNodeKind.Mapping)
        {
            foreach (KeyValuePair<YamlNode, YamlNode> e in node.Entries)
            {
                found = FirstTextLeaf(e.Value, path + "/" + e.Key.GetValue<string>().Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal)) ?? found;
            }
        }
        else if (node.Kind == YamlNodeKind.Sequence)
        {
            int i = 0;
            foreach (YamlNode item in node.Items)
            {
                found = FirstTextLeaf(item, path + "/" + i++) ?? found;
            }
        }

        return found;
    }

    [GlobalCleanup]
    public void Cleanup() => _doc?.Dispose();

    private static (string, string, object?)[] ReadOps(string json)
    {
        using JsonDocument d = JsonDocument.Parse(json);
        var list = new List<(string, string, object?)>();
        foreach (JsonElement op in d.RootElement.GetProperty("ops").EnumerateArray())
        {
            object? value = null;
            if (op.TryGetProperty("value", out JsonElement v))
            {
                value = v.ValueKind switch
                {
                    JsonValueKind.String => v.GetString(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Number => v.TryGetInt64(out long l) ? l : v.GetDouble(),
                    _ => null,
                };
            }

            list.Add((op.GetProperty("op").GetString()!, op.GetProperty("path").GetString()!, value));
        }

        return [.. list];
    }

    [Benchmark(Description = "OneYaml parse")]
    public int ParseOneYaml()
    {
        using YamlDocument doc = YamlDocument.Parse(_bytes);
        return doc.Documents.Count;
    }

    [Benchmark(Description = "SharpYaml syntax tree parse")]
    public YamlSyntaxTree ParseSharpYaml() => YamlSyntaxTree.Parse(Encoding.UTF8.GetString(_bytes), Trivia);

    [Benchmark(Description = "OneYaml unchanged write")]
    public byte[] EmitOneYaml() => _doc!.ToUtf8Bytes();

    [Benchmark(Description = "SharpYaml unchanged write")]
    public byte[] EmitSharpYaml() => Encoding.UTF8.GetBytes(_tree!.ToFullString());

    [Benchmark(Description = "OneYaml one Set on a parsed document")]
    public void SetOneYaml() => _doc!.Set(_setPath, (_setCount++ & 1) == 0 ? "alpha" : "beta");

    [Benchmark(Description = "OneYaml parse + edit script + write")]
    public byte[] EditOneYaml()
    {
        if (_ops.Length == 0)
        {
            return [];
        }

        using YamlDocument doc = YamlDocument.Parse(_bytes);
        foreach ((string op, string path, object? value) in _ops)
        {
            if (op == "remove")
            {
                doc.Remove(path);
            }
            else
            {
                doc.Set(path, value);
            }
        }

        return doc.ToUtf8Bytes();
    }

    [Benchmark(Description = "SharpYaml parse + write (no edit, lower bound)")]
    public byte[] EditSharpYamlLowerBound() => Encoding.UTF8.GetBytes(YamlSyntaxTree.Parse(Encoding.UTF8.GetString(_bytes), Trivia).ToFullString());

    private sealed class DocumentConfig : ManualConfig
    {
        public DocumentConfig()
        {
            AddJob(Job.ShortRun.WithToolchain(InProcessEmitToolchain.Instance));
        }
    }
}
