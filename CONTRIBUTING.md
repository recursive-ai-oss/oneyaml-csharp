# Contributing

Thanks for helping. Bug reports, reproductions, documentation fixes and pull requests are all welcome.

## Before you start

- **Found a vulnerability?** Do not open an issue. Follow [SECURITY.md](SECURITY.md).
- **Found a bug?** Open an issue with the smallest YAML and C# that show it, the package version, the .NET version, and what you expected. A failing test is better still.
- **Want a feature?** Open an issue first and describe the problem you are solving. The library keeps a small API on purpose, so a feature that is not discussed may not be accepted.

By taking part you agree to follow the [Code of Conduct](CODE_OF_CONDUCT.md).

## Setup

You need the .NET SDK named in `global.json` and the .NET 8 runtime, because the tests run on .NET 8 and .NET 10. Clone the repository and run:

```sh
dotnet build OneYaml.slnx
dotnet test --solution OneYaml.slnx
```

Build output goes outside the clone: to the directory in `ONEYAML_ARTIFACTS` if you set it, otherwise to a per-user cache directory (see `Directory.Build.props`).

To try your build in another project, pack it with a version of its own, so that NuGet does not mix it up with the published package of the same number, and add it from the folder it was written to:

```sh
dotnet pack src/OneYaml/OneYaml.csproj -c Release -p:VersionSuffix=local.1 -o $HOME/oneyaml-packages   # in the clone
dotnet add package OneYaml --version 1.0.0-local.1 --source $HOME/oneyaml-packages                      # in your project
```

The version is the `VersionPrefix` in `Directory.Build.props` followed by the suffix you pass.

The repository holds:

| Path | What it is |
|---|---|
| `src/OneYaml` | the library, and its source generator in `src/OneYaml.SourceGeneration` |
| `src/OneYaml.Compat.YamlDotNet`, `src/OneYaml.Compat.SharpYaml` | the compatibility adapters |
| `tests/OneYaml.Tests` | unit tests, the conformance tests and the documentation samples |
| `tests/OneYaml.Compat.Tests` | differential tests of the adapters against the real YamlDotNet and SharpYaml |
| `tests/OneYaml.SourceGeneration.Tests`, `tests/OneYaml.AotSmoke` | generator snapshots, and a native AOT build that must publish without warnings |
| `tests/OneYaml.Fuzz` | fuzz targets; its README says how to run them |
| `bench/OneYaml.Benchmarks` | BenchmarkDotNet benchmarks; its README says how to run them |
| `tools/` | the repository checks |

The conformance tests read the shared case kit, [oneyaml-kit](https://github.com/recursive-ai-oss/oneyaml-kit). They are skipped unless `ONEYAML_KIT` names a checkout of it (and `ONEYAML_YTS_DIR` the yaml-test-suite data, which defaults to its place inside the kit). CI runs them against a pinned kit revision.

## Checks

Before you send a pull request, run the local checks:

```sh
tools/gate
```

`tools/gate` runs the same checks as CI: build, tests, format, public text, secret scan. In detail, it restores and builds the solution in Release for both target frameworks (warnings are errors, and the trimming and AOT analyzers are on), runs the tests except those in the `Slow` category, checks formatting with `dotnet format`, checks the text of the files you changed against `main` (set `GATE_BASE` to compare with another branch), scans for secrets, and checks that `src/` has no unsafe code. CI runs the same checks, and all the tests, on Linux, macOS and Windows.

## What a good pull request has

- **One change.** Keep a pull request to one fix or one feature.
- **A test that fails without it.** Behaviour that could plausibly break gets a test. Tests never assert on timing; a guard against slow behaviour asserts on deterministic work, such as allocated bytes, and speed is left to the benchmarks.
- **Documentation.** Every public member has an XML doc comment; the build fails without one. A C# block in `README.md`, `docs/` or a package readme is a sample in `tests/*/DocSamples`: the line before the block names it, as in `<!-- sample: EditPointers -->`, and the block is the text of the `#region EditPointers` in a sample file. The tests fail if the two differ, and they run each sample and compare what it prints with the output in its comments. An `<example>` in the XML documentation must also be the text of a sample region. Edit the sample, then copy its text.
- **No hand-typed numbers.** Benchmark and conformance figures in the documentation are generated, between `<!-- generated:... -->` and `<!-- end generated -->` markers. Do not edit inside them. They are written by the oneyaml-kit `bin/bench readme` command from a benchmark result and a conformance result for `oneyaml-csharp`, which fills the blocks in `README.md`, `docs/performance.md` and `docs/conformance.md` and the charts in `docs/img`: `bin/bench readme --results <results directory> --library oneyaml-csharp --readme README.md --docs docs --charts docs/img`. The tests check that the markers are present and paired, not what is between them.
- **A changelog line** under `[Unreleased]` in [CHANGELOG.md](CHANGELOG.md) for anything a user can see. Mark breaking changes.
- **No new dependency** for the `OneYaml` package: it depends on the .NET libraries only, and a pull request that adds a package to it will be declined unless there is a strong reason.
- **No `unsafe` code** in `src/`.

## Performance changes

Show before and after with the BenchmarkDotNet benchmarks you touched in `bench/OneYaml.Benchmarks`, with `[MemoryDiagnoser]` allocations. A change that slows a benchmark or adds allocations needs a reason.

## Licence

The project is dual licensed under [MIT](LICENSE-MIT) or [Apache-2.0](LICENSE-APACHE). Unless you state otherwise, your contribution is licensed the same way, without additional terms.
