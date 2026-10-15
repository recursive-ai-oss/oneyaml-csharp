using BenchmarkDotNet.Attributes;
using OneYamlKit.Bench.Models;
using Cm = OneYaml.Compat.YamlDotNet;
using CmNames = OneYaml.Compat.YamlDotNet.Serialization.NamingConventions;
using Rl = YamlDotNet;
using RlNames = YamlDotNet.Serialization.NamingConventions;

namespace OneYaml.Benchmarks;

/// <summary>
/// The YamlDotNet compatibility package against YamlDotNet itself, with the same builder calls on both sides:
/// typed load, untyped load, dump and the representation model. Only the kit's records and workflow models are
/// used for typed rows; the workflow's three renamed members are set through attribute overrides on both sides.
/// </summary>
[MemoryDiagnoser]
[Config(typeof(ParseConfig))]
public class CompatTypedLoadBenchmarks
{
    private static readonly Rl.Serialization.IDeserializer s_real = Real();
    private static readonly Cm.Serialization.IDeserializer s_compat = Compat();

    private string _text = string.Empty;

    [Params("gha-workflow", "records-4k", "records-64k")]
    public string Workload { get; set; } = string.Empty;

    private bool IsRecords => Workload.StartsWith("records", StringComparison.Ordinal);

    internal static Rl.Serialization.IDeserializer Real() => new Rl.Serialization.DeserializerBuilder()
        .WithNamingConvention(RlNames.CamelCaseNamingConvention.Instance)
        .WithAttemptingUnquotedStringTypeDeserialization()
        .Build();

    // The models carry YamlDotNet's own attributes for the renamed members; the package cannot see those, so the same
    // names are given as overrides.
    internal static Cm.Serialization.IDeserializer Compat() => new Cm.Serialization.DeserializerBuilder()
        .WithNamingConvention(CmNames.CamelCaseNamingConvention.Instance)
        .WithAttemptingUnquotedStringTypeDeserialization()
        .WithAttributeOverride<Triggers>(t => t.PullRequest!, Renamed("pull_request"))
        .WithAttributeOverride<Job>(j => j.RunsOn, Renamed("runs-on"))
        .WithAttributeOverride<Matrix>(m => m.RunsOn, Renamed("runs-on"))
        .WithAttributeOverride<Step>(x => x.Condition!, Renamed("if"))
        .Build();

    private static Cm.Serialization.YamlMemberAttribute Renamed(string alias) => new() { Alias = alias, ApplyNamingConventions = false };

    [GlobalSetup]
    public void Setup()
    {
        (_, _text) = Workloads.Load(Workload);
        // Both sides must read the same model, or the comparison means nothing.
        object real = ReadReal();
        object compat = ReadCompat();
        string a = YamlSerializer.Serialize(real, real.GetType());
        string b = YamlSerializer.Serialize(compat, compat.GetType());
        if (a != b || a.Length < 50)
        {
            throw new InvalidOperationException("The compat read differs from the YamlDotNet read.");
        }
    }

    [Benchmark(Baseline = true, Description = "YamlDotNet")]
    public object ReadReal() => IsRecords ? s_real.Deserialize<List<Record>>(_text) : s_real.Deserialize<Workflow>(_text);

    [Benchmark(Description = "OneYaml compat")]
    public object ReadCompat() => IsRecords ? s_compat.Deserialize<List<Record>>(_text) : s_compat.Deserialize<Workflow>(_text);
}

/// <summary>Untyped load: the whole file into dictionaries and lists.</summary>
[MemoryDiagnoser]
[Config(typeof(ParseConfig))]
public class CompatObjectLoadBenchmarks
{
    private static readonly Rl.Serialization.IDeserializer s_real = new Rl.Serialization.DeserializerBuilder().Build();
    private static readonly Cm.Serialization.IDeserializer s_compat = new Cm.Serialization.DeserializerBuilder().Build();

    private string _text = string.Empty;

    [Params("gha-workflow", "k8s-multi-16k", "openapi-100k", "records-64k", "helm-values-64k")]
    public string Workload { get; set; } = string.Empty;

    [GlobalSetup]
    public void Setup()
    {
        (_, _text) = Workloads.Load(Workload);
        if (_text.Contains("\n---", StringComparison.Ordinal))
        {
            // The string overloads read one document; take the first.
            _text = _text[.._text.IndexOf("\n---", StringComparison.Ordinal)];
        }
    }

    [Benchmark(Baseline = true, Description = "YamlDotNet")]
    public object? ReadReal() => s_real.Deserialize<object>(_text);

    [Benchmark(Description = "OneYaml compat")]
    public object? ReadCompat() => s_compat.Deserialize<object>(_text);
}

/// <summary>Dump of the records and workflow models.</summary>
[MemoryDiagnoser]
[Config(typeof(ParseConfig))]
public class CompatDumpBenchmarks
{
    private static readonly Rl.Serialization.ISerializer s_real = new Rl.Serialization.SerializerBuilder()
        .WithNamingConvention(RlNames.CamelCaseNamingConvention.Instance).Build();

    private static readonly Cm.Serialization.ISerializer s_compat = new Cm.Serialization.SerializerBuilder()
        .WithNamingConvention(CmNames.CamelCaseNamingConvention.Instance).Build();

    private List<Record> _records = [];

    [Params("records-4k", "records-64k")]
    public string Workload { get; set; } = string.Empty;

    [GlobalSetup]
    public void Setup()
    {
        (byte[] bytes, _) = Workloads.Load(Workload);
        _records = YamlSerializer.Deserialize<List<Record>>(bytes)!;
    }

    [Benchmark(Baseline = true, Description = "YamlDotNet")]
    public string WriteReal() => s_real.Serialize(_records);

    [Benchmark(Description = "OneYaml compat")]
    public string WriteCompat() => s_compat.Serialize(_records);
}

/// <summary>The representation model: load a stream, and load and save it back.</summary>
[MemoryDiagnoser]
[Config(typeof(ParseConfig))]
public class CompatStreamBenchmarks
{
    private string _text = string.Empty;

    [Params("gha-workflow", "openapi-100k", "records-64k", "helm-ingress-nginx", "compose-merge-2k")]
    public string Workload { get; set; } = string.Empty;

    [GlobalSetup]
    public void Setup() => (_, _text) = Workloads.Load(Workload);

    [Benchmark(Baseline = true, Description = "YamlDotNet load")]
    public object LoadReal()
    {
        var s = new Rl.RepresentationModel.YamlStream();
        s.Load(new StringReader(_text));
        return s;
    }

    [Benchmark(Description = "OneYaml compat load")]
    public object LoadCompat()
    {
        var s = new Cm.RepresentationModel.YamlStream();
        s.Load(new StringReader(_text));
        return s;
    }

    [Benchmark(Description = "YamlDotNet load and save")]
    public string SaveReal()
    {
        var s = new Rl.RepresentationModel.YamlStream();
        s.Load(new StringReader(_text));
        var w = new StringWriter();
        s.Save(w, false);
        return w.ToString();
    }

    [Benchmark(Description = "OneYaml compat load and save")]
    public string SaveCompat()
    {
        var s = new Cm.RepresentationModel.YamlStream();
        s.Load(new StringReader(_text));
        var w = new StringWriter();
        s.Save(w, false);
        return w.ToString();
    }
}
