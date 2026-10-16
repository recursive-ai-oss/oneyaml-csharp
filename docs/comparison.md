# Comparison

How oneyaml-csharp compares with the .NET YAML libraries it is most often weighed against: YamlDotNet, SharpYaml and VYaml.

**Bias.** This page is written by the authors of oneyaml-csharp. We chose the criteria, and they are the ones this library was built for: YAML 1.2 behaviour, editing a file without disturbing it, and bounded handling of untrusted input. A project with other priorities would draw a different table. We tried to state each library's strengths as its own users would.

**Versions.** This page describes YamlDotNet 18.1.0, SharpYaml 3.14.0 and VYaml 1.4.0. Library facts come from the libraries' own documentation and from a survey we ran against those versions on 2026-09-30. On 2026-10-16 the current releases were YamlDotNet 18.1.0, SharpYaml 3.16.0 and VYaml 1.4.1; we read the release notes and READMEs of the newer two and found nothing that changes a line here, but we did not repeat the behaviour tests (depth limits, aliases) against them. Libraries change, so check the current release before you rely on a line here.

This page compares features and behaviour and has no figures. Speed and memory are in [Performance](performance.md), generated from the kit's release run, whose method line names the library versions it measured; these can be newer than the ones above. Conformance counts are in [Conformance](conformance.md).

## What each library is

| | oneyaml-csharp | YamlDotNet | SharpYaml 3 | VYaml |
|---|---|---|---|---|
| API shape | System.Text.Json style serializer, plus a lossless `YamlDocument` | builders (`DeserializerBuilder`, `SerializerBuilder`) and a representation model (`YamlStream`) | System.Text.Json style serializer, a syntax tree and a model | `[YamlObject]` source-generated serializer and a UTF-8 parser |
| Default YAML version | 1.2, Core schema | a mix of 1.1 and 1.2; typed binding follows 1.1 (`yes` is `true`, `017` is 15) | 1.2, Core schema by default; other schemas selectable | 1.2, "mostly supported" by its own description |
| Targets | .NET 8, .NET 10 | `netstandard2.0` and up, .NET Framework, current .NET | `netstandard2.0` and up, current .NET | Unity first, also NuGet |
| Status | new; 1.0.0 is the first release | the most used .NET YAML library, maintained for many years | maintained; v3 was a breaking redesign in 2026 | maintained, Unity-focused |

## Like with like

Compare libraries on the job they share.

- **Typed load and dump.** All four. oneyaml-csharp and SharpYaml share the System.Text.Json shape (options object, naming policies, System.Text.Json attributes, a source-generated context); YamlDotNet uses builders and its own attributes; VYaml generates code for `[YamlObject]` types. All four have a source generator for native AOT; YamlDotNet's is a separate package.
- **Editing a file and keeping it as it was.** oneyaml-csharp's `YamlDocument` writes a new value in the old one's style and keeps every other byte. SharpYaml's `YamlSyntaxTree.WithTextChange` also keeps every byte outside the range you replace, but you write the YAML text of the new value yourself. YamlDotNet's `YamlStream` and SharpYaml's model keep a scalar's style but lose comments and blank lines when they save. VYaml has no document model.
- **Patching.** oneyaml-csharp applies RFC 6902 and RFC 7396 to the YAML text, atomically. None of the others has a patch API; the usual route is to convert to a JSON tree (for example with Yaml2JsonNode and JsonPatch.Net), which loses formatting and comments.
- **Limits on input.** oneyaml-csharp bounds depth, alias expansion and integer size by default and has a `YamlLimits.Untrusted` preset ([Limits](limits.md)). SharpYaml limits depth (64 by default) and refuses aliases into untyped targets unless you opt in. YamlDotNet's deserializer limits recursion; in our survey its `YamlStream` 18.1.0 had no depth limit, and VYaml had none either: deep input ended the process with a stack overflow.

## Where oneyaml-csharp is the better fit

- You change YAML files that people also edit by hand, and the diff must hold only your change.
- You want to apply a JSON Patch or a merge patch to a YAML file and keep its comments.
- You want YAML 1.2 as the specification says, measured against the yaml-test-suite with every known deviation listed ([Conformance](conformance.md)).
- You read YAML from outside your program and want bounds on by default, with a stricter preset to opt into.
- You already know System.Text.Json and want the same options, attributes and source generation for YAML.

## Where the others are the better fit

- **You target .NET Framework, .NET Standard or Unity.** oneyaml-csharp runs on .NET 8 and .NET 10 only. YamlDotNet and SharpYaml target `netstandard2.0`; VYaml is built for Unity.
- **You need YamlDotNet's extension points.** Type inspectors, node deserializers, event emitters and object-graph visitors let you change how YamlDotNet binds at a level the oneyaml-csharp serializer does not open. The YamlDotNet adapter does not provide them either, and the SharpYaml adapter has no custom converters; each migration guide lists what its adapter is missing.
- **You want maturity and an ecosystem.** YamlDotNet has years of production use, a large number of dependent packages and integrations (configuration providers, for example). oneyaml-csharp is new, and 1.0.0 is its first release.
- **You read or write YAML 1.1 files as YAML 1.1.** YamlDotNet reads `yes` as `true` and `017` as octal in typed binding by default. In oneyaml-csharp that takes the YamlDotNet adapter.
- **You need comments as data.** No library here gives a full comment API, but YamlDotNet's parser can report comment events and its emitter can write them. oneyaml-csharp keeps comments through edits but cannot read or add them.
- **You need a streaming event parser.** YamlDotNet's `Parser`, SharpYaml's parser and VYaml's `YamlParser` are public pull parsers. oneyaml-csharp exposes its reader only inside a converter.
- **You need SharpYaml's schemas.** SharpYaml can select the Failsafe, JSON, Core or an extended schema. oneyaml-csharp reads with the Core schema only.
- **Speed on a given workload.** See [Performance](performance.md) for where oneyaml-csharp is slower; a library that wins on your workload may be the better choice.

## Choosing

- New code on .NET 8 or later that reads configuration, or edits files people maintain: oneyaml-csharp.
- Existing YamlDotNet or SharpYaml code: keep it, or swap to the adapter and move to the `OneYaml` API file by file ([from YamlDotNet](migrate-yamldotnet.md), [from SharpYaml](migrate-sharpyaml.md)).
- .NET Framework or Unity: YamlDotNet, SharpYaml or VYaml.
