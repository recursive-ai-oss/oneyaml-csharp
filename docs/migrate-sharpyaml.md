# Migrating from SharpYaml

SharpYaml 3 and OneYaml share the System.Text.Json shape, so most code moves with few changes. There are two ways:

1. **Swap to the adapter.** `OneYaml.Compat.SharpYaml` provides SharpYaml's `YamlSerializer`, `YamlSerializerOptions`, attributes and exceptions under a new namespace. For the API listed below, changing the package and the `using` lines keeps your call sites.
2. **Move to the `OneYaml` API.** It adds what the adapter leaves out (converters, polymorphism, extension data, generated contexts) and a `YamlDocument` that edits a file without losing its comments. This is the route for new code.

## Before you swap

A build that passes after the swap is not yet a finished migration. Check these four things, in this order:

| Check | What to look for |
|---|---|
| **Compile gaps** | Custom converters (`YamlConverter<T>`, `YamlReader`, `YamlWriter`), the `IYamlOn*` hooks, polymorphism, extension data, generated contexts (`Ctx.Default` does not exist), and the `Model` and `Syntax` namespaces are not provided, so code that uses them fails to build. The [full list](compat/sharpyaml.md#what-is-left-out). |
| **Runtime rejections** | Some options compile but throw `NotSupportedException` at first use: a schema other than Core, `DuplicateKeyHandling` other than the default, `DictionaryKeyPolicy`, `Populate`, some `DefaultIgnoreCondition` values, `UnsafeAllowDeserializeFromTagTypeName` and a custom `TypeInfoResolver`. The layout options the writer cannot follow are refused too: `BlockSequenceMappingStyle` other than `Default` or `Compact`, `BlockSequenceSequenceStyle` other than `Default` or `Expanded`, `ScalarStylePreferences` with `PreferPlainStyle` or `PreferQuotedForAmbiguousScalars` set, and `WriteIndented = false`. `ReferenceHandling` other than `None` is refused when writing; reading follows aliases with any value. |
| **Semantic changes** | Output can differ (whole floats, quoting of YAML 1.1 look-alikes), overflowing numbers are an error, and encodings without a byte order mark are detected. See [Check after the swap](#check-after-the-swap) and the [list of differences](compat/sharpyaml.md). |
| **Safety limits** | `MaxDepth` defaults to 64, as in SharpYaml, and is never raised above the library limit. For input you do not control, set `Limits = YamlLimits.Untrusted` ([Input you do not control](#input-you-do-not-control)). `Limits` is an addition: SharpYaml has no such option. |

## Swap to the adapter

```sh
dotnet remove package SharpYaml
dotnet add package OneYaml.Compat.SharpYaml
```

| Before | After |
|---|---|
| `using SharpYaml;` | `using OneYaml.Compat.SharpYaml;` |
| `using SharpYaml.Serialization;` | `using OneYaml.Compat.SharpYaml.Serialization;` |

Before, with SharpYaml 3.14.0:

<!-- sample: SharpYamlBefore -->
```csharp
using System.Text.Json;
using SharpYaml;

var options = new YamlSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
var service = YamlSerializer.Deserialize<Service>("name: web\nreplicas: 2\n", options)!;
Console.Write(YamlSerializer.Serialize(service with { Replicas = 3 }, options));
// name: web
// replicas: 3

record Service(string Name, int Replicas);
```

After, with the adapter; only the `using` line changed, and the output is the same:

<!-- sample: SharpYamlAfter -->
```csharp
using System.Text.Json;
using OneYaml.Compat.SharpYaml;

var options = new YamlSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
var service = YamlSerializer.Deserialize<Service>("name: web\nreplicas: 2\n", options)!;
Console.Write(YamlSerializer.Serialize(service with { Replicas = 3 }, options));
// name: web
// replicas: 3

record Service(string Name, int Replicas);
```

### What the adapter covers

- `YamlSerializer.Serialize`, `Deserialize` and `TryDeserialize` over strings, spans, readers, writers and streams, with `Type` and generic forms.
- `YamlSerializerOptions` and its enums, `ReflectionYamlTypeInfoResolver`, `YamlTypeInfo`.
- The attributes `YamlPropertyName`, `YamlIgnore`, `YamlInclude`, `YamlRequired`, `YamlPropertyOrder`, `YamlConstructor` and `YamlBlockSequenceItemStyle`, with the System.Text.Json attributes as the fallback.
- `YamlException`, `SyntaxErrorException`, `SemanticErrorException` and `Mark`.

What it does not provide, and the options it rejects, are in [Before you swap](#before-you-swap).

### Check after the swap

Read [the full list of differences](compat/sharpyaml.md), each with a repro. The ones most likely to matter:

- **Output.** Whole floats are written with a fraction (`100.0`) and YAML 1.1 look-alikes are quoted (`'y'`, `'on'`).
- **Numbers that overflow** their type are an error, where SharpYaml turns them into infinity.
- **Streams in UTF-16 or UTF-32** without a byte order mark are detected, as YAML 1.2 says.
- **Exceptions.** Syntax errors are `SyntaxErrorException`; every other error is a `YamlException`.

### Input you do not control

`YamlSerializerOptions.Limits` is not in SharpYaml. It sets the [limits](limits.md) of the `OneYaml` library for every read through those options, and when it is `null`, the default, reading uses `YamlLimits.Default`. For uploads, request bodies and files from other systems, set it to `YamlLimits.Untrusted`, which also bounds the input size and the size of a scalar, and bounds alias expansion more tightly. `MaxDepth` can lower the depth further, never raise it. `YamlLimits` is in the `OneYaml` namespace.

<!-- sample: SharpYamlLimits -->
```csharp
using OneYaml.Compat.SharpYaml;

// 17 short lines whose aliases double at each level: a small text that stands for a very large value.
string upload = "a0: &a0 [x, x]\n" + string.Concat(Enumerable.Range(1, 16).Select(i => $"a{i}: &a{i} [*a{i - 1}, *a{i - 1}]\n"));

// Limits is not in SharpYaml. It takes the limits of the OneYaml library; Untrusted is its preset for outside input.
var options = new YamlSerializerOptions { Limits = OneYaml.YamlLimits.Untrusted };
try
{
    YamlSerializer.Deserialize<object>(upload, options);
}
catch (YamlException e)
{
    Console.WriteLine(e.Message);
    // (Lin: 14, Col: 17, Chr: 293) - (Lin: 14, Col: 17, Chr: 293): Expanding the aliases exceeds the budget set by YamlLimits (AliasNodeFactor 2, AliasNodeFloor 100000, AliasByteFactor 2, AliasByteFloor 8388608).
}

// The default limits allow the same text.
Console.WriteLine(YamlSerializer.Deserialize<object>(upload) is not null); // True
```

Code that sets `Limits` no longer compiles against SharpYaml itself.

## Move to the OneYaml API

```sh
dotnet add package OneYaml
```

If the project still references `OneYaml.Compat.SharpYaml`, keep `OneYaml` at the same version: the adapter depends on that exact version.

<!-- sample: SharpYamlPrimary -->
```csharp
using OneYaml;

// camelCase is the default naming policy, so the options can go.
var service = YamlSerializer.Deserialize<Service>("name: web\nreplicas: 2\n")!;
Console.Write(YamlSerializer.Serialize(service with { Replicas = 3 }));
// name: web
// replicas: 3

record Service(string Name, int Replicas);
```

| SharpYaml | OneYaml |
|---|---|
| `SharpYaml.YamlSerializer` | `OneYaml.YamlSerializer` |
| `PropertyNamingPolicy`, `PropertyNameCaseInsensitive`, `DefaultIgnoreCondition`, `IndentSize` | the same names on `YamlSerializerOptions` |
| `UnmappedMemberHandling` (`JsonUnmappedMemberHandling`) | `UnmappedMemberHandling` (`YamlUnmappedMemberHandling`) |
| `MaxDepth` | `Limits = YamlLimits.Default with { MaxDepth = n }` ([Limits](limits.md)) |
| `DuplicateKeyHandling` | a repeated key is always an error; `YamlDocumentOptions.AllowDuplicateKeys` lets you edit such a file |
| `Schema`, `UseSchema` | the Core schema; `MergeKeys = true` for `<<` |
| `ReferenceHandling = Preserve` to read aliases | aliases are read by default, as shared instances, within the alias budget |
| `WriteIndented = false` | no equivalent: output is block style; the compat package throws when writing with it |
| `[YamlSerializable]` on a `YamlSerializerContext` | the same shape, `OneYaml.YamlSerializerContext` ([Source generation](serializer.md#source-generation-and-native-aot)) |
| `YamlConverter<T>` with `YamlReader` and `YamlWriter` | `YamlConverter<T>` with `Utf8YamlReader` and `Utf8YamlEmitter` ([Converters](serializer.md#converters)) |
| `[YamlPolymorphic]`, `[YamlDerivedType]` | the same names, with a key or a tag discriminator ([Polymorphism](serializer.md#polymorphism)) |
| `YamlSyntaxTree.WithTextChange(start, length, text)` | `YamlDocument` indexers and `Set`, which write the YAML text for you ([Editing](editing.md)) |
| `Model.YamlStream` | `YamlDocument` |

Behaviour to expect when you move:

- **The default naming policy differs.** SharpYaml uses member names as declared unless you set a policy; OneYaml uses camel case. If your SharpYaml code sets no `PropertyNamingPolicy`, set `PropertyNamingPolicy = null` to keep the same keys.
- **Aliases are read.** SharpYaml refuses aliases into `object` and dictionaries unless `ReferenceHandling` is `Preserve`; OneYaml reads them as shared instances and bounds their expansion ([Limits](limits.md)).
- **Repeated keys are always an error** in the serializer, and only the Core schema is available.

## Editing a file

SharpYaml's syntax tree keeps every byte outside a range you replace, but you write the YAML text for the new value yourself, and find the range. `YamlDocument` takes a .NET value and writes it in the style of the old one:

<!-- sample: XmlParse -->
```csharp
using var doc = YamlDocument.Parse("name: web   # the public site\nreplicas: 2\n");
doc.Root["replicas"] = 3;
Console.Write(doc);
// name: web   # the public site
// replicas: 3
```

See [Editing](editing.md) and [Patches](patches.md).
