using BenchmarkDotNet.Attributes;
using OneYamlKit.Bench.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace OneYaml.Benchmarks;

/// <summary>
/// Dump of the kit's typed models: the OneYaml reflection and generated paths (to a string and to UTF-8 bytes)
/// against VYaml and SharpYaml through generated code and YamlDotNet through reflection. The model is loaded once
/// in setup, so only the writing is timed.
/// </summary>
[MemoryDiagnoser]
[Config(typeof(ParseConfig))]
public class TypedDumpBenchmarks
{
    private static readonly ISerializer s_yamlDotNet = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    private object _model = null!;

    [Params("gha-workflow", "records-4k", "records-64k")]
    public string Workload { get; set; } = string.Empty;

    private bool IsRecords => Workload.StartsWith("records", StringComparison.Ordinal);

    [GlobalSetup]
    public void Setup()
    {
        (byte[] bytes, _) = Workloads.Load(Workload);
        _model = IsRecords ? YamlSerializer.Deserialize<List<Record>>(bytes)! : YamlSerializer.Deserialize<Workflow>(bytes)!;

        // The generated path must write the same text as reflection.
        if (OneYamlGenerated() != WriteOneYaml())
        {
            throw new InvalidOperationException("The two OneYaml paths wrote different text.");
        }
    }

    private string OneYamlGenerated() => IsRecords
        ? YamlSerializer.Serialize((List<Record>)_model, OneYamlContext.Default.ListRecord)
        : YamlSerializer.Serialize((Workflow)_model, OneYamlContext.Default.Workflow);

    [Benchmark(Baseline = true, Description = "OneYaml reflection")]
    public string WriteOneYaml() => IsRecords ? YamlSerializer.Serialize((List<Record>)_model) : YamlSerializer.Serialize((Workflow)_model);

    [Benchmark(Description = "OneYaml reflection (UTF-8)")]
    public byte[] WriteOneYamlBytes() => IsRecords ? YamlSerializer.SerializeToUtf8Bytes((List<Record>)_model) : YamlSerializer.SerializeToUtf8Bytes((Workflow)_model);

    [Benchmark(Description = "OneYaml generated")]
    public string WriteOneYamlGenerated() => OneYamlGenerated();

    [Benchmark(Description = "OneYaml generated (UTF-8)")]
    public byte[] WriteOneYamlGeneratedBytes() => IsRecords
        ? YamlSerializer.SerializeToUtf8Bytes((List<Record>)_model, OneYamlContext.Default.ListRecord)
        : YamlSerializer.SerializeToUtf8Bytes((Workflow)_model, OneYamlContext.Default.Workflow);

    [Benchmark(Description = "VYaml generated")]
    public ReadOnlyMemory<byte> WriteVYaml() => IsRecords
        ? VYaml.Serialization.YamlSerializer.Serialize((List<Record>)_model)
        : VYaml.Serialization.YamlSerializer.Serialize((Workflow)_model);

    [Benchmark(Description = "SharpYaml generated")]
    public string WriteSharpYaml() => IsRecords
        ? SharpYaml.YamlSerializer.Serialize((List<Record>)_model, SharpYamlContext.Default.ListRecord)
        : SharpYaml.YamlSerializer.Serialize((Workflow)_model, SharpYamlContext.Default.Workflow);

    [Benchmark(Description = "YamlDotNet reflection")]
    public string WriteYamlDotNet() => s_yamlDotNet.Serialize(_model);
}

/// <summary>
/// Dump of untyped trees: each library writes the tree it loaded itself, so the values are in the form its own
/// reader gives. OneYaml writes the tree with the reflection options and with generated metadata for <c>object</c>.
/// </summary>
[MemoryDiagnoser]
[Config(typeof(ParseConfig))]
public class GenericDumpBenchmarks
{
    private static readonly ISerializer s_yamlDotNetOut = new SerializerBuilder().Build();

    private static readonly IDeserializer s_yamlDotNetIn = new DeserializerBuilder()
        .WithAttemptingUnquotedStringTypeDeserialization()
        .Build();

    private static readonly SharpYaml.YamlSerializerOptions s_sharpOptions = new() { ReferenceHandling = SharpYaml.YamlReferenceHandling.Preserve };

    private object? _oneYaml;
    private object? _vYaml;
    private object? _sharpYaml;
    private object? _yamlDotNet;

    [Params("openapi-100k", "helm-values-64k", "long-scalars-256k", "scalar-styles-32k", "unicode-32k", "wide-map-64k")]
    public string Workload { get; set; } = string.Empty;

    [GlobalSetup]
    public void Setup()
    {
        (byte[] bytes, string text) = Workloads.Load(Workload);
        _oneYaml = YamlSerializer.Deserialize<object>(bytes);
        _vYaml = VYaml.Serialization.YamlSerializer.Deserialize<object>(bytes);
        _sharpYaml = SharpYaml.YamlSerializer.Deserialize<object>(text, s_sharpOptions);
        _yamlDotNet = s_yamlDotNetIn.Deserialize<object>(text);

        // The text OneYaml writes must be the document it was given.
        object? back = YamlSerializer.Deserialize<object>(YamlSerializer.Serialize(_oneYaml));
        if (back is null || YamlSerializer.Serialize(back) != YamlSerializer.Serialize(_oneYaml))
        {
            throw new InvalidOperationException("The dump does not read back as the same value.");
        }
    }

    [Benchmark(Baseline = true, Description = "OneYaml reflection")]
    public string WriteOneYaml() => YamlSerializer.Serialize(_oneYaml);

    [Benchmark(Description = "OneYaml reflection (UTF-8)")]
    public byte[] WriteOneYamlBytes() => YamlSerializer.SerializeToUtf8Bytes(_oneYaml);

    [Benchmark(Description = "OneYaml generated")]
    public string WriteOneYamlGenerated() => YamlSerializer.Serialize(_oneYaml, OneYamlContext.Default.Object);

    [Benchmark(Description = "OneYaml generated (UTF-8)")]
    public byte[] WriteOneYamlGeneratedBytes() => YamlSerializer.SerializeToUtf8Bytes(_oneYaml, OneYamlContext.Default.Object);

    [Benchmark(Description = "VYaml")]
    public ReadOnlyMemory<byte> WriteVYaml() => VYaml.Serialization.YamlSerializer.Serialize(_vYaml);

    [Benchmark(Description = "SharpYaml")]
    public string WriteSharpYaml() => SharpYaml.YamlSerializer.Serialize(_sharpYaml, s_sharpOptions);

    [Benchmark(Description = "YamlDotNet")]
    public string WriteYamlDotNet() => s_yamlDotNetOut.Serialize(_yamlDotNet!);
}
