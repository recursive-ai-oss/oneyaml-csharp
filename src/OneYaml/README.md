# OneYaml

**Pure C# YAML 1.2 that edits a file and keeps its comments, with a System.Text.Json-style serializer.**

- `YamlDocument` keeps every byte you do not change. Indexers and RFC 6901 pointer methods write a new value in the style of the old one; JSON Patch (RFC 6902) and JSON Merge Patch (RFC 7396) apply atomically.
- `YamlSerializer` reads and writes .NET types with `YamlSerializerOptions`, naming policies, converters, polymorphism and extension data. The System.Text.Json attributes are honoured where no YAML attribute is present. A source generator in this package covers trimmed and native AOT applications.
- YAML 1.2 Core schema by default. Nesting, alias expansion and integer size are bounded by default; `YamlLimits.Untrusted` adds bounds for input you do not control.
- No dependencies outside the .NET libraries. Targets .NET 8 and .NET 10.

## Quick start

<!-- sample: QuickStart -->
```csharp
using OneYaml;

string src = """
    # Build settings
    script: |
      make build
    replicas: 2      # the CI job scales this
    """;

using var doc = YamlDocument.Parse(src);
doc.Set("/script", "make build\nmake test\n"); // still a block literal
doc.Root["replicas"] = 3;                      // only the 2 changes
Console.WriteLine(doc);
// # Build settings
// script: |
//   make build
//   make test
// replicas: 3      # the CI job scales this

var cfg = YamlSerializer.Deserialize<Config>(src)!;
Console.WriteLine($"{cfg.Replicas} replicas"); // 2 replicas

record Config(int Replicas, string Script);
```

## Documentation

- [README](https://github.com/recursive-ai-oss/oneyaml-csharp/blob/v1.0.0/README.md): benchmarks, conformance, when to use it and when not to
- [Serializer](https://github.com/recursive-ai-oss/oneyaml-csharp/blob/v1.0.0/docs/serializer.md), [Editing](https://github.com/recursive-ai-oss/oneyaml-csharp/blob/v1.0.0/docs/editing.md), [Patches](https://github.com/recursive-ai-oss/oneyaml-csharp/blob/v1.0.0/docs/patches.md), [Limits](https://github.com/recursive-ai-oss/oneyaml-csharp/blob/v1.0.0/docs/limits.md)
- Migrating from [YamlDotNet](https://github.com/recursive-ai-oss/oneyaml-csharp/blob/v1.0.0/docs/migrate-yamldotnet.md) or [SharpYaml](https://github.com/recursive-ai-oss/oneyaml-csharp/blob/v1.0.0/docs/migrate-sharpyaml.md)
- [Changelog](https://github.com/recursive-ai-oss/oneyaml-csharp/blob/main/CHANGELOG.md) and [security policy](https://github.com/recursive-ai-oss/oneyaml-csharp/blob/main/SECURITY.md)

Report problems at [GitHub issues](https://github.com/recursive-ai-oss/oneyaml-csharp/issues). Licensed under MIT or Apache-2.0, at your option.
