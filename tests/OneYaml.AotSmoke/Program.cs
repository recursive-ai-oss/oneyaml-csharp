using OneYaml;
using OneYaml.AotSmoke;

// Reads a document through generated metadata only. The program is published with the native
// toolchain (or trimmed) by run.sh, which fails on any trim or AOT warning, and then runs it.
const string Yaml = """
    name: build
    # comments and anchors are plain YAML
    defaults: &d {cpu: 2, memory: 4Gi}
    replicas: 3
    mode: fast
    labels: {team: core, tier: web}
    steps:
      - run: make
      - run: make test
        env: {CI: "true"}
    limits: *d
    origin: {x: 1, y: 2}
    ports: [80, 443]
    owner: {name: ada, level: 7}
    """;

BuildJob? job = YamlSerializer.Deserialize(Yaml, SmokeContext.Default.BuildJob);

bool ok = job is not null
    && job.Name == "build"
    && job.Replicas == 3
    && job.Mode == Mode.Fast
    && job.Labels is { Count: 2 } && job.Labels["team"] == "core"
    && job.Steps.Count == 2 && job.Steps[1].Env!.Count == 1 && job.Steps[1].Run == "make test"
    && job.Limits.Cpu == 2 && job.Limits.Memory == "4Gi"
    && job.Origin.X == 1 && job.Origin.Y == 2
    && job.Ports.SequenceEqual([80, 443])
    && job.Owner.Name == "ada" && job.Owner.Level == 7;

// The same document as the generic model, and the error contract.
object? generic = YamlSerializer.Deserialize(Yaml, SmokeContext.Default.Object);
bool genericOk = generic is Dictionary<string, object?> map && map["replicas"] is long and 3;

string error;
try
{
    YamlSerializer.Deserialize("replicas: many\nname: x\n", SmokeContext.Default.BuildJob);
    error = "no error";
}
catch (YamlException ex)
{
    error = $"{ex.Category} at {ex.Line}:{ex.Column}";
}

bool reflectionOff = false;
try
{
    // Not in the context, and reflection is switched off: the type must not be found.
    YamlSerializer.Deserialize("x: 1", SmokeContext.Default.Options.GetTypeInfo<Guid[]>());
}
catch (NotSupportedException)
{
    reflectionOff = true;
}

Console.WriteLine($"typed={ok} generic={genericOk} error={error} reflectionOff={reflectionOff}");
return ok && genericOk && error == "Binding at 1:11" && reflectionOff ? 0 : 1;
