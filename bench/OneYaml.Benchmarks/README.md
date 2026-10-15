# Benchmarks

Run from this directory, with the report folder outside the repository:

```
dotnet run -c Release -f net10.0 -- --filter '*' --artifacts "$DIR"
```

`--job short` gives a quick smoke run. Reports and binaries stay out of the repository.

## Parse benchmarks

`ParseBenchmarks` reads every event of a document with the OneYaml reader and with the event
parsers of VYaml 1.4.0, SharpYaml 3.14.0 and YamlDotNet 18.1.0, and reports time and allocated
bytes per file. Three variants show what the work costs: events only, events with the position of
every comment recorded, and events with every scalar turned into a string.

The input files are not in the repository. Put them in a directory and point
`ONEYAML_WORKLOADS` at it; the files are named after the `Workload` parameter plus `.yaml`:

```
gha-workflow  k8s-multi-16k  openapi-100k  records-64k
long-scalars-256k  scalar-styles-32k  unicode-32k  flow-heavy-64k
```

The generated shapes (`records`, `k8s-multi`, `openapi`, `long-scalars`, `scalar-styles`,
`unicode`, `flow-heavy`) come from the generator in the shared case kit, at the sizes in the
names; `gha-workflow` is one of the kit's real-world files.

```
ONEYAML_WORKLOADS="$DIR/workloads" dotnet run -c Release -f net10.0 -- --filter '*ParseBenchmarks*' --artifacts "$DIR/bdn"
```

The benchmarks run in process, so they work wherever the build output is redirected to.

## Load benchmarks

`TypedLoadBenchmarks` binds the gha-workflow, records-4k and records-64k files to the kit's models
(`Models.cs`, the same shapes the kit's harness uses) with the OneYaml reflection binder, VYaml and
SharpYaml through generated code, and YamlDotNet through reflection. `GenericLoadBenchmarks` loads
eleven files as untyped values (`object`) with OneYaml (reflection and generated), VYaml, SharpYaml and YamlDotNet; a file with several documents is read as a list of them. Both report time and allocated bytes.
They read the same `ONEYAML_WORKLOADS` directory; `records-4k` is cut from the first records of
`records-64k` when no file of that name exists.

```
ONEYAML_WORKLOADS="$DIR/workloads" dotnet run -c Release -f net10.0 -- --filter '*LoadBenchmarks*' --artifacts "$DIR/bdn"
```

## Dump benchmarks

`TypedDumpBenchmarks` writes the gha-workflow, records-4k and records-64k models with the OneYaml
reflection and generated paths (to a string and to UTF-8 bytes), VYaml and SharpYaml through generated code,
and YamlDotNet through reflection. `GenericDumpBenchmarks` writes ten untyped trees, the task of the kit's
`dump` rows: OneYaml writes its generic tree through the reflection options and through generated metadata
for `object`, to a string and to UTF-8 bytes; VYaml and SharpYaml write the tree their own reader produced
(SharpYaml reads with references preserved and writes with default options, then encodes to UTF-8, as the
kit does). The model or tree is loaded in setup, so only the writing is timed. Both read the
`ONEYAML_WORKLOADS` directory; the Helm file is `helm-values-64k`, made by the kit's generator.
`ONEYAML_DUMP_WORKLOADS` (comma separated names) narrows the untyped run to some of the files.

```
ONEYAML_WORKLOADS="$DIR/workloads" dotnet run -c Release -f net10.0 -- --filter '*DumpBenchmarks*' --artifacts "$DIR/bdn"
```

Two helpers compare builds without BenchmarkDotNet. `--dump-hash [name...]` prints a hash of what the
untyped dump writes for each workload, so two builds can be checked for identical output. `--dump-loop name
seconds` dumps one tree repeatedly and prints the best time of a batch, for a quick before/after on a
busy machine. The load counterparts are `--load-hash [name...]`, a hash of the value graph a generic load
builds (reflection, generated, and the string overload), and `--load-loop name seconds`, which loads one
workload repeatedly with OneYaml (both paths), VYaml and SharpYaml as the kit does and prints each best batch
time in microseconds and the bytes one load allocates.

## Scalar benchmarks

The scalar benchmarks read workload files from a checkout of the conformance kit: set
`ONEYAML_KIT` to its path first.
