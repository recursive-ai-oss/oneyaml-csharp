# OneYaml.Compat.SharpYaml

**A compatibility adapter for code written against the SharpYaml 3 serializer, running on OneYaml.**

It provides SharpYaml's `YamlSerializer`, `YamlSerializerOptions`, its attributes and exceptions under the `OneYaml.Compat.SharpYaml` namespace. For the API the migration guide lists, changing the package and the `using` lines keeps your call sites:

```text
using SharpYaml;                ->  using OneYaml.Compat.SharpYaml;
using SharpYaml.Serialization;  ->  using OneYaml.Compat.SharpYaml.Serialization;
```

Custom converters, polymorphism, extension data, generated contexts and SharpYaml's document model are not provided; code that uses them does not compile, so the gap shows at build time. Some options fail at first use instead, and some behaviour differs on purpose; each is listed with a repro. It reads with OneYaml's default limits. `YamlSerializerOptions.Limits` is an addition that SharpYaml does not have: set it to `OneYaml.YamlLimits.Untrusted` for input you do not control.

- [Migrating from SharpYaml](https://github.com/recursive-ai-oss/oneyaml-csharp/blob/v1.1.1/docs/migrate-sharpyaml.md): the swap, what to check after it, and the move to the `OneYaml` API
- [Every difference from SharpYaml](https://github.com/recursive-ai-oss/oneyaml-csharp/blob/v1.1.1/docs/compat/sharpyaml.md)
- [OneYaml](https://github.com/recursive-ai-oss/oneyaml-csharp/blob/v1.1.1/README.md), the library underneath

New code should use the `OneYaml` package. Report problems at [GitHub issues](https://github.com/recursive-ai-oss/oneyaml-csharp/issues). Licensed under MIT or Apache-2.0, at your option.
