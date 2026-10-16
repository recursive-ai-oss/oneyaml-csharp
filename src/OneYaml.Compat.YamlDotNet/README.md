# OneYaml.Compat.YamlDotNet

**A compatibility adapter for code written against YamlDotNet 18, running on OneYaml.**

It provides the YamlDotNet serializer builders (`DeserializerBuilder`, `SerializerBuilder`, naming conventions, `YamlMember`, `IYamlTypeConverter`) and the representation model (`YamlStream`, `YamlMappingNode` and the other nodes) under the `OneYaml.Compat.YamlDotNet` namespaces. For the API the migration guide lists, changing the package and the `using` lines keeps your call sites:

```text
using YamlDotNet.Serialization;      ->  using OneYaml.Compat.YamlDotNet.Serialization;
using YamlDotNet.RepresentationModel; ->  using OneYaml.Compat.YamlDotNet.RepresentationModel;
using YamlDotNet.Core;               ->  using OneYaml.Compat.YamlDotNet.Core;
```

It reads the YAML 1.1 forms YamlDotNet documents for typed targets, and it does not copy YamlDotNet's defects. Some behaviour differs on purpose, and some of YamlDotNet's API is not provided; both are listed with a repro for each. It reads with OneYaml's default limits. `DeserializerBuilder.WithLimits`, and limits overloads of `YamlStream.Load` and the `Parser` constructor, are additions that YamlDotNet does not have: pass them `OneYaml.YamlLimits.Untrusted` for input you do not control. An option the adapter cannot follow, such as `EnsureRoundtrip`, throws `NotSupportedException` instead of being ignored.

- [Migrating from YamlDotNet](https://github.com/recursive-ai-oss/oneyaml-csharp/blob/main/docs/migrate-yamldotnet.md): the swap, what to check after it, and the move to the `OneYaml` API
- [Every difference from YamlDotNet](https://github.com/recursive-ai-oss/oneyaml-csharp/blob/main/docs/compat/yamldotnet.md)
- [OneYaml](https://github.com/recursive-ai-oss/oneyaml-csharp#readme), the library underneath

New code should use the `OneYaml` package. Report problems at [GitHub issues](https://github.com/recursive-ai-oss/oneyaml-csharp/issues). Licensed under MIT or Apache-2.0, at your option.
