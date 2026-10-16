#region SharpYamlBefore
using System.Text.Json;
using SharpYaml;
#endregion
using Xunit;

namespace DocSamples.SharpYamlBefore;

[Collection(nameof(DocSample))]
public class SharpYamlBeforeSamples
{
    [Fact]
    public void SharpYamlBefore() => DocSample.Run(nameof(SharpYamlBefore), () =>
    {
        #region SharpYamlBefore
        var options = new YamlSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var service = YamlSerializer.Deserialize<Service>("name: web\nreplicas: 2\n", options)!;
        Console.Write(YamlSerializer.Serialize(service with { Replicas = 3 }, options));
        // name: web
        // replicas: 3
        #endregion
    });
}

#region SharpYamlBefore
record Service(string Name, int Replicas);
#endregion
