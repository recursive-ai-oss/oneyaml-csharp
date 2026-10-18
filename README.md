# oneyaml-csharp

**Lossless, comment-preserving editing and a System.Text.Json-style
serializer for YAML 1.2, in one .NET package.** Both run on one parser,
so a typed read and an edit never disagree about what a file means.
Pure C#, correct YAML 1.2 by default; nesting, alias expansion and
integer size are bounded unless you lift the limits.

[![NuGet](https://img.shields.io/nuget/v/OneYaml)](https://www.nuget.org/packages/OneYaml)
[![CI](https://github.com/recursive-ai-oss/oneyaml-csharp/actions/workflows/ci.yml/badge.svg)](https://github.com/recursive-ai-oss/oneyaml-csharp/actions/workflows/ci.yml)
[![.NET 8 and 10](https://img.shields.io/badge/.NET-8.0%20%7C%2010.0-512BD4)](docs/stability.md)
[![License](https://img.shields.io/badge/license-MIT%20OR%20Apache--2.0-blue)](#license)

How many times faster oneyaml-csharp is than the strongest other .NET
YAML library on each task, on real configuration files (wall-clock
time, log scale, higher is better; a task where oneyaml-csharp is
slower shows as a slower bar):

<!-- generated:bench-chart -->
<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/recursive-ai-oss/oneyaml-csharp/v1.2.0/docs/img/oneyaml-csharp-hero-dark.svg">
  <img alt="Bar chart: how many times faster oneyaml-csharp is than the strongest competitor on each task, log scale, higher is better. 7 tasks: 5 better, 0 level, 1 mixed, 0 worse, 1 not comparable. Speeds are over real-world workloads. A mark counts every workload. Loses only outside the real-world workloads: unchanged emit." src="https://raw.githubusercontent.com/recursive-ai-oss/oneyaml-csharp/v1.2.0/docs/img/oneyaml-csharp-hero-light.svg">
</picture>

[Full results, with memory and the method →](https://github.com/recursive-ai-oss/oneyaml-csharp/blob/v1.2.0/docs/performance.md)
<!-- end generated -->

## Why oneyaml-csharp

- **Edits that keep your file.** `YamlDocument` changes only what you
  touch, in the old value's style; comments and layout elsewhere stay
  byte for byte. JSON Patch and Merge Patch apply the same way
  ([editing](docs/editing.md), [patches](docs/patches.md)).
- **A serializer you already know.** Options, naming policies,
  converters, polymorphism and the System.Text.Json attributes, with a
  source generator in the package for trimmed and native AOT apps
  ([serializer](docs/serializer.md)).
- **Correct YAML 1.2 by default.** `yes` is a string and `0755` the
  integer 755; YAML 1.1 forms come only from `MergeKeys` or the
  compatibility packages. Checked against the full yaml-test-suite,
  with every deviation listed ([conformance](docs/conformance.md)).
- **Safe on outside input.** Depth, alias expansion and integer size
  are bounded by default; input size, scalar size and document count
  are bounded when you set them, and `YamlLimits.Untrusted` sets them
  ([limits](docs/limits.md)). No unsafe code, no dependencies outside
  .NET, and fuzzed ([fuzzing](tests/OneYaml.Fuzz/README.md)).
- **Fast, with the losses shown.** Ahead of every other .NET YAML
  library at typed load, generic load, dump, document parse, editing
  and writing back an unedited file, through reflection and generated
  metadata alike, and level or lower on memory throughout. Not on
  every file: writing back some unedited files was slower than
  SharpYaml's syntax tree in our run, the generated-metadata column
  was slower on a few tiny files, and a few files are level
  ([every loss](docs/performance.md#results-at-a-glance)).

[How oneyaml-csharp compares](docs/comparison.md) with YamlDotNet,
SharpYaml and VYaml, including when one of them is the better choice.

## Install

```sh
dotnet add package OneYaml
```

Moving from YamlDotNet or SharpYaml? A [compatibility package](#packages)
covers the common serializer and model calls; its guide lists what it
leaves out and what differs.

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

Keys bind in camel case by default. Read [what an edit keeps, and what
it does not](docs/editing.md#what-an-edit-keeps-and-what-it-does-not);
for native AOT, use [generated metadata](docs/serializer.md#source-generation-and-native-aot).

## Where to go

- **Using it:** [serializer](docs/serializer.md),
  [editing](docs/editing.md), [patches](docs/patches.md),
  [limits](docs/limits.md), [reader and emitter](docs/reader-emitter.md).
  The API reference is the XML documentation your IDE shows.
- **Migrating:** [from YamlDotNet](docs/migrate-yamldotnet.md),
  [from SharpYaml](docs/migrate-sharpyaml.md).
- **Choosing a library:** [how oneyaml-csharp compares](docs/comparison.md),
  including where the others win.
- **Evidence:** [benchmark tables](docs/performance.md#results-at-a-glance)
  (the numbers behind the chart, every loss and the method),
  [yaml-test-suite conformance](docs/conformance.md),
  [fuzzing](tests/OneYaml.Fuzz/README.md).
- **What changed, and what stays stable:** [CHANGELOG.md](CHANGELOG.md),
  [stability and supported .NET versions](docs/stability.md).
- **Contributing and security:** [CONTRIBUTING.md](CONTRIBUTING.md),
  [SECURITY.md](SECURITY.md), [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md).
- **Other languages:**
  [oneyaml-go](https://github.com/recursive-ai-oss/oneyaml-go) and
  [oneyaml-rust](https://github.com/recursive-ai-oss/oneyaml-rust), all
  measured by [oneyaml-kit](https://github.com/recursive-ai-oss/oneyaml-kit).

## Packages

| Package | What it is |
|---|---|
| [`OneYaml`](https://www.nuget.org/packages/OneYaml) | The library: `YamlSerializer`, `YamlDocument`, patches, limits. Start here. |
| [`OneYaml.Compat.YamlDotNet`](https://www.nuget.org/packages/OneYaml.Compat.YamlDotNet) | A subset of YamlDotNet's API on top of `OneYaml`: the serializer builders and the representation model, tested against YamlDotNet 18.1.0. Not covered: static contexts, type inspectors, node deserializers, the scanner and comment events; [what is covered and every difference](docs/compat/yamldotnet.md). |
| [`OneYaml.Compat.SharpYaml`](https://www.nuget.org/packages/OneYaml.Compat.SharpYaml) | A subset of SharpYaml's API on top of `OneYaml`: the serializer, its options and attributes, tested against SharpYaml 3.14.0. Not covered: custom converters, polymorphism, generated contexts, the `Model` and `Syntax` namespaces; [what is covered and every difference](docs/compat/sharpyaml.md). |

## License

Licensed under either of [Apache License, Version 2.0](LICENSE-APACHE) or
[MIT license](LICENSE-MIT) at your option.
