using System.Text;
using Xunit;

namespace OneYaml.Tests.Writing;

// The typed models of the benchmark workloads, with the same shapes.
internal sealed class WorkRecord
{
    public long Id { get; set; }

    public string Name { get; set; } = "";

    public string Email { get; set; } = "";

    public bool Active { get; set; }

    public double Score { get; set; }

    public List<string> Tags { get; set; } = [];

    public WorkAddress Address { get; set; } = new();

    public string? Note { get; set; }
}

internal sealed class WorkAddress
{
    public string City { get; set; } = "";

    public string Zip { get; set; } = "";

    public WorkGeo? Geo { get; set; }
}

internal sealed class WorkGeo
{
    public double Lat { get; set; }

    public double Lng { get; set; }
}

internal sealed class WorkWorkflow
{
    public string Name { get; set; } = "";

    public WorkTriggers On { get; set; } = new();

    public Dictionary<string, WorkJob> Jobs { get; set; } = [];
}

internal sealed class WorkTriggers
{
    [YamlPropertyName("pull_request")]
    public string? PullRequest { get; set; }

    public WorkPush Push { get; set; } = new();
}

internal sealed class WorkPush
{
    public List<string> Branches { get; set; } = [];
}

internal sealed class WorkJob
{
    [YamlPropertyName("runs-on")]
    public string RunsOn { get; set; } = "";

    public WorkStrategy? Strategy { get; set; }

    public object? Container { get; set; }

    public Dictionary<string, WorkService>? Services { get; set; }

    public Dictionary<string, string>? Env { get; set; }

    public List<WorkStep> Steps { get; set; } = [];
}

internal sealed class WorkStrategy
{
    public WorkMatrix Matrix { get; set; } = new();
}

internal sealed class WorkMatrix
{
    [YamlPropertyName("runs-on")]
    public List<string> RunsOn { get; set; } = [];
}

internal sealed class WorkService
{
    public string Image { get; set; } = "";

    public List<string> Ports { get; set; } = [];
}

internal sealed class WorkStep
{
    public string? Id { get; set; }

    public string? Name { get; set; }

    [YamlPropertyName("if")]
    public string? Condition { get; set; }

    public string? Uses { get; set; }

    public string? Run { get; set; }

    public string? Shell { get; set; }

    public Dictionary<string, object?>? With { get; set; }
}

[YamlSerializable(typeof(WorkWorkflow))]
[YamlSerializable(typeof(List<WorkRecord>))]
[YamlSerializable(typeof(object))]
internal sealed partial class WorkloadContext : YamlSerializerContext
{
}

/// <summary>
/// The benchmark workloads, loaded and written back: the whole value must survive, two other parsers must read the
/// output as the same events, and reflection and generated metadata must write the same bytes.
/// </summary>
public class WorkloadWriteTests
{
    public static IEnumerable<object[]> Files()
    {
        var seen = new SortedSet<string>(StringComparer.Ordinal);
        if (Corpus.KitDirectory is { } kit)
        {
            string real = Path.Combine(kit, "bench", "workloads", "real");
            if (Directory.Exists(real))
            {
                foreach (string dir in Directory.EnumerateDirectories(real))
                {
                    string file = Path.Combine(dir, "input.yaml");
                    if (File.Exists(file))
                    {
                        seen.Add(file);
                    }
                }
            }
        }

        // The larger generated workloads sit outside the repository; they are used when the directory is named.
        if (Environment.GetEnvironmentVariable("ONEYAML_WORKLOADS") is { Length: > 0 } extra && Directory.Exists(extra))
        {
            foreach (string file in Directory.EnumerateFiles(extra, "*.yaml"))
            {
                seen.Add(file);
            }
        }

        foreach (string file in seen)
        {
            yield return [file];
        }
    }

    [Fact]
    public void The_workload_list_is_not_empty_when_the_kit_is_present()
    {
        if (Corpus.KitDirectory is null)
        {
            Assert.Skip($"{Corpus.KitVariable} is not set");
        }

        Assert.True(Files().Count() >= 7);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void A_workload_loaded_as_objects_is_written_back_whole(string file)
    {
        string text = File.ReadAllText(file);
        List<object?> documents = YamlSerializer.DeserializeAll<object>(text)!;

        foreach (object? document in documents)
        {
            string written = YamlSerializer.Serialize<object?>(document);
            ReadBack.Check(written);
            Assert.True(Canon.Of(document) == Canon.Of(YamlSerializer.Deserialize<object>(written)), Path.GetFileName(file) + " changed");
            Assert.Equal(written, YamlSerializer.Serialize<object?>(document, WorkloadContext.Default.Object));
        }
    }

    [Fact]
    public void The_typed_workflow_and_record_models_are_written_back_whole()
    {
        int typed = 0;
        foreach (object[] row in Files())
        {
            string file = (string)row[0];
            string name = Path.GetFileNameWithoutExtension(file);
            string text = File.ReadAllText(file);
            if (name == "gha-workflow" || file.Contains("gha-workflow", StringComparison.Ordinal))
            {
                WorkWorkflow model = YamlSerializer.Deserialize<WorkWorkflow>(text)!;
                string written = YamlSerializer.Serialize(model);
                Assert.Equal(written, YamlSerializer.Serialize(model, WorkloadContext.Default.WorkWorkflow));
                ReadBack.Check(written);
                Assert.Equal(Canon.Of(model), Canon.Of(YamlSerializer.Deserialize<WorkWorkflow>(written)));
                typed++;
            }
            else if (name.StartsWith("records", StringComparison.Ordinal))
            {
                List<WorkRecord> model = YamlSerializer.Deserialize<List<WorkRecord>>(text)!;
                string written = YamlSerializer.Serialize(model);
                Assert.Equal(written, YamlSerializer.Serialize(model, WorkloadContext.Default.ListWorkRecord));
                ReadBack.Check(written);
                Assert.Equal(Canon.Of(model), Canon.Of(YamlSerializer.Deserialize<List<WorkRecord>>(written)));
                typed++;
            }
        }

        if (typed == 0)
        {
            Assert.Skip("no typed workload files are available");
        }
    }

    [Fact]
    public void Output_is_valid_utf8_without_a_byte_order_mark_and_ends_with_a_line_break()
    {
        object?[] values = [new Dictionary<string, object?> { ["é日本"] = "😀" }, "x", 1L, new List<object?>()];
        foreach (object? value in values)
        {
            byte[] bytes = YamlSerializer.SerializeToUtf8Bytes(value);

            Assert.NotEqual(0xEF, bytes[0]);
            Assert.Equal((byte)'\n', bytes[^1]);
            Assert.Equal(bytes, Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes)));
        }
    }
}
