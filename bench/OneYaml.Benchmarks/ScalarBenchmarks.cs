using System.Buffers;
using System.Buffers.Text;
using System.Text;
using BenchmarkDotNet.Attributes;
using OneYaml.Emitting;
using OneYaml.Schema;

namespace OneYaml.Benchmarks;

/// <summary>
/// Resolves the plain scalars (keys and values) of real workload files. The scalars are cut out
/// with a line heuristic, which is enough to give the resolver realistic input.
/// </summary>
[MemoryDiagnoser]
public class SchemaResolveBenchmarks
{
    private byte[] _data = [];
    private int[] _offsets = [];
    private int[] _lengths = [];

    [Params("compose-wordpress", "gha-workflow", "helm-ingress-nginx", "openapi-petstore", "prometheus-config")]
    public string Workload { get; set; } = string.Empty;

    [GlobalSetup]
    public void Setup()
    {
        string? kit = Environment.GetEnvironmentVariable("ONEYAML_KIT");
        string path = Path.Combine(kit ?? ".", "bench", "workloads", "real", Workload, "input.yaml");
        if (!File.Exists(path))
        {
            throw new InvalidOperationException("Set ONEYAML_KIT to the conformance kit checkout; missing " + path);
        }

        PlainScalars.Extract(File.ReadAllBytes(path), out _data, out _offsets, out _lengths);
        Console.WriteLine($"// {Workload}: {_offsets.Length} plain scalars");
    }

    [Benchmark(Description = "ResolvePlain per scalar")]
    public int Resolve()
    {
        int sum = 0;
        for (int i = 0; i < _offsets.Length; i++)
        {
            sum += (int)CoreSchema.ResolvePlain(_data.AsSpan(_offsets[i], _lengths[i]));
        }

        return sum;
    }

    [Benchmark(Description = "ResolvePlain + Yaml11 check per scalar")]
    public int ResolveAndYaml11()
    {
        int sum = 0;
        for (int i = 0; i < _offsets.Length; i++)
        {
            ReadOnlySpan<byte> s = _data.AsSpan(_offsets[i], _lengths[i]);
            sum += (int)CoreSchema.ResolvePlain(s) + (Yaml11.LooksNonString(s) ? 1 : 0);
        }

        return sum;
    }

    [Benchmark(Description = "Can be plain (dump) per scalar")]
    public int CanBePlain()
    {
        int sum = 0;
        for (int i = 0; i < _offsets.Length; i++)
        {
            sum += Utf8YamlWriter.CanBePlain(_data.AsSpan(_offsets[i], _lengths[i]), EmitContext.BlockValue, StyleMode.Dump) ? 1 : 0;
        }

        return sum;
    }
}

/// <summary>Number parsing against the runtime's UTF-8 parsers, over 1,000 inputs per call.</summary>
[MemoryDiagnoser]
public class NumberParseBenchmarks
{
    private const int Count = 1000;
    private byte[] _data = [];
    private int[] _offsets = [];
    private int[] _lengths = [];

    [Params("int", "double")]
    public string Kind { get; set; } = string.Empty;

    [GlobalSetup]
    public void Setup()
    {
        var rnd = new Random(1);
        var all = new List<byte[]>();
        for (int i = 0; i < Count; i++)
        {
            string s = Kind == "int"
                ? (rnd.Next(4) == 0 ? rnd.NextInt64().ToString(System.Globalization.CultureInfo.InvariantCulture) : rnd.Next(-100000, 100000).ToString(System.Globalization.CultureInfo.InvariantCulture))
                : (rnd.NextDouble() * Math.Pow(10, rnd.Next(-5, 6))).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            all.Add(Encoding.ASCII.GetBytes(s));
        }

        _data = all.SelectMany(x => x).ToArray();
        _offsets = new int[Count];
        _lengths = new int[Count];
        int at = 0;
        for (int i = 0; i < Count; i++)
        {
            _offsets[i] = at;
            _lengths[i] = all[i].Length;
            at += all[i].Length;
        }
    }

    [Benchmark(Baseline = true, Description = "Core schema parser")]
    public long Core()
    {
        long sum = 0;
        for (int i = 0; i < Count; i++)
        {
            ReadOnlySpan<byte> s = _data.AsSpan(_offsets[i], _lengths[i]);
            if (Kind == "int")
            {
                sum += CoreSchema.TryParseInt64(s, out long l) ? l : 0;
            }
            else
            {
                sum += CoreSchema.TryParseDouble(s, out double d) ? (long)d : 0;
            }
        }

        return sum;
    }

    [Benchmark(Description = "long/double.TryParse(utf8)")]
    public long RuntimeUtf8()
    {
        long sum = 0;
        for (int i = 0; i < Count; i++)
        {
            ReadOnlySpan<byte> s = _data.AsSpan(_offsets[i], _lengths[i]);
            if (Kind == "int")
            {
                sum += long.TryParse(s, out long l) ? l : 0;
            }
            else
            {
                sum += double.TryParse(s, out double d) ? (long)d : 0;
            }
        }

        return sum;
    }

    [Benchmark(Description = "Utf8Parser.TryParse")]
    public long Utf8ParserTryParse()
    {
        long sum = 0;
        for (int i = 0; i < Count; i++)
        {
            ReadOnlySpan<byte> s = _data.AsSpan(_offsets[i], _lengths[i]);
            if (Kind == "int")
            {
                sum += Utf8Parser.TryParse(s, out long l, out _) ? l : 0;
            }
            else
            {
                sum += Utf8Parser.TryParse(s, out double d, out _) ? (long)d : 0;
            }
        }

        return sum;
    }
}

