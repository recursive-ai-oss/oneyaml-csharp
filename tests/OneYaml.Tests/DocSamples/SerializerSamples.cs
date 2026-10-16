#region SerializerBasics
using OneYaml;
#endregion
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace DocSamples.Serializer;

[Collection(nameof(DocSample))]
public class SerializerSamples
{
    [Fact]
    public void SerializerBasics() => DocSample.Run(nameof(SerializerBasics), () =>
    {
        #region SerializerBasics
        var job = YamlSerializer.Deserialize<Job>("""
            name: build
            image: dotnet/sdk:10.0
            steps:
              - dotnet restore
              - dotnet test
            env:
              CI: "true"
            """)!;
        Console.WriteLine($"{job.Name}: {job.Steps.Count} steps"); // build: 2 steps

        job.Steps.Add("dotnet pack");
        Console.Write(YamlSerializer.Serialize(job));
        // name: build
        // image: dotnet/sdk:10.0
        // steps:
        //   - dotnet restore
        //   - dotnet test
        //   - dotnet pack
        // env:
        //   CI: 'true'
        #endregion
    });

    [Fact]
    public void SerializerOptions() => DocSample.Run(nameof(SerializerOptions), () =>
    {
        #region SerializerOptions
        var options = new YamlSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.KebabCaseLower,
            DefaultIgnoreCondition = YamlIgnoreCondition.WhenWritingNull,
            UnmappedMemberHandling = YamlUnmappedMemberHandling.Disallow,
            SourceName = "pool.yaml",
        };

        Console.Write(YamlSerializer.Serialize(new Pool("web", MaxConnections: 100, Comment: null), options));
        // pool-name: web
        // max-connections: 100

        try
        {
            YamlSerializer.Deserialize<Pool>("pool-name: web\nmax-conections: 100\n", options);
        }
        catch (YamlException e)
        {
            Console.WriteLine(e.Message);
            // pool.yaml: The key 'max-conections' is not a member of Pool. (line 2, column 1, path /max-conections)
        }
        #endregion
    });

    [Fact]
    public void SerializerAttributes() => DocSample.Run(nameof(SerializerAttributes), () =>
    {
        #region SerializerAttributes
        var probe = YamlSerializer.Deserialize<Probe>("path: /healthz\nperiod-seconds: 10\n")!;
        Console.WriteLine($"{probe.Path} every {probe.PeriodSeconds}s"); // /healthz every 10s

        try
        {
            YamlSerializer.Deserialize<Probe>("period-seconds: 10\n");
        }
        catch (YamlException e)
        {
            Console.WriteLine(e.Message); // The required key 'path' is missing. (line 1, column 1)
        }
        #endregion
    });

    [Fact]
    public void SerializerEnums() => DocSample.Run(nameof(SerializerEnums), () =>
    {
        #region SerializerEnums
        Console.Write(YamlSerializer.Serialize(new Container("web", PullPolicy.IfNotPresent)));
        // name: web
        // pull: IfNotPresent

        var options = new YamlSerializerOptions { EnumNamingPolicy = JsonNamingPolicy.CamelCase };
        Console.Write(YamlSerializer.Serialize(new Container("web", PullPolicy.IfNotPresent), options));
        // name: web
        // pull: ifNotPresent

        Console.WriteLine(YamlSerializer.Deserialize<Container>("name: web\npull: ifnotpresent\n")!.Pull); // IfNotPresent
        #endregion
    });

    [Fact]
    public void SerializerConverter() => DocSample.Run(nameof(SerializerConverter), () =>
    {
        #region SerializerConverter
        var options = new YamlSerializerOptions { Converters = { new VersionConverter() } };

        var release = YamlSerializer.Deserialize<Release>("name: app\nversion: 2.1.0\n", options)!;
        Console.WriteLine(release.Version.Minor); // 1

        try
        {
            YamlSerializer.Deserialize<Release>("name: app\nversion: two\n", options);
        }
        catch (YamlException e)
        {
            Console.WriteLine(e.Message); // 'two' is not a version. (line 2, column 10, path /version)
        }
        #endregion
    });

    [Fact]
    public void SerializerPolymorphism() => DocSample.Run(nameof(SerializerPolymorphism), () =>
    {
        #region SerializerPolymorphism
        var shapes = YamlSerializer.Deserialize<List<Shape>>("""
            - $type: circle
              radius: 2.0
            - side: 3.0
              $type: square
            """)!;
        Console.WriteLine(string.Join(", ", shapes)); // Circle { Radius = 2 }, Square { Side = 3 }

        Console.Write(YamlSerializer.Serialize<List<Shape>>([new Square { Side = 1.5 }]));
        // - $type: square
        //   side: 1.5
        #endregion
    });

    [Fact]
    public void SerializerTags() => DocSample.Run(nameof(SerializerTags), () =>
    {
        #region SerializerTags
        var steps = YamlSerializer.Deserialize<List<Step>>("""
            - !run make test
            - !copy {from: bin/app, to: /opt/app}
            """)!;
        Console.WriteLine(string.Join(", ", steps)); // Run { Command = make test }, Copy { From = bin/app, To = /opt/app }

        Console.Write(YamlSerializer.Serialize(steps));
        // - !run make test
        // - !copy
        //   from: bin/app
        //   to: /opt/app
        #endregion
    });

    [Fact]
    public void SerializerExtensionData() => DocSample.Run(nameof(SerializerExtensionData), () =>
    {
        #region SerializerExtensionData
        var plugin = YamlSerializer.Deserialize<Plugin>("""
            name: lint
            level: strict
            paths: [src, tests]
            """)!;
        Console.WriteLine(string.Join(", ", plugin.Settings.Keys)); // level, paths

        Console.Write(YamlSerializer.Serialize(plugin));
        // name: lint
        // level: strict
        // paths:
        //   - src
        //   - tests
        #endregion
    });

    [Fact]
    public void SerializerGeneric() => DocSample.Run(nameof(SerializerGeneric), () =>
    {
        #region SerializerGeneric
        var root = (Dictionary<string, object?>)YamlSerializer.Deserialize<object>("""
            port: 8080
            ratio: 0.5
            enabled: yes
            id: 123456789012345678901234567890
            80: http
            tags: [a, b]
            """)!;

        foreach (var (key, value) in root)
        {
            Console.WriteLine($"{key}: {value?.GetType().Name}");
            // port: Int64
            // ratio: Double
            // enabled: String
            // id: BigInteger
            // 80: String
            // tags: List`1
        }
        #endregion
    });

    [Fact]
    public void SerializerStreams() => DocSample.Run(nameof(SerializerStreams), () =>
    {
        #region SerializerStreams
        IReadOnlyList<Pool?> pools = YamlSerializer.DeserializeAll<Pool>("""
            pool-name: web
            ---
            pool-name: jobs
            max-connections: 10
            """, new YamlSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.KebabCaseLower });
        Console.WriteLine(pools.Count); // 2

        Console.Write(YamlSerializer.SerializeAll(["a", "b"]));
        // a
        // ---
        // b
        #endregion
    });

    [Fact]
    public void SerializerErrors() => DocSample.Run(nameof(SerializerErrors), () =>
    {
        #region SerializerErrors
        try
        {
            YamlSerializer.Deserialize<Pool>("poolName: web\nmaxConnections: many\n",
                new YamlSerializerOptions { SourceName = "pools.yaml" });
        }
        catch (YamlException e)
        {
            Console.WriteLine(e.Category);               // Binding
            Console.WriteLine($"{e.Line}:{e.Column}");   // 2:17
            Console.WriteLine(e.Path);                   // /maxConnections
            Console.WriteLine(e.Message);
            // pools.yaml: The value 'many' cannot be read as a 32-bit integer. (line 2, column 17, path /maxConnections)
        }
        #endregion
    });

    [Fact]
    public void SerializerSourceGeneration() => DocSample.Run(nameof(SerializerSourceGeneration), () =>
    {
        #region SerializerSourceGeneration
        Settings settings = YamlSerializer.Deserialize("retries: 3\nendpoint: https://example.com\n", AppYamlContext.Default.Settings)!;
        Console.WriteLine(settings.Retries); // 3

        string yaml = YamlSerializer.Serialize(settings with { Retries = 5 }, AppYamlContext.Default.Settings);
        Console.Write(yaml);
        // retries: 5
        // endpoint: https://example.com
        #endregion
    });
}

