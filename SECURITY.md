# Security policy

## Supported versions

Security fixes go into the latest release. Older releases, previews included, are not patched; upgrade to the latest. The supported .NET versions are listed under [Stability](README.md#stability).

## Reporting a vulnerability

Report it privately through GitHub: open the [Security tab](https://github.com/recursive-ai-oss/oneyaml-csharp/security) of this repository and choose **Report a vulnerability**. Please do not open a public issue or pull request for a vulnerability.

Include the input or code that shows the problem, the package and version, the .NET version, and what you expected. A reproduction that is a few lines of YAML or C# is the most useful thing you can send.

A stack overflow, an unhandled exception other than the documented ones, a hang, or memory use out of proportion to the input is a bug in this library and a valid report, whether or not you can show it is exploitable.

## What the library promises

- **Bounds by default.** Nesting depth, alias expansion and the digits of an integer are bounded unless you pass `YamlLimits.None`. Nesting is checked before the binder or a converter runs, so deep input does not overflow the stack. [Limits](docs/limits.md) lists each bound.
- **More bounds with `YamlLimits.Untrusted`.** Input size, scalar size and documents per stream are bounded only with `Untrusted`, which also tightens the default bounds. Use it for any input you do not control.
- **Problems in the input are a `YamlException`.** The reading, editing and patch APIs report bad or oversized input with a `YamlException` and its `Category`; the exceptions for misuse of the API (such as `KeyNotFoundException` from an indexer) are documented on each member. That is a design goal tested by fuzzing, not a proof: an input that causes any other exception is a bug to report as above.
- **Nothing is executed.** A tag never names a .NET type to create or calls code; a tag such as `!!python/object/apply:os.system` is read as plain data. Polymorphism creates only the derived types listed on the base type.
- **No `unsafe` code** in the libraries, no native code, and no dependency outside the .NET libraries.
- **Fuzzed.** The reader, the document, typed binding and patches have fuzz targets (`tests/OneYaml.Fuzz`) that run under the default and the untrusted limits.

## What it does not promise

These are known limits, not bugs. Read [Limits](docs/limits.md) before you handle input from outside.

- Code you write in a `YamlConverter<T>` runs outside the library's limits.
- The compatibility adapters (`OneYaml.Compat.YamlDotNet`, `OneYaml.Compat.SharpYaml`) always run with the default limits and have no `Untrusted` mode. For outside input, read with the `OneYaml` API.
- Without `Untrusted`, there is no cap on input size, scalar size or documents per stream, and memory grows with the input. A `YamlDocument` keeps the text and an index of it.
- `YamlLimits.None` removes every bound, alias expansion included. Never use it on input you do not trust.
- Reflection-based binding creates the types you ask for and the types their members declare. Bind untrusted input to types meant for it.
- `YamlSerializer.Serialize` writes a value that is shared in several places once for each place, so its output can be far larger than the object graph in memory.
