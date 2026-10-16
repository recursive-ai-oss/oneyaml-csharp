# Migrating from YamlDotNet

There are two ways to move code written against YamlDotNet:

1. **Swap to the adapter.** `OneYaml.Compat.YamlDotNet` provides the YamlDotNet serializer builders and representation model under new namespaces. For the API listed below, changing the package and the `using` lines keeps your call sites, and YAML 1.1 forms read as YamlDotNet reads them.
2. **Move to the `OneYaml` API.** `YamlSerializer` and `YamlDocument` are YAML 1.2, bounded for untrusted input, and can edit a file without losing its comments. This is the route for new code.

You can do the first now and the second file by file: the two packages work side by side.

## Swap to the adapter

```sh
dotnet remove package YamlDotNet
dotnet add package OneYaml.Compat.YamlDotNet --prerelease
```

| Before | After |
|---|---|
| `using YamlDotNet.Serialization;` | `using OneYaml.Compat.YamlDotNet.Serialization;` |
| `using YamlDotNet.Serialization.NamingConventions;` | `using OneYaml.Compat.YamlDotNet.Serialization.NamingConventions;` |
| `using YamlDotNet.RepresentationModel;` | `using OneYaml.Compat.YamlDotNet.RepresentationModel;` |
| `using YamlDotNet.Core;` | `using OneYaml.Compat.YamlDotNet.Core;` |

Before, with YamlDotNet 18.1.0:

<!-- sample: YamlDotNetBefore -->
```csharp
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

IDeserializer deserializer = new DeserializerBuilder()
    .WithNamingConvention(CamelCaseNamingConvention.Instance)
    .IgnoreUnmatchedProperties()
    .Build();
var service = deserializer.Deserialize<Service>("name: web\nreplicas: 2\nlegacy: true\n");

ISerializer serializer = new SerializerBuilder()
    .WithNamingConvention(CamelCaseNamingConvention.Instance)
    .Build();
Console.Write(serializer.Serialize(service));
// name: web
// replicas: 2

class Service
{
    public string Name { get; set; } = "";
    public int Replicas { get; set; }
}
```

After, with the adapter; only the `using` lines changed, and the output is the same:

<!-- sample: YamlDotNetAfter -->
```csharp
using OneYaml.Compat.YamlDotNet.Serialization;
using OneYaml.Compat.YamlDotNet.Serialization.NamingConventions;

IDeserializer deserializer = new DeserializerBuilder()
    .WithNamingConvention(CamelCaseNamingConvention.Instance)
    .IgnoreUnmatchedProperties()
    .Build();
var service = deserializer.Deserialize<Service>("name: web\nreplicas: 2\nlegacy: true\n");

ISerializer serializer = new SerializerBuilder()
    .WithNamingConvention(CamelCaseNamingConvention.Instance)
    .Build();
Console.Write(serializer.Serialize(service));
// name: web
// replicas: 2

class Service
{
    public string Name { get; set; } = "";
    public int Replicas { get; set; }
}
```

### What the adapter covers

- **`Serialization`:** `DeserializerBuilder` and `SerializerBuilder` with `WithNamingConvention`, `WithEnumNamingConvention`, `WithTypeConverter`, `WithTagMapping`, `WithAttributeOverride`, `IgnoreUnmatchedProperties`, `WithCaseInsensitivePropertyMatching`, `WithDuplicateKeyChecking`, `WithAttemptingUnquotedStringTypeDeserialization`, `WithMaximumRecursion`, `WithTypeMapping`, `WithObjectFactory`, `ConfigureDefaultValuesHandling`, `WithQuotingNecessaryStrings`, `WithDefaultScalarStyle`, `DisableAliases`, `JsonCompatible`, `WithNewLine` and `WithIndentedSequences`; `IDeserializer`, `ISerializer`, the six naming conventions, `[YamlMember]`, `[YamlIgnore]`, `[YamlConverter]`, `IYamlTypeConverter` and `IYamlConvertible`.
- **`RepresentationModel`:** `YamlStream`, `YamlDocument`, `YamlMappingNode`, `YamlSequenceNode`, `YamlScalarNode`, visitors, node positions.
- **`Core`:** `Parser`, `Emitter`, the event classes, `Mark` and the exception types.

