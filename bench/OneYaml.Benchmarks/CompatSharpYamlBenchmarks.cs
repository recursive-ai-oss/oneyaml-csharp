using BenchmarkDotNet.Attributes;
using OneYamlKit.Bench.Models;
using Cs = OneYaml.Compat.SharpYaml;
using Rs = SharpYaml;

namespace OneYaml.Benchmarks;

// The SharpYaml compatibility package against SharpYaml itself, with the same calls on both sides. Options are made
// once, as a program that cares about speed makes them. The real library is run through reflection, which is what the
// compat package replaces; its generated path is a column of its own where the package has no equivalent.

/// <summary>Typed load of the kit's models.</summary>
[MemoryDiagnoser]
[Config(typeof(ParseConfig))]
public class CompatSharpYamlTypedLoadBenchmarks
{
    private static readonly Rs.YamlSerializerOptions s_real = new() { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };
    private static readonly Cs.YamlSerializerOptions s_compat = new() { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };

    private string _text = string.Empty;

    [Params("gha-workflow", "records-4k", "records-64k")]
    public string Workload { get; set; } = string.Empty;

    private bool IsRecords => Workload.StartsWith("records", StringComparison.Ordinal);

    [GlobalSetup]
    public void Setup()
    {
        (_, _text) = Workloads.Load(Workload);
        string a = OneYaml.YamlSerializer.Serialize(ReadReal(), ReadReal()!.GetType());
        string b = OneYaml.YamlSerializer.Serialize(ReadCompat(), ReadCompat()!.GetType());
        if (a != b || a.Length < 50)
        {
            throw new InvalidOperationException("The compat read differs from the SharpYaml read.");
        }
    }

    [Benchmark(Baseline = true, Description = "SharpYaml")]
    public object? ReadReal() => IsRecords ? Rs.YamlSerializer.Deserialize<List<Record>>(_text, s_real) : Rs.YamlSerializer.Deserialize<Workflow>(_text, s_real);

    [Benchmark(Description = "OneYaml compat")]
    public object? ReadCompat() => IsRecords ? Cs.YamlSerializer.Deserialize<List<Record>>(_text, s_compat) : Cs.YamlSerializer.Deserialize<Workflow>(_text, s_compat);

    [Benchmark(Description = "SharpYaml generated")]
    public object? ReadRealGenerated() => IsRecords
        ? Rs.YamlSerializer.Deserialize(_text, SharpYamlContext.Default.ListRecord)
        : Rs.YamlSerializer.Deserialize(_text, SharpYamlContext.Default.Workflow);
}

/// <summary>Untyped load: the whole file into dictionaries and lists.</summary>
[MemoryDiagnoser]
[Config(typeof(ParseConfig))]
public class CompatSharpYamlObjectLoadBenchmarks
{
    // Aliases are followed only when references are preserved in the real library, so the row sets what a program that reads anchored files sets.
    private static readonly Rs.YamlSerializerOptions s_real = new() { ReferenceHandling = Rs.YamlReferenceHandling.Preserve };
    private static readonly Cs.YamlSerializerOptions s_compat = new() { ReferenceHandling = Cs.YamlReferenceHandling.Preserve };

    private string _text = string.Empty;

    [Params("gha-workflow", "k8s-multi-16k", "openapi-100k", "records-64k", "helm-values-64k", "wide-map-64k", "long-scalars-256k", "unicode-32k", "compose-merge-2k")]
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

    [Benchmark(Baseline = true, Description = "SharpYaml")]
    public object? ReadReal() => Rs.YamlSerializer.Deserialize<object>(_text, s_real);

    [Benchmark(Description = "OneYaml compat")]
    public object? ReadCompat() => Cs.YamlSerializer.Deserialize<object>(_text, s_compat);
}

/// <summary>The cost of a text with several documents, which both libraries read to the first.</summary>
[MemoryDiagnoser]
[Config(typeof(ParseConfig))]
public class CompatSharpYamlFirstDocumentBenchmarks
{
    private static readonly Rs.YamlSerializerOptions s_real = new() { ReferenceHandling = Rs.YamlReferenceHandling.Preserve };
    private static readonly Cs.YamlSerializerOptions s_compat = new() { ReferenceHandling = Cs.YamlReferenceHandling.Preserve };

    private string _text = string.Empty;

    [GlobalSetup]
    public void Setup() => (_, _text) = Workloads.Load("k8s-multi-16k");

    [Benchmark(Baseline = true, Description = "SharpYaml")]
    public object? ReadReal() => Rs.YamlSerializer.Deserialize<object>(_text, s_real);

    [Benchmark(Description = "OneYaml compat")]
    public object? ReadCompat() => Cs.YamlSerializer.Deserialize<object>(_text, s_compat);
}

/// <summary>Dump of the kit's typed models and of an untyped tree.</summary>
[MemoryDiagnoser]
[Config(typeof(ParseConfig))]
public class CompatSharpYamlDumpBenchmarks
{
    private static readonly Rs.YamlSerializerOptions s_real = new() { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };
    private static readonly Cs.YamlSerializerOptions s_compat = new() { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };

    private object _model = null!;

    [Params("gha-workflow", "records-4k", "records-64k", "openapi-100k", "k8s-multi-16k")]
    public string Workload { get; set; } = string.Empty;

    private bool IsRecords => Workload.StartsWith("records", StringComparison.Ordinal);

    private bool IsTyped => IsRecords || Workload == "gha-workflow";

    [GlobalSetup]
    public void Setup()
    {
        (byte[] bytes, string text) = Workloads.Load(Workload);
        if (text.Contains("\n---", StringComparison.Ordinal))
        {
            text = text[..text.IndexOf("\n---", StringComparison.Ordinal)];
        }

        _model = IsRecords ? OneYaml.YamlSerializer.Deserialize<List<Record>>(bytes)!
            : Workload == "gha-workflow" ? OneYaml.YamlSerializer.Deserialize<Workflow>(bytes)!
            : OneYaml.YamlSerializer.Deserialize<object>(text)!;
    }

    [Benchmark(Baseline = true, Description = "SharpYaml")]
    public string WriteReal() => IsRecords ? Rs.YamlSerializer.Serialize((List<Record>)_model, s_real)
        : IsTyped ? Rs.YamlSerializer.Serialize((Workflow)_model, s_real)
        : Rs.YamlSerializer.Serialize(_model, s_real);

    [Benchmark(Description = "OneYaml compat")]
    public string WriteCompat() => IsRecords ? Cs.YamlSerializer.Serialize((List<Record>)_model, s_compat)
        : IsTyped ? Cs.YamlSerializer.Serialize((Workflow)_model, s_compat)
        : Cs.YamlSerializer.Serialize(_model, s_compat);

    [Benchmark(Description = "SharpYaml generated")]
    public string WriteRealGenerated() => IsRecords ? Rs.YamlSerializer.Serialize((List<Record>)_model, SharpYamlContext.Default.ListRecord)
        : IsTyped ? Rs.YamlSerializer.Serialize((Workflow)_model, SharpYamlContext.Default.Workflow)
        : Rs.YamlSerializer.Serialize(_model, s_real);
}