#region SerializerBasics
class Job
{
    public string Name { get; set; } = "";
    public string? Image { get; set; }
    public List<string> Steps { get; set; } = [];
    public Dictionary<string, string> Env { get; set; } = [];
}
#endregion

#region SerializerOptions
record Pool(string PoolName, int? MaxConnections = null, string? Comment = null);
#endregion

#region SerializerAttributes
class Probe
{
    [YamlRequired]
    public string Path { get; set; } = "";

    [YamlPropertyName("period-seconds")]
    public int PeriodSeconds { get; set; } = 30;

    [JsonIgnore] // System.Text.Json attributes are honoured where no YAML attribute is present
    public string? Cached { get; set; }
}
#endregion

#region SerializerEnums
enum PullPolicy { Always, IfNotPresent, Never }

record Container(string Name, PullPolicy Pull);
#endregion

#region SerializerConverter
record Release(string Name, Version Version);

sealed class VersionConverter : YamlConverter<Version>
{
    public override Version Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options)
    {
        string text = reader.GetString();
        return Version.TryParse(text, out Version? version)
            ? version
            : throw reader.CreateException($"'{text}' is not a version.");
    }

    public override void Write(Utf8YamlEmitter writer, Version value, YamlSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
#endregion

#region SerializerPolymorphism
[YamlPolymorphic]
[YamlDerivedType(typeof(Circle), "circle")]
[YamlDerivedType(typeof(Square), "square")]
abstract record Shape;

record Circle : Shape { public double Radius { get; init; } }

record Square : Shape { public double Side { get; init; } }
#endregion

#region SerializerTags
[YamlPolymorphic(DiscriminatorKind = YamlDiscriminatorKind.Tag)]
[YamlDerivedType(typeof(Run), "run")]
[YamlDerivedType(typeof(Copy), "copy")]
abstract record Step;

[YamlConverter(typeof(RunConverter))] // a run step is written as a plain string
record Run(string Command) : Step;

record Copy(string From, string To) : Step;

sealed class RunConverter : YamlConverter<Run>
{
    public override Run Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options) =>
        new(reader.GetString());

    public override void Write(Utf8YamlEmitter writer, Run value, YamlSerializerOptions options) =>
        writer.WriteStringValue(value.Command);
}
#endregion

#region SerializerExtensionData
class Plugin
{
    public string Name { get; set; } = "";

    [YamlExtensionData]
    public Dictionary<string, object?> Settings { get; set; } = [];
}
#endregion

#region SerializerSourceGeneration
record Settings(int Retries, Uri Endpoint);

[YamlSerializable(typeof(Settings))]
partial class AppYamlContext : YamlSerializerContext;
#endregion
