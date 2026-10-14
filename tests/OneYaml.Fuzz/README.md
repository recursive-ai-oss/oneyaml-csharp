# Fuzzing

`tests/OneYaml.Fuzz` mutates seed inputs and checks that the reader ends in events or a
`YamlException` and never in another exception, a hang or a broken event stream:

```
dotnet run -c Release -f net10.0 --project tests/OneYaml.Fuzz -- --seconds 60 --seed 1 --out "$DIR/fuzz"
```

Seeds come from `ONEYAML_YTS_DIR`, `ONEYAML_KIT` and `ONEYAML_WORKLOADS`. A failing input is written
to the output directory as a `.yaml` file next to a `.txt` with the details.
