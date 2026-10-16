# Changelog

All notable changes are listed here, newest first. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/). A change that can break your code is marked **Breaking** and says what to write instead. What a release promises, and the supported .NET versions, are under [Stability](README.md#stability).

## [Unreleased]

## [1.0.0] - 2026-10-16

The first release: a YAML 1.2 library for .NET 8 and .NET 10 that reads and writes .NET types, and edits a file while keeping its comments and layout. From this release the public API follows the promises under [Stability](https://github.com/recursive-ai-oss/oneyaml-csharp/blob/v1.0.0/README.md#stability).

### Added

- **The `OneYaml` package.**
  - `YamlSerializer` reads YAML into .NET types and writes .NET values as YAML, in the shape of System.Text.Json: `YamlSerializerOptions`, naming policies (camel case by default), custom converters, polymorphism by key or by tag, extension data, and the System.Text.Json attributes where no YAML attribute is present. A source generator in the package writes metadata for trimmed and native AOT applications. See [Serializer](https://github.com/recursive-ai-oss/oneyaml-csharp/blob/v1.0.0/docs/serializer.md).
  - `YamlDocument` edits a file through `YamlNode` indexers or RFC 6901 pointer methods and keeps every byte you do not change. A new value is written in the style of the old one, or the nearest style that can hold it. See [Editing](https://github.com/recursive-ai-oss/oneyaml-csharp/blob/v1.0.0/docs/editing.md).
  - `ApplyPatch` (JSON Patch, RFC 6902) and `ApplyMergePatch` (JSON Merge Patch, RFC 7396) change a `YamlDocument` atomically and keep its layout. See [Patches](https://github.com/recursive-ai-oss/oneyaml-csharp/blob/v1.0.0/docs/patches.md).
  - `YamlConverter<T>` with `Utf8YamlReader` for custom types, and `Utf8YamlEmitter` to write YAML token by token. See [Reader and emitter](https://github.com/recursive-ai-oss/oneyaml-csharp/blob/v1.0.0/docs/reader-emitter.md).
  - `YamlException` reports bad or oversized input with a stable `Category`, the line, column and byte offset, and the path of a value that failed to bind.
- **The `OneYaml.Compat.YamlDotNet` package**, for code written against YamlDotNet 18: the serializer builders and the representation model under `OneYaml.Compat.YamlDotNet` namespaces. See [Migrating from YamlDotNet](https://github.com/recursive-ai-oss/oneyaml-csharp/blob/v1.0.0/docs/migrate-yamldotnet.md) and the [differences from YamlDotNet](https://github.com/recursive-ai-oss/oneyaml-csharp/blob/v1.0.0/docs/compat/yamldotnet.md).
- **The `OneYaml.Compat.SharpYaml` package**, for code written against the SharpYaml 3 serializer: `YamlSerializer`, `YamlSerializerOptions` and the attributes under the `OneYaml.Compat.SharpYaml` namespace. See [Migrating from SharpYaml](https://github.com/recursive-ai-oss/oneyaml-csharp/blob/v1.0.0/docs/migrate-sharpyaml.md) and the [differences from SharpYaml](https://github.com/recursive-ai-oss/oneyaml-csharp/blob/v1.0.0/docs/compat/sharpyaml.md).
- **Limits.** `YamlLimits` has the presets `Default`, `Untrusted` and `None`. `Default` applies whenever you pass no limits and bounds nesting depth, alias expansion and the digits of an integer. `Untrusted` also bounds input size, scalar size and documents per stream; use it for input you do not control. The adapters read with `Default` unless you give them limits through members their targets do not have: `WithLimits`, and limits overloads of `YamlStream.Load` and the `Parser` constructor, for YamlDotNet code; `YamlSerializerOptions.Limits` for SharpYaml code. See [Limits](https://github.com/recursive-ai-oss/oneyaml-csharp/blob/v1.0.0/docs/limits.md).

### Behaviour to know before you upgrade from another library

- **YAML 1.2 Core schema by default.** Plain `yes` is a string, `0755` is the integer 755, a key written twice in a mapping is an error, and `<<` merges only with the `MergeKeys` option. The compatibility adapters read the YAML 1.1 forms their targets document.
- **`test` in a patch compares YAML values, not JSON values.** An integer and a float are different values, so `1` does not equal `1.0`, and a plain key `80` is an integer that does not match a member `"80"`. See [Differences from RFC 6902](https://github.com/recursive-ai-oss/oneyaml-csharp/blob/v1.0.0/docs/patches.md#differences-from-rfc-6902).
- **What an edit keeps.** Removing an entry also removes the comment lines just above it, replacing a whole mapping or sequence discards the comments inside it, and writes through an alias are refused. There is no API to read or write comments. See [What an edit keeps](https://github.com/recursive-ai-oss/oneyaml-csharp/blob/v1.0.0/docs/editing.md#what-an-edit-keeps-and-what-it-does-not).
- **The adapters refuse what they cannot do.** Options an adapter cannot follow, such as YamlDotNet's `EnsureRoundtrip` and SharpYaml's layout options, throw `NotSupportedException` instead of being ignored. Each is listed on the adapter's differences page.

[Unreleased]: https://github.com/recursive-ai-oss/oneyaml-csharp/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/recursive-ai-oss/oneyaml-csharp/releases/tag/v1.0.0
