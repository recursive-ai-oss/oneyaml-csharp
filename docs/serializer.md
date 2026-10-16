# Serializer

`YamlSerializer` reads YAML into .NET types and writes .NET values as YAML. Its shape is the shape of System.Text.Json: static `Serialize` and `Deserialize` methods, an immutable options object, naming policies, converters, attributes, and a source generator for trimmed and native AOT applications. If you know `JsonSerializer`, most of this page will look familiar; the sections below say where YAML differs.

The samples on this page are complete programs. They assume implicit usings (the default for new projects), and the ones that use `JsonNamingPolicy` or the System.Text.Json attributes also need `using System.Text.Json;` or `using System.Text.Json.Serialization;`.

## Reading and writing

<!-- sample: SerializerBasics -->
```csharp
using OneYaml;

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

class Job
{
    public string Name { get; set; } = "";
    public string? Image { get; set; }
    public List<string> Steps { get; set; } = [];
    public Dictionary<string, string> Env { get; set; } = [];
}
```

Things to know:

- **Input and output.** `Deserialize` reads a `string`, a `ReadOnlySpan<char>`, UTF-8 bytes (`ReadOnlySpan<byte>`; UTF-16 and UTF-32 are detected by a byte order mark or by their zero bytes), a `Stream` (also `DeserializeAsync`) or a `TextReader`. `Serialize` writes a `string`, UTF-8 bytes (`SerializeToUtf8Bytes`), a `Stream` (also `SerializeAsync`), a `TextWriter` or an `IBufferWriter<byte>`.
- **One document.** `Deserialize` reads exactly one document; a stream that holds more is an error that names `DeserializeAll` ([Several documents](#several-documents)).
- **What is written.** Block style, two spaces per level, and a line break at the end. A string that a YAML 1.1 reader would take for something else is quoted, so `"true"`, `"yes"` and `"007"` are written as `'true'`, `'yes'` and `'007'`, and the output reads back as the same values under YAML 1.2 and YAML 1.1. A string with line breaks is written as a literal block (`|`). Members whose value is `null` are written as `null` unless you change [`DefaultIgnoreCondition`](#options).
- **What is read.** Plain scalars follow the YAML 1.2 Core schema: `yes` and `on` are strings, `0755` is 755, `0x1F` is 31 and `1_000` is a string. A quoted scalar is always a string, so `"5"` never binds to an `int`. Keys that match no member are skipped unless you set [`UnmappedMemberHandling`](#options). A key that appears twice in a mapping is an error.
- **Records and constructors.** Records, constructor parameters, `init` accessors and `required` members bind without attributes, as in System.Text.Json.
- **Dates.** A `DateTime` read from a timestamp with an offset is converted to the local time of the machine, as System.Text.Json does; use `DateTimeOffset` to keep the offset.

## Options

`YamlSerializerOptions` uses `init` accessors and is read-only once it has been used. Create an instance once and reuse it: type metadata is built once per instance. To change a copy, use `new YamlSerializerOptions(existing) { ... }`.

| Option | Default | What it does |
|---|---|---|
| `PropertyNamingPolicy` | `JsonNamingPolicy.CamelCase` | How member names become keys, for reading and writing. `null` keeps names as declared. |
| `PropertyNameCaseInsensitive` | `false` | Match keys to members without regard to case, after the naming policy. |
| `UnmappedMemberHandling` | `Skip` | `Disallow` makes a key that matches no member a binding error. |
| `DefaultIgnoreCondition` | `Never` | `WhenWritingNull` or `WhenWritingDefault` leave members out of the output. |
| `EnumNamingPolicy` | `null` | How enum members are named; `null` writes them as declared ([Enums](#enums)). |
| `IndentSize` | `2` | Spaces per level when writing, 1 to 8. |
| `Limits` | `YamlLimits.Default` | The bounds enforced while reading ([Limits](limits.md)). |
| `MergeKeys` | `false` | Merge YAML 1.1 `<<` keys; when off, `<<` is an ordinary key. |
| `SourceName` | `null` | A name, such as a file name, that starts every error message. |
| `Converters` | empty | Custom converters, tried before the built-in ones ([Converters](#converters)). |
| `TypeInfoResolver` | `null` | Where metadata comes from: a generated context, or reflection when `null` ([Source generation](#source-generation-and-native-aot)). |

<!-- sample: SerializerOptions -->
```csharp
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

record Pool(string PoolName, int? MaxConnections = null, string? Comment = null);
```

## Attributes

The YAML attributes are in the `OneYaml` namespace. Where a member has none, the System.Text.Json attribute of the same meaning is used, so a model written for `JsonSerializer` binds without change. When a member has both, the YAML attribute wins.

| YAML attribute | System.Text.Json attribute also honoured | Effect |
|---|---|---|
| `[YamlPropertyName("key")]` | `[JsonPropertyName]` | The key of the member, instead of the naming policy's |
| `[YamlIgnore]`, with an optional `Condition` | `[JsonIgnore]` | Never read or written, or left out when null or default |
| `[YamlRequired]` (and the `required` keyword) | `[JsonRequired]` | A missing key is a binding error |
| `[YamlPropertyOrder(n)]` | `[JsonPropertyOrder]` | The member's position when writing |
| `[YamlConstructor]` | `[JsonConstructor]` | The constructor to bind through |
| `[YamlExtensionData]` | `[JsonExtensionData]`, on `Dictionary<string, object?>` only | Collects keys no member takes ([Extension data](#extension-data)) |
| `[YamlPolymorphic]`, `[YamlDerivedType]` | `[JsonPolymorphic]`, `[JsonDerivedType]` (key form) | [Polymorphism](#polymorphism) |
| `[YamlEnumMemberName("name")]` | `[JsonStringEnumMemberName]` | The text of one enum member |
| `[YamlConverter(typeof(C))]` | none: `[JsonConverter]` converters read JSON tokens | [Converters](#converters) |

<!-- sample: SerializerAttributes -->
```csharp
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

class Probe
{
    [YamlRequired]
    public string Path { get; set; } = "";

    [YamlPropertyName("period-seconds")]
    public int PeriodSeconds { get; set; } = 30;

    [JsonIgnore] // System.Text.Json attributes are honoured where no YAML attribute is present
    public string? Cached { get; set; }
}
```

## Enums

Enum members are written as declared; the property naming policy does not apply to them. Set `EnumNamingPolicy` to rename them all, or `[YamlEnumMemberName]` to rename one. Reading accepts the declared names and the attributed names, in any letter case.

<!-- sample: SerializerEnums -->
```csharp
Console.Write(YamlSerializer.Serialize(new Container("web", PullPolicy.IfNotPresent)));
// name: web
// pull: IfNotPresent

var options = new YamlSerializerOptions { EnumNamingPolicy = JsonNamingPolicy.CamelCase };
Console.Write(YamlSerializer.Serialize(new Container("web", PullPolicy.IfNotPresent), options));
// name: web
// pull: ifNotPresent

Console.WriteLine(YamlSerializer.Deserialize<Container>("name: web\npull: ifnotpresent\n")!.Pull); // IfNotPresent

enum PullPolicy { Always, IfNotPresent, Never }

record Container(string Name, PullPolicy Pull);
```

## Converters

Derive from `YamlConverter<T>` to read and write a type yourself, as you would derive from `JsonConverter<T>`. `Read` gets a `Utf8YamlReader` on the first token of the value and must leave it on the last one; `Write` writes exactly one value to a `Utf8YamlEmitter`. Throw `reader.CreateException(message)` to report bad input: the exception carries the position and path of the value.

<!-- sample: SerializerConverter -->
```csharp
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
```

A converter can also be named by `[YamlConverter(typeof(VersionConverter))]` on a type or a member. When several apply, a converter on the member wins over one in `Converters`, which wins over one on the type; this holds for reflection and for generated metadata. On a generic type the attribute may name an open generic converter, such as `typeof(BoxConverter<>)`, and `YamlConverterFactory` makes converters on demand. `HandleNull` says whether `Read` is called for a null value. [Reader and emitter](reader-emitter.md) shows a converter that reads a mapping token by token.

## Polymorphism

Mark a base type with `[YamlPolymorphic]` and list each derived type with `[YamlDerivedType(typeof(D), "name")]`. Only listed types are ever created, so the text cannot choose an arbitrary .NET type. By default the discriminator is the key `$type`; it may be anywhere in the mapping, and when it is not the first key the reader looks ahead for it, which counts against the alias budget of the [limits](limits.md). Set `TypeDiscriminatorPropertyName` to use another key.

<!-- sample: SerializerPolymorphism -->
```csharp
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

[YamlPolymorphic]
[YamlDerivedType(typeof(Circle), "circle")]
[YamlDerivedType(typeof(Square), "square")]
abstract record Shape;

record Circle : Shape { public double Radius { get; init; } }

record Square : Shape { public double Side { get; init; } }
```

A discriminator that names no listed type is a binding error. YAML can also say the type with a tag, which suits configuration formats that use tags such as `!run`. With `DiscriminatorKind = YamlDiscriminatorKind.Tag`, the derived types may be mappings, sequences or scalars:

<!-- sample: SerializerTags -->
```csharp
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
```

`[JsonPolymorphic]` and `[JsonDerivedType]` are honoured too, always with a key discriminator.

## Extension data

A `Dictionary<string, object?>` or `IDictionary<string, object?>` member marked `[YamlExtensionData]` collects the keys no other member takes, and they are written back after the other members. The values have the untyped shapes of the next section.

<!-- sample: SerializerExtensionData -->
```csharp
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

class Plugin
{
    public string Name { get; set; } = "";

    [YamlExtensionData]
    public Dictionary<string, object?> Settings { get; set; } = [];
}
```

## Untyped values

`Deserialize<object>` gives a fixed set of shapes: `Dictionary<string, object?>` (in document order), `List<object?>`, `string`, `long`, `BigInteger` for integers that do not fit in a `long` (never rounded), `double`, `bool` and `null`.

<!-- sample: SerializerGeneric -->
```csharp
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
```

- A key is its source text, so `80: http` has the key `"80"`. Two keys with the same text are a duplicate-key error. A key that is a mapping or a sequence cannot be a dictionary key; the error points you to `YamlDocument`, which can read it.
- Tags are dropped wherever the target is `object`: `!Sub x` reads as the string `x`. Timestamps stay strings. To see tags, use a converter (`Utf8YamlReader.Tag`) or a `YamlDocument`, which keeps them.
- An alias gives the same instance as its anchor, and every alias counts against the alias budget of the limits. An alias inside the node its own anchor names (`&a [*a]`) is a cycle and a `Syntax` error.

## Several documents

A YAML stream can hold several documents separated by `---`. `DeserializeAll<T>` reads them all, `DeserializeAllAsync<T>` returns them from a stream as an `IAsyncEnumerable<T>`, and `SerializeAll` writes several.

<!-- sample: SerializerStreams -->
```csharp
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
```

## Errors

Every failure caused by the input is a `YamlException`. Its `Message` stands on its own: it says what failed and where, with the `SourceName` first when you set one.

<!-- sample: SerializerErrors -->
```csharp
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
```

`Category` is the stable part to test in code: `Syntax`, `Encoding`, `Limit`, `DuplicateKey`, `UnresolvedTag` and `Binding` come from reading, the others from [editing](editing.md) and [patches](patches.md). `Line` and `Column` are 1-based and count characters; `Offset` is the byte offset in the UTF-8 text. `Path` names the value that failed to bind, in RFC 6901 pointer form. The text of a message is written for people and may change between releases.

## Source generation and native AOT

The overloads that take `YamlSerializerOptions` use reflection, and are marked `[RequiresUnreferencedCode]` and `[RequiresDynamicCode]` for that reason. For a trimmed or native AOT application, declare a context and pass its generated metadata instead:

<!-- sample: SerializerSourceGeneration -->
```csharp
Settings settings = YamlSerializer.Deserialize("retries: 3\nendpoint: https://example.com\n", AppYamlContext.Default.Settings)!;
Console.WriteLine(settings.Retries); // 3

string yaml = YamlSerializer.Serialize(settings with { Retries = 5 }, AppYamlContext.Default.Settings);
Console.Write(yaml);
// retries: 5
// endpoint: https://example.com

record Settings(int Retries, Uri Endpoint);

[YamlSerializable(typeof(Settings))]
partial class AppYamlContext : YamlSerializerContext;
```

- The generator ships in the `OneYaml` package; there is nothing else to install. It writes a `YamlTypeInfo<T>` property on the context for each `[YamlSerializable]` type and for every type those reach.
- Generated and reflection metadata bind the same way: the attributes, converters, polymorphism and extension data above work with both.
- For options other than the defaults, create the context with them, `new AppYamlContext(new YamlSerializerOptions { ... })`, or set `TypeInfoResolver = AppYamlContext.Default` on options you already pass around.
- In an application published with `PublishTrimmed` or `PublishAot`, reflection-based binding is off by default, because it cannot work reliably once the trimmer has removed members. Set the MSBuild property `YamlSerializerIsReflectionEnabledByDefault` to `true` to keep it.
- Mistakes in a context are reported at build time as diagnostics `OY0001` to `OY0010`, such as a context that is not `partial` or a derived type that is not listed correctly.
- `YamlDocument` has generated-metadata overloads too: `Set(path, value, typeInfo)`, `Get(path, typeInfo)` and `YamlNode.Deserialize(typeInfo)` ([Editing](editing.md)).
