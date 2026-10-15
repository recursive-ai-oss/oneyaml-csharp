using System.Text.Json.Serialization;

namespace OneYaml.Compat.Tests.SharpYamlCompat;

// The typed models of the benchmark kit, as the workloads are read into them. Renamed members use the attribute
// both libraries honour.

public class Record
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public bool Active { get; set; }
    public double Score { get; set; }
    public List<string> Tags { get; set; } = [];
    public Address Address { get; set; } = new();
    public string? Note { get; set; }
}

public class Address
{
    public string City { get; set; } = "";
    public string Zip { get; set; } = "";
    public Geo? Geo { get; set; }
}

public class Geo
{
    public double Lat { get; set; }
    public double Lng { get; set; }
}

public class Workflow
{
    public string Name { get; set; } = "";
    public Triggers On { get; set; } = new();
    public Dictionary<string, Job> Jobs { get; set; } = [];
}

public class Triggers
{
    [JsonPropertyName("pull_request")]
    public string? PullRequest { get; set; }
    public Push Push { get; set; } = new();
}

public class Push
{
    public List<string> Branches { get; set; } = [];
}

public class Job
{
    [JsonPropertyName("runs-on")]
    public string RunsOn { get; set; } = "";
    public Strategy? Strategy { get; set; }
    /// <summary>A string (an image name) or a mapping with `image` and `options`.</summary>
    public object? Container { get; set; }
    public Dictionary<string, Service>? Services { get; set; }
    public Dictionary<string, string>? Env { get; set; }
    public List<Step> Steps { get; set; } = [];
}

public class Strategy
{
    public Matrix Matrix { get; set; } = new();
}

public class Matrix
{
    [JsonPropertyName("runs-on")]
    public List<string> RunsOn { get; set; } = [];
}

public class Service
{
    public string Image { get; set; } = "";
    public List<string> Ports { get; set; } = [];
}

public class Step
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    [JsonPropertyName("if")]
    public string? Condition { get; set; }
    public string? Uses { get; set; }
    public string? Run { get; set; }
    public string? Shell { get; set; }
    /// <summary>Each value is a string or a boolean.</summary>
    public Dictionary<string, object?>? With { get; set; }
}

