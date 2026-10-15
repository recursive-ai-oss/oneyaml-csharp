using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using Json.Patch;
using Yaml2JsonNode;
using Y2J = Yaml2JsonNode.YamlSerializer;

namespace OneYaml.Benchmarks;

/// <summary>
/// The kit's patch workloads: parse, apply an RFC 6902 patch, write the result. OneYaml keeps the
/// document, so the output is the input with only the patched values changed. The Yaml2JsonNode row
/// with JsonPatch.Net works on data and writes a fresh serialisation, so it does not keep comments or
/// layout; it is listed for orientation, not as a like-for-like row.
/// </summary>
[MemoryDiagnoser]
[Config(typeof(PatchConfig))]
public class PatchBenchmarks
{
    private byte[] _bytes = [];
    private string _text = string.Empty;
    private string _patchText = string.Empty;
    private YamlPatch _patch = new();
    private JsonPatch _jsonPatch = null!;

    [Params("reference-a", "reference-b", "compose-wordpress", "gha-workflow", "records-4k", "records-64k", "openapi-100k-many-ops", "records-64k-many-ops", "flow-appends-1k")]
    public string Workload { get; set; } = string.Empty;

    /// <summary>The size of the input, to turn allocated bytes into a multiple of it.</summary>
    public int InputBytes => _bytes.Length;

    [GlobalSetup]
    public void Setup()
    {
        string dir = Environment.GetEnvironmentVariable("ONEYAML_WORKLOADS")
            ?? throw new InvalidOperationException("Set ONEYAML_WORKLOADS to the directory holding the workload files.");
        bool appends = Workload == "flow-appends-1k";
        bool many = Workload.EndsWith("-many-ops", StringComparison.Ordinal) || appends;
        string name = Workload.EndsWith("-many-ops", StringComparison.Ordinal) ? Workload[..^"-many-ops".Length] : Workload;
        string folder = Path.Combine(dir, "workloads", appends ? "reference-a" : name);
        _bytes = appends ? Encoding.UTF8.GetBytes("a: 1\nv: " + new string('x', 240) + "\n") : File.ReadAllBytes(Path.Combine(folder, "input.yaml"));
        _text = Encoding.UTF8.GetString(_bytes);
        JsonArray ops = appends ? Appends() : many ? ManyOps() : (JsonArray)JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "patch.json")))!["ops"]!;
        _patchText = ops.ToJsonString();
        _patch = YamlPatch.Parse(_patchText);
        _jsonPatch = JsonSerializer.Deserialize<JsonPatch>(_patchText)!;
        if (!many)
        {
            byte[] expected = File.ReadAllBytes(Path.Combine(folder, "patch.expected.yaml"));
            if (!expected.AsSpan().SequenceEqual(OneYamlPatch()))
            {
                throw new InvalidOperationException("The patch does not give the kit's expected bytes for " + Workload);
            }
        }
    }

    // One empty sequence, then a thousand copies of a long string appended to it.
    private static JsonArray Appends()
    {
        var ops = new JsonArray { new JsonObject { ["op"] = "add", ["path"] = "/f", ["value"] = new JsonArray() } };
        for (int i = 0; i < 1000; i++)
        {
            ops.Add(new JsonObject { ["op"] = "copy", ["from"] = "/v", ["path"] = "/f/-" });
        }

        return ops;
    }

    // Many small operations on one big document: replaces of scalars spread over the file.
    private JsonArray ManyOps()
    {
        var ops = new JsonArray();
        using YamlDocument doc = YamlDocument.Parse(_bytes);
        var paths = new List<string>();
        Collect(doc.Root, string.Empty, paths);
        int step = Math.Max(1, paths.Count / 100);
        for (int i = 0; i < paths.Count && ops.Count < 100; i += step)
        {
            ops.Add(new JsonObject { ["op"] = "replace", ["path"] = paths[i], ["value"] = "patched-" + i });
        }

        return ops;
    }

    private static void Collect(YamlNode node, string path, List<string> into)
    {
        if (node.Kind == YamlNodeKind.Scalar && path.Length > 0 && node.TryGetValue<string>(out string? s) && s is not null && s.Length > 0 && !char.IsDigit(s[0]) && s is not ("true" or "false"))
        {
            into.Add(path);
            return;
        }

        if (node.Kind == YamlNodeKind.Mapping)
        {
            foreach (KeyValuePair<YamlNode, YamlNode> e in node.Entries)
            {
                Collect(e.Value, path + "/" + e.Key.GetValue<string>().Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal), into);
            }
        }
        else if (node.Kind == YamlNodeKind.Sequence)
        {
            int i = 0;
            foreach (YamlNode item in node.Items)
            {
                Collect(item, path + "/" + i++, into);
            }
        }
    }

    [Benchmark(Description = "OneYaml parse + ApplyPatch(json text) + write")]
    public byte[] OneYamlPatch()
    {
        using YamlDocument doc = YamlDocument.Parse(_bytes);
        doc.ApplyPatch(_patchText);
        return doc.ToUtf8Bytes();
    }

    [Benchmark(Description = "OneYaml parse + ApplyPatch(YamlPatch) + write")]
    public byte[] OneYamlBuiltPatch()
    {
        using YamlDocument doc = YamlDocument.Parse(_bytes);
        doc.ApplyPatch(_patch);
        return doc.ToUtf8Bytes();
    }

    [Benchmark(Description = "Yaml2JsonNode parse + JsonPatch.Net + write (differs: not lossless)")]
    public byte[] JsonPatchNet()
    {
        JsonNode? node = Y2J.Parse(_text).ToJsonNode().First();
        PatchResult result = _jsonPatch.Apply(node);
        return Encoding.UTF8.GetBytes(Y2J.Serialize(result.Result!.ToYamlNode(), null));
    }

    private sealed class PatchConfig : ManualConfig
    {
        public PatchConfig()
        {
            AddJob(Job.ShortRun.WithToolchain(InProcessEmitToolchain.Instance));
        }
    }
}
