# Changelog

All notable changes are listed here, newest first. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/). A change that can break your code is marked **Breaking** and says what to write instead. What a release promises, and the supported .NET versions, are under [Stability](README.md#stability).

## [Unreleased]

The first preview of 1.0. Three packages: `OneYaml`, `OneYaml.Compat.YamlDotNet` and `OneYaml.Compat.SharpYaml`, for .NET 8 and .NET 10.

### Added

- `YamlSerializer`: read YAML into .NET types and write .NET values as YAML, from and to strings, spans, UTF-8 bytes, streams (also asynchronously), text readers and writers, and buffer writers; `DeserializeAll` and `SerializeAll` for streams with several documents. YAML 1.2 Core schema by default. See [Serializer](docs/serializer.md).
- `YamlSerializerOptions`: naming policies (`JsonNamingPolicy`, camel case by default), case-insensitive matching, handling of unknown keys, ignore conditions, enum naming, indentation, limits, YAML 1.1 merge keys as an option, a source name for error messages, and custom converters.
- Attributes for names, ignoring, required members, order, constructors, extension data, converters, enum member names and polymorphism by key or by tag. The System.Text.Json attributes of the same meaning are honoured where no YAML attribute is present.
- A source generator, shipped in the `OneYaml` package: `[YamlSerializable]` on a `partial` `YamlSerializerContext` writes metadata for trimmed and native AOT applications.
- `YamlConverter<T>` and `YamlConverterFactory` for custom types, with `Utf8YamlReader` to read tokens and `Utf8YamlEmitter` to write them. See [Reader and emitter](docs/reader-emitter.md).
- `YamlDocument`: parse a stream and write it back byte for byte; edit through `YamlNode` indexers or RFC 6901 pointer methods (`Set`, `Add`, `Remove`, `Get`, `TryGet`, `Contains`, `GetNode`) and keep the comments and layout of everything else. A new value takes the style of the old one or the nearest style that can hold it. Writes through an alias are refused. See [Editing](docs/editing.md).
- `ApplyPatch` (RFC 6902, also through the `YamlPatch` builder) and `ApplyMergePatch` (RFC 7396) on a `YamlDocument`, atomically. See [Patches](docs/patches.md).
- `YamlLimits`, with `Default`, `Untrusted` and `None` presets, on by default on every path that follows aliases. See [Limits](docs/limits.md).
- `YamlException` with a stable `Category`, a line, a column, a byte offset, the path of a value that failed to bind, the source name and, for patches, the index of the failed operation.
- `OneYaml.Compat.YamlDotNet`: a compatibility adapter for code written against YamlDotNet 18, with its serializer builders and representation model. Every difference is listed in [YamlDotNet differences](docs/compat/yamldotnet.md). See [Migrating from YamlDotNet](docs/migrate-yamldotnet.md).
- `OneYaml.Compat.SharpYaml`: a compatibility adapter for code written against the SharpYaml 3 serializer. Every difference is listed in [SharpYaml differences](docs/compat/sharpyaml.md). See [Migrating from SharpYaml](docs/migrate-sharpyaml.md).

[Unreleased]: https://github.com/recursive-ai-oss/oneyaml-csharp/commits/main
