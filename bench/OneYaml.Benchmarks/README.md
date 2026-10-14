# Benchmarks

Run from this directory, with the report folder outside the repository:

```
dotnet run -c Release -f net10.0 -- --filter '*' --artifacts "$DIR"
```

`--job short` gives a quick smoke run. Reports and binaries stay out of the repository.