The [list of differences](compat/yamldotnet.md#surface-not-provided-gap) names what is not provided, such as the scanner, `MergingParser`, type inspectors, node deserializers and static contexts. Code that uses them does not compile, so the gap shows at build time.

The adapter's representation model reads a file and writes it back as it was when nothing changed:

<!-- sample: YamlDotNetModel -->
```csharp
using OneYaml.Compat.YamlDotNet.RepresentationModel;

var stream = new YamlStream();
stream.Load(new StringReader("# service settings\nname: web\nreplicas: 2   # set by CI\n"));

var root = (YamlMappingNode)stream.Documents[0].RootNode;
Console.WriteLine(root["replicas"]); // 2

var text = new StringWriter();
stream.Save(text);               // nothing was changed, so the text comes back as it was read
Console.Write(text);
// # service settings
// name: web
// replicas: 2   # set by CI
```

After you change a node, `Save` writes the model again without comments, as YamlDotNet does. To change a file and keep its comments, use `YamlDocument` ([below](#editing-a-file)).

### Check after the swap

The adapter reproduces YamlDotNet's documented behaviour, not its defects. Read [the full list of differences](compat/yamldotnet.md), each with a repro, and check these first:

- **Output that reads back.** Strings that a reader would take for something else are quoted (`'true'`, `'yes'`, `'123'`), characters that YamlDotNet writes in a way that changes on reading are escaped, and long keys are written so that they read back. Snapshot tests of serializer output may need updating.
- **Comments survive an unchanged round trip.** `YamlStream.Save` returns the loaded text when nothing changed; YamlDotNet writes it again without comments and with `...` after each document.
- **More valid YAML is read.** Some inputs that YamlDotNet refuses or throws `ArgumentException` on are valid YAML 1.2 and are read.
- **Limits stay on.** The alias budget and depth limit always apply, and `WithMaximumRecursion` cannot raise the depth above the library limit. A billion-laughs input fails with a `YamlException` instead of using memory without bound. There is no setting for untrusted input; read such input with the `OneYaml` API and `YamlLimits.Untrusted`.
- **Exception classes.** `SyntaxErrorException`, `SemanticErrorException`, `MaximumRecursionLevelReachedException` and `AnchorNotFoundException` are raised, but which class a given error gets can differ from YamlDotNet. Catch `YamlException` if you can.
- **Line breaks.** The serializer writes the platform line break, as YamlDotNet does.

## Move to the OneYaml API

```sh
dotnet add package OneYaml --prerelease
```

<!-- sample: YamlDotNetPrimary -->
```csharp
using OneYaml;

// camelCase keys and skipping unknown keys are the defaults; no builder is needed.
var service = YamlSerializer.Deserialize<Service>("name: web\nreplicas: 2\nlegacy: true\n")!;
Console.Write(YamlSerializer.Serialize(service));
// name: web
// replicas: 2

class Service
{
    public string Name { get; set; } = "";
    public int Replicas { get; set; }
}
```

| YamlDotNet | OneYaml |
|---|---|
| `new DeserializerBuilder().Build().Deserialize<T>(yaml)` | `YamlSerializer.Deserialize<T>(yaml)` |
| `new SerializerBuilder().Build().Serialize(value)` | `YamlSerializer.Serialize(value)` |
| `WithNamingConvention(CamelCaseNamingConvention.Instance)` | the default (`PropertyNamingPolicy = JsonNamingPolicy.CamelCase`) |
| `UnderscoredNamingConvention`, `HyphenatedNamingConvention` | `JsonNamingPolicy.SnakeCaseLower`, `JsonNamingPolicy.KebabCaseLower` |
| `NullNamingConvention` (YamlDotNet's default) | `PropertyNamingPolicy = null` |
| `IgnoreUnmatchedProperties()` | the default; `UnmappedMemberHandling = Disallow` is YamlDotNet's default behaviour |
| `WithCaseInsensitivePropertyMatching()` | `PropertyNameCaseInsensitive = true` |
| `WithDuplicateKeyChecking()` | always on: a repeated key is an error |
| `WithMaximumRecursion(n)` | `Limits = YamlLimits.Default with { MaxDepth = n }` |
| `ConfigureDefaultValuesHandling(OmitNull)` | `DefaultIgnoreCondition = YamlIgnoreCondition.WhenWritingNull` |
| `WithTypeConverter(IYamlTypeConverter)` | `Converters = { new MyConverter() }`, a `YamlConverter<T>` ([Converters](serializer.md#converters)) |
| `WithTagMapping("!x", typeof(X))` | `[YamlPolymorphic(DiscriminatorKind = YamlDiscriminatorKind.Tag)]` and `[YamlDerivedType(typeof(X), "x")]` on a base type ([Polymorphism](serializer.md#polymorphism)) |
| `[YamlMember(Alias = "x")]`, `[YamlMember(Order = n)]` | `[YamlPropertyName("x")]`, `[YamlPropertyOrder(n)]` |
| `[YamlIgnore]` | `[YamlIgnore]` (in the `OneYaml` namespace) |
| `MergingParser` | `MergeKeys = true` |
| `YamlStream`, `YamlMappingNode`, `YamlScalarNode` | `YamlDocument`, `YamlNode` ([Editing](editing.md)) |
| `YamlException.Start` | `YamlException.Line`, `Column`, `Offset`, plus `Category` and `Path` |
| static builders with a separate generator package | `[YamlSerializable]` on a `YamlSerializerContext`; the generator is in the package ([Source generation](serializer.md#source-generation-and-native-aot)) |

Behaviour to expect when you move:

- **YAML 1.2, not 1.1.** `yes`, `on` and `y` are strings, so reading `enabled: yes` into a `bool` is an error; write `true`. `017` is 17, not octal 15; `0o17` is 15.
- **Untyped values have types.** `Deserialize<object>` gives `long`, `double`, `bool` and `null` for plain scalars, where YamlDotNet gives strings ([Untyped values](serializer.md#untyped-values)).
- **Repeated keys are an error**, where YamlDotNet keeps the last one by default.
- **`null` members are written** as `null` unless you set `DefaultIgnoreCondition`.

## Editing a file

YamlDotNet's representation model writes a file again from the model, so comments and blank lines are lost when you save a change. `YamlDocument` changes only the value you set:

<!-- sample: YamlDotNetStreamToDocument -->
```csharp
// Instead of YamlStream: load, change one value, save, and keep the comments.
using var doc = YamlDocument.Parse("# service settings\nname: web\nreplicas: 2   # set by CI\n");
doc.Root["replicas"] = 3;
Console.Write(doc);
// # service settings
// name: web
// replicas: 3   # set by CI
```

See [Editing](editing.md) for pointers, styles and aliases, and [Patches](patches.md) to apply a JSON Patch.
