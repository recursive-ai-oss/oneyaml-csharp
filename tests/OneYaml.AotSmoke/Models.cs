using OneYaml;

namespace OneYaml.AotSmoke;

public enum Mode
{
    Fast,
    Careful,
}

public sealed class BuildJob
{
    public required string Name { get; init; }

    public int Replicas { get; set; }

    public Mode Mode { get; set; }

    public Dictionary<string, string> Labels { get; set; } = [];

    public List<BuildStep> Steps { get; set; } = [];

    public Resources Limits { get; set; } = new(0, "");

    public Point Origin { get; set; }

    public int[] Ports { get; set; } = [];

    public Owner Owner { get; set; } = new() { Name = "" };
}

public sealed class BuildStep
{
    public string Run { get; set; } = "";

    public IDictionary<string, string>? Env { get; set; }
}

public sealed record Resources(int Cpu, string Memory);

public struct Point
{
    public int X { get; set; }

    public int Y { get; init; }
}

public sealed class Owner
{
    public required string Name { get; init; }

    public int Level { get; init; }
}

[YamlSerializable(typeof(BuildJob))]
[YamlSerializable(typeof(object))]
[YamlSerializable(typeof(Dictionary<string, object?>))]
internal sealed partial class SmokeContext : YamlSerializerContext
{
}