/// <summary>Scalar writing into a reused buffer; the scalars are those of a workload.</summary>
[MemoryDiagnoser]
public class ScalarWriteBenchmarks
{
    private readonly ArrayBufferWriter<byte> _buffer = new(1 << 20);
    private byte[] _data = [];
    private int[] _offsets = [];
    private int[] _lengths = [];
    private byte[][] _multiline = [];

    [GlobalSetup]
    public void Setup()
    {
        string? kit = Environment.GetEnvironmentVariable("ONEYAML_KIT");
        string path = Path.Combine(kit ?? ".", "bench", "workloads", "real", "helm-ingress-nginx", "input.yaml");
        if (!File.Exists(path))
        {
            throw new InvalidOperationException("Set ONEYAML_KIT to the conformance kit checkout; missing " + path);
        }

        PlainScalars.Extract(File.ReadAllBytes(path), out _data, out _offsets, out _lengths);
        _multiline =
        [
            Encoding.UTF8.GetBytes("line one\nline two\nline three\n"),
            Encoding.UTF8.GetBytes("  indented first\nsecond\n\nfourth"),
            Encoding.UTF8.GetBytes("tab\there \"quoted\" \\ back\u0001"),
        ];
    }

    [Benchmark(Description = "WriteString dump, per scalar")]
    public int Strings()
    {
        _buffer.Clear();
        var w = new Utf8YamlWriter(_buffer);
        for (int i = 0; i < _offsets.Length; i++)
        {
            w.WriteString(_data.AsSpan(_offsets[i], _lengths[i]), ScalarWriteStyle.Plain, EmitContext.BlockValue, StyleMode.Dump);
            w.WriteByte((byte)'\n');
        }

        return _buffer.WrittenCount;
    }

    [Benchmark(Description = "WriteString dump, multi-line and escapes")]
    public int Multiline()
    {
        _buffer.Clear();
        var w = new Utf8YamlWriter(_buffer);
        for (int round = 0; round < 100; round++)
        {
            foreach (byte[] v in _multiline)
            {
                w.WriteString(v, ScalarWriteStyle.Plain, EmitContext.BlockValue, StyleMode.Dump);
                w.WriteString(v, ScalarWriteStyle.DoubleQuoted, EmitContext.BlockValue, StyleMode.Dump);
            }
        }

        return _buffer.WrittenCount;
    }

    [Benchmark(Description = "WriteInt64 x1000")]
    public int Ints()
    {
        _buffer.Clear();
        var w = new Utf8YamlWriter(_buffer);
        for (int i = 0; i < 1000; i++)
        {
            w.WriteInt64((i * 7919L) - 500000);
        }

        return _buffer.WrittenCount;
    }

    [Benchmark(Description = "WriteDouble x1000")]
    public int Doubles()
    {
        _buffer.Clear();
        var w = new Utf8YamlWriter(_buffer);
        for (int i = 0; i < 1000; i++)
        {
            w.WriteDouble(i * 0.37 + 1.0 / 3);
        }

        return _buffer.WrittenCount;
    }
}

/// <summary>Cuts plain scalars out of YAML text by lines, for benchmark input only.</summary>
internal static class PlainScalars
{
    public static void Extract(byte[] yaml, out byte[] data, out int[] offsets, out int[] lengths)
    {
        var starts = new List<int>();
        var lens = new List<int>();
        int pos = 0;
        while (pos < yaml.Length)
        {
            int end = Array.IndexOf(yaml, (byte)'\n', pos);
            if (end < 0)
            {
                end = yaml.Length;
            }

            int from = pos;
            pos = end + 1;
            while (from < end && (yaml[from] == (byte)' ' || yaml[from] == (byte)'-' && from + 1 < end && yaml[from + 1] == (byte)' '))
            {
                from++;
            }

            if (from >= end || yaml[from] == (byte)'#')
            {
                continue;
            }

            Span<byte> line = yaml.AsSpan(from, end - from);
            int colon = line.IndexOf(": "u8);
            if (colon < 0 && line.Length > 0 && line[^1] == (byte)':')
            {
                colon = line.Length - 1;
            }

            if (colon > 0)
            {
                Add(from, colon, starts, lens, yaml);
                int vFrom = colon + 2;
                if (vFrom < line.Length)
                {
                    Add(from + vFrom, line.Length - vFrom, starts, lens, yaml);
                }
            }
            else if (colon < 0)
            {
                Add(from, line.Length, starts, lens, yaml);
            }
        }

        data = yaml;
        offsets = [.. starts];
        lengths = [.. lens];
    }

    private static void Add(int from, int length, List<int> starts, List<int> lens, byte[] yaml)
    {
        ReadOnlySpan<byte> s = yaml.AsSpan(from, length);
        int hash = s.IndexOf(" #"u8);
        if (hash >= 0)
        {
            s = s.Slice(0, hash);
        }

        s = s.Trim((byte)' ');
        s = s.TrimEnd((byte)'\r');
        if (s.IsEmpty || s[0] is (byte)'"' or (byte)'\'' or (byte)'&' or (byte)'*' or (byte)'!' or (byte)'|' or (byte)'>' or (byte)'[' or (byte)'{' or (byte)'#')
        {
            return;
        }

        int offset = from + (yaml.AsSpan(from, length).IndexOf(s));
        starts.Add(offset);
        lens.Add(s.Length);
    }
}
