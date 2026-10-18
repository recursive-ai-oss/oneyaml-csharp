# OneYaml

**Lossless, comment-preserving editing and a System.Text.Json-style
serializer for YAML 1.2, in one .NET package.** Both run on one parser,
so a typed read and an edit never disagree about what a file means.
Pure C#, correct YAML 1.2 by default, bounded on any input.

How many times faster OneYaml is than the strongest other .NET YAML
library on each task (wall-clock time, log scale, higher is better; a
task where OneYaml is slower shows as a slower bar):

![Bar chart: how many times faster OneYaml is than the strongest other .NET YAML library on each task, wall-clock time, log scale, higher is better. The numbers are in the linked results.](https://raw.githubusercontent.com/recursive-ai-oss/oneyaml-csharp/v1.2.0/docs/img/oneyaml-csharp-hero-light.svg)

[Full results, with every loss and the method →](https://github.com/recursive-ai-oss/oneyaml-csharp/blob/v1.2.0/docs/performance.md#results-at-a-glance)

## Install

```sh
dotnet add package OneYaml
```

## Quick start

Change two values in a file, keeping the comment, the block scalar and
every other byte as they were; then bind the original text to a record.

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

- [README](https://github.com/recursive-ai-oss/oneyaml-csharp/blob/v1.2.0/README.md): why OneYaml and where to go;
  [stability and supported .NET versions](https://github.com/recursive-ai-oss/oneyaml-csharp/blob/v1.2.0/docs/stability.md)
- [Serializer](https://github.com/recursive-ai-oss/oneyaml-csharp/blob/v1.2.0/docs/serializer.md), [Editing](https://github.com/recursive-ai-oss/oneyaml-csharp/blob/v1.2.0/docs/editing.md),
  [Patches](https://github.com/recursive-ai-oss/oneyaml-csharp/blob/v1.2.0/docs/patches.md), [Limits](https://github.com/recursive-ai-oss/oneyaml-csharp/blob/v1.2.0/docs/limits.md)
- Migrating from [YamlDotNet](https://github.com/recursive-ai-oss/oneyaml-csharp/blob/v1.2.0/docs/migrate-yamldotnet.md) or
  [SharpYaml](https://github.com/recursive-ai-oss/oneyaml-csharp/blob/v1.2.0/docs/migrate-sharpyaml.md);
  [how OneYaml compares](https://github.com/recursive-ai-oss/oneyaml-csharp/blob/v1.2.0/docs/comparison.md)
- [Changelog](https://github.com/recursive-ai-oss/oneyaml-csharp/blob/main/CHANGELOG.md) and [security policy](https://github.com/recursive-ai-oss/oneyaml-csharp/blob/main/SECURITY.md)

Report problems at [GitHub issues](https://github.com/recursive-ai-oss/oneyaml-csharp/issues).
Licensed under MIT or Apache-2.0, at your option.
