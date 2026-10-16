# Fuzzing

`tests/OneYaml.Fuzz` mutates seed inputs and runs one target per process:

| Target | What must hold |
|---|---|
| `reader` | The pull reader ends in well-formed events or a `YamlException`; offsets stay in the input; the same text in another encoding reads the same. |
| `doc` | An unedited `YamlDocument` writes back the input byte for byte; each document read through `YamlNode.Deserialize<object>` equals the generic load. |
| `bind` | Typed binding through reflection and through the generated context (records, immutable collections, enums, extension data, polymorphism by key and by tag, converters) gives the same value, the same written text, or the same error category. |
| `patch` | Random RFC 6902 and RFC 7396 patches: a failed patch leaves every byte as it was, a patch that works re-parses and gives the structure an independent tree model says. |

```
dotnet run -c Release -f net10.0 --project tests/OneYaml.Fuzz -- --target doc --seconds 60 --seed 1 --out "$DIR/fuzz"
```

Options: `--max-runs N`, `--max-classes N` (stop after N distinct failure classes, default 5), `--slow-ms` (default 1000),
`--cap-ms` (hang watchdog, default 5000), `--max-length`, and `--replay FILE` to run one saved input under both limits.
`--plant` makes each target corrupt its own result for some inputs, to check that a run reports them.

Every input runs under the default limits or under `YamlLimits.Untrusted`, on a thread with a 1 MiB stack.
Under the untrusted preset an input of at most 64 KB that takes longer than `--slow-ms`, or allocates far more than a
multiple of its size, is a failure too: that is where superlinear behaviour shows. A target brackets its library calls,
so the model it checks against is not charged to the library.

Seeds come from `ONEYAML_YTS_DIR`, `ONEYAML_KIT` and `ONEYAML_WORKLOADS`. A failing input is shrunk and written to the
output directory as a `.yaml` next to a `.txt` with the details. The hang watchdog saves the input as it was and stops the run.
