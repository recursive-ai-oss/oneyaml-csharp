using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using OneYaml.Parsing;
using OneYaml.Text;

namespace OneYaml.Benchmarks;

/// <summary>
/// Reads every event of a document with each library's event parser. The input files come from
/// the directory named by ONEYAML_WORKLOADS (see the README next to this file).
/// </summary>
[MemoryDiagnoser]
[Config(typeof(ParseConfig))]
public class ParseBenchmarks
{
    private byte[] _bytes = [];
    private string _text = string.Empty;

    [Params(
        "gha-workflow",
        "k8s-multi-16k",
        "openapi-100k",
        "records-64k",
        "long-scalars-256k",
        "scalar-styles-32k",
        "unicode-32k",
        "flow-heavy-64k")]
    public string Workload { get; set; } = string.Empty;

    [GlobalSetup]
    public void Setup()
    {
        string dir = Environment.GetEnvironmentVariable("ONEYAML_WORKLOADS")
            ?? throw new InvalidOperationException("Set ONEYAML_WORKLOADS to the directory holding the workload files.");
        _bytes = File.ReadAllBytes(Path.Combine(dir, Workload + ".yaml"));
        _text = Encoding.UTF8.GetString(_bytes);
    }

    [Benchmark(Baseline = true, Description = "OneYaml events")]
    public int OneYamlEvents()
    {
        using YamlInput input = YamlInput.FromBytes(_bytes, YamlLimits.Default);
        var reader = new YamlReader(input.Utf8Text);
        try
        {
            int n = 0;
            while (reader.Read())
            {
                n++;
            }

            return n;
        }
        finally
        {
            reader.Dispose();
        }
    }

    [Benchmark(Description = "OneYaml events, comments recorded")]
    public int OneYamlEventsWithComments()
    {
        using YamlInput input = YamlInput.FromBytes(_bytes, YamlLimits.Default);
        var reader = new YamlReader(input.Utf8Text, new YamlReaderOptions { RecordComments = true });
        try
        {
            int n = 0;
            while (reader.Read())
            {
                n++;
            }

            return n + reader.CommentCount;
        }
        finally
        {
            reader.Dispose();
        }
    }

    [Benchmark(Description = "OneYaml events + scalar strings")]
    public int OneYamlEventsAndStrings()
    {
        using YamlInput input = YamlInput.FromBytes(_bytes, YamlLimits.Default);
        var reader = new YamlReader(input.Utf8Text);
        try
        {
            int n = 0;
            while (reader.Read())
            {
                if (reader.TokenType == YamlTokenType.Scalar)
                {
                    n += reader.GetString().Length;
                }

                n++;
            }

            return n;
        }
        finally
        {
            reader.Dispose();
        }
    }

    [Benchmark(Description = "VYaml YamlParser")]
    public int VYamlEvents()
    {
        var parser = VYaml.Parser.YamlParser.FromBytes(_bytes);
        int n = 0;
        while (parser.Read())
        {
            n++;
        }

        return n;
    }

    [Benchmark(Description = "VYaml YamlParser + scalar strings")]
    public int VYamlEventsAndStrings()
    {
        var parser = VYaml.Parser.YamlParser.FromBytes(_bytes);
        int n = 0;
        while (parser.Read())
        {
            if (parser.CurrentEventType == VYaml.Parser.ParseEventType.Scalar)
            {
                n += parser.GetScalarAsString()!.Length;
            }

            n++;
        }

        return n;
    }

    [Benchmark(Description = "SharpYaml Parser")]
    public int SharpYamlEvents()
    {
        SharpYaml.IParser parser = SharpYaml.Parser.CreateParser(new StringReader(_text));
        int n = 0;
        while (parser.MoveNext())
        {
            n++;
        }

        return n;
    }

    [Benchmark(Description = "YamlDotNet Parser")]
    public int YamlDotNetEvents()
    {
        var parser = new YamlDotNet.Core.Parser(new StringReader(_text));
        int n = 0;
        while (parser.MoveNext())
        {
            n++;
        }

        return n;
    }
}

// In process, so the run does not depend on where the build output lives.
internal sealed class ParseConfig : ManualConfig
{
    public ParseConfig()
    {
        AddJob(Job.Default
            .WithLaunchCount(1)
            .WithWarmupCount(3)
            .WithIterationCount(12)
            .WithToolchain(InProcessEmitToolchain.Instance));
    }
}
