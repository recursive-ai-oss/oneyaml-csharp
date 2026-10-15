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

// Writing through generated metadata, typed and generic, and reading the text back.
string written = YamlSerializer.Serialize(job!, SmokeContext.Default.BuildJob);
BuildJob? again = YamlSerializer.Deserialize(written, SmokeContext.Default.BuildJob);
bool writeOk = again is not null
    && again.Replicas == 3
    && again.Mode == Mode.Fast
    && again.Steps.Count == 2 && again.Steps[1].Env!.Count == 1
    && again.Limits.Memory == "4Gi"
    && again.Origin.Y == 2
    && again.Ports.SequenceEqual([80, 443])
    && again.Owner.Level == 7
    && YamlSerializer.Serialize(again, SmokeContext.Default.BuildJob) == written;
string genericWritten = YamlSerializer.Serialize(generic, SmokeContext.Default.Object);
object? genericAgain = YamlSerializer.Deserialize(genericWritten, SmokeContext.Default.Object);
bool genericWriteOk = genericAgain is Dictionary<string, object?> again2 && again2["replicas"] is long and 3
    && YamlSerializer.Serialize(genericAgain, SmokeContext.Default.Object) == genericWritten;

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

// The reflection overloads carry the trim and AOT annotations. Calling one here is suppressed on purpose,
// to show what a program that ignores the warning gets: a clear error, not reflection. Without the
// suppression the build fails with IL2026 and IL3050, which is the warning applications see.
bool switchOff = !YamlSerializer.IsReflectionEnabledByDefault && CallsReflectionOverload();

[System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The smoke test calls it to see it refuse.")]
[System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050", Justification = "The smoke test calls it to see it refuse.")]
static bool CallsReflectionOverload()
{
    try
    {
        YamlSerializer.Deserialize<BuildJob>("name: x");
        return false;
    }
    catch (InvalidOperationException)
    {
        return true;
    }
}

// Options with no resolver never reach reflection through GetTypeInfo.
bool plainOptionsRefuse = false;
try
{
    new YamlSerializerOptions().GetTypeInfo<BuildJob>();
}
catch (InvalidOperationException)
{
    plainOptionsRefuse = true;
}

Console.WriteLine($"switchOff={switchOff} plainOptionsRefuse={plainOptionsRefuse}");
Console.WriteLine($"typed={ok} generic={genericOk} write={writeOk} genericWrite={genericWriteOk} error={error} reflectionOff={reflectionOff}");
return ok && genericOk && writeOk && genericWriteOk && error == "Binding at 1:11" && reflectionOff && switchOff && plainOptionsRefuse ? 0 : 1;
