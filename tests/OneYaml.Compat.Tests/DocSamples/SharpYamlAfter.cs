#region SharpYamlAfter
using System.Text.Json;
using OneYaml.Compat.SharpYaml;
#endregion
using Xunit;

namespace DocSamples.SharpYamlAfter;

[Collection(nameof(DocSample))]
public class SharpYamlAfterSamples
{
    [Fact]
    public void SharpYamlAfter() => DocSample.Run(nameof(SharpYamlAfter), () =>
    {
        #region SharpYamlAfter
        var options = new YamlSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var service = YamlSerializer.Deserialize<Service>("name: web\nreplicas: 2\n", options)!;
        Console.Write(YamlSerializer.Serialize(service with { Replicas = 3 }, options));
        // name: web
        // replicas: 3
        #endregion
    });
}

#region SharpYamlAfter
record Service(string Name, int Replicas);
#endregion
