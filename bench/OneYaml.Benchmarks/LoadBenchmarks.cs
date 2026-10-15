using BenchmarkDotNet.Attributes;
using OneYamlKit.Bench.Models;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace OneYaml.Benchmarks;

/// <summary>
/// Typed load of the kit workloads with the reflection binder against the competitors' paths: VYaml and
/// SharpYaml read through generated code, YamlDotNet through reflection. The input files come from the
/// directory named by ONEYAML_WORKLOADS. The 4k records file is the first records of the 64k one.
/// </summary>
[MemoryDiagnoser]
[Config(typeof(ParseConfig))]
public class TypedLoadBenchmarks
{
    private static readonly IDeserializer s_yamlDotNet = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithAttemptingUnquotedStringTypeDeserialization()
        .Build();

    private byte[] _bytes = [];
    private string _text = string.Empty;

    [Params("gha-workflow", "records-4k", "records-64k")]
    public string Workload { get; set; } = string.Empty;

    private bool IsRecords => Workload.StartsWith("records", StringComparison.Ordinal);

    [GlobalSetup]
    public void Setup()
    {
        (_bytes, _text) = Workloads.Load(Workload);
        if (IsRecords)
        {
            Check(YamlSerializer.Deserialize<List<Record>>(_bytes)!.Count);
            Check(YamlSerializer.Deserialize(_bytes, OneYamlContext.Default.ListRecord)!.Count);
        }
        else
        {
            Check(YamlSerializer.Deserialize<Workflow>(_bytes)!.Jobs.Count);
            Check(YamlSerializer.Deserialize(_bytes, OneYamlContext.Default.Workflow)!.Jobs.Count);
        }
    }

    private static void Check(int count)
    {
        if (count == 0)
        {
            throw new InvalidOperationException("The workload bound to an empty result.");
        }
    }

    [Benchmark(Baseline = true, Description = "OneYaml reflection")]
    public object? ReadOneYaml() => IsRecords
        ? YamlSerializer.Deserialize<List<Record>>(_bytes)
        : YamlSerializer.Deserialize<Workflow>(_bytes);

    [Benchmark(Description = "OneYaml generated")]
    public object? ReadOneYamlGenerated() => IsRecords
        ? YamlSerializer.Deserialize(_bytes, OneYamlContext.Default.ListRecord)
        : YamlSerializer.Deserialize(_bytes, OneYamlContext.Default.Workflow);

    [Benchmark(Description = "VYaml generated")]
    public object? ReadVYaml() => IsRecords
        ? VYaml.Serialization.YamlSerializer.Deserialize<List<Record>>(_bytes)
        : VYaml.Serialization.YamlSerializer.Deserialize<Workflow>(_bytes);

    [Benchmark(Description = "SharpYaml generated")]
    public object? ReadSharpYaml() => IsRecords
        ? SharpYaml.YamlSerializer.Deserialize(_text, SharpYamlContext.Default.ListRecord)
        : SharpYaml.YamlSerializer.Deserialize(_text, SharpYamlContext.Default.Workflow);

    [Benchmark(Description = "YamlDotNet reflection")]
    public object? ReadYamlDotNet() => IsRecords
        ? s_yamlDotNet.Deserialize<List<Record>>(_text)
        : s_yamlDotNet.Deserialize<Workflow>(_text);
}

/// <summary>Generic (untyped) load of the kit workloads against the competitors.</summary>
[MemoryDiagnoser]
[Config(typeof(ParseConfig))]
public class GenericLoadBenchmarks
{
    private static readonly IDeserializer s_yamlDotNet = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithAttemptingUnquotedStringTypeDeserialization()
        .Build();

    private static readonly SharpYaml.YamlSerializerOptions s_sharpOptions = new() { ReferenceHandling = SharpYaml.YamlReferenceHandling.Preserve };

    private byte[] _bytes = [];
    private string _text = string.Empty;

    [Params("reference-a", "reference-b", "compose-merge-2k", "gha-workflow", "k8s-ingress-nginx-multi", "k8s-multi-16k", "openapi-100k", "records-64k", "wide-map-64k", "long-scalars-256k", "anchors-32k")]
    public string Workload { get; set; } = string.Empty;

    private bool _multi;

    [GlobalSetup]
    public void Setup()
    {
        (_bytes, _text) = Workloads.Load(Workload);
        _multi = _text.Contains("\n---", StringComparison.Ordinal);
        if (YamlSerializer.DeserializeAll<object>(_bytes).Count == 0)
        {
            throw new InvalidOperationException("The workload loaded as nothing.");
        }
    }

    [Benchmark(Baseline = true, Description = "OneYaml")]
    public object? ReadOneYaml() => _multi
        ? YamlSerializer.DeserializeAll<object>(_bytes)
        : YamlSerializer.Deserialize<object>(_bytes);

    [Benchmark(Description = "OneYaml generated")]
    public object? ReadOneYamlGenerated() => _multi
        ? YamlSerializer.DeserializeAll(_bytes, OneYamlContext.Default.Object)
        : YamlSerializer.Deserialize(_bytes, OneYamlContext.Default.Object);

    [Benchmark(Description = "VYaml")]
    public object? ReadVYaml() => _multi
        ? VYaml.Serialization.YamlSerializer.DeserializeMultipleDocuments<object>(_bytes).ToList()
        : VYaml.Serialization.YamlSerializer.Deserialize<object>(_bytes);

    // The serializer reads one document per call, so a stream of several goes through its document model.
    [Benchmark(Description = "SharpYaml")]
    public object? ReadSharpYaml() => _multi
        ? SharpYaml.Model.YamlStream.Load(new StringReader(_text))
        : SharpYaml.YamlSerializer.Deserialize<object>(_text, s_sharpOptions);

    [Benchmark(Description = "YamlDotNet")]
    public object? ReadYamlDotNet()
    {
        if (!_multi)
        {
            return s_yamlDotNet.Deserialize<object>(_text);
        }

        var docs = new List<object?>();
        var parser = new Parser(new StringReader(_text));
        parser.Consume<YamlDotNet.Core.Events.StreamStart>();
        while (parser.Accept<YamlDotNet.Core.Events.DocumentStart>(out _))
        {
            docs.Add(s_yamlDotNet.Deserialize<object>(parser));
        }

        return docs;
    }
}

internal static class Workloads
{
    public static (byte[] Bytes, string Text) Load(string name)
    {
        string dir = Environment.GetEnvironmentVariable("ONEYAML_WORKLOADS")
            ?? throw new InvalidOperationException("Set ONEYAML_WORKLOADS to the directory holding the workload files.");
        string file = Path.Combine(dir, name + ".yaml");
        byte[] bytes;
        if (name == "records-4k" && !File.Exists(file))
        {
            // The first records of the 64k file, cut at a record boundary below 4 KiB.
            byte[] all = File.ReadAllBytes(Path.Combine(dir, "records-64k.yaml"));
            int cut = 0;
            for (int i = 0; i + 1 < 4096 && i + 1 < all.Length; i++)
            {
                if (all[i] == (byte)'\n' && all[i + 1] == (byte)'-')
                {
                    cut = i + 1;
                }
            }

            bytes = all[..cut];
        }
        else
        {
            bytes = File.ReadAllBytes(file);
        }

        return (bytes, System.Text.Encoding.UTF8.GetString(bytes));
    }
}
