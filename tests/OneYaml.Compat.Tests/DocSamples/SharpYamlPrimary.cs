#region SharpYamlPrimary
using OneYaml;
#endregion
using Xunit;

namespace DocSamples.SharpYamlPrimary;

[Collection(nameof(DocSample))]
public class SharpYamlPrimarySamples
{
    [Fact]
    public void SharpYamlPrimary() => DocSample.Run(nameof(SharpYamlPrimary), () =>
    {
        #region SharpYamlPrimary
        // camelCase is the default naming policy, so the options can go.
        var service = YamlSerializer.Deserialize<Service>("name: web\nreplicas: 2\n")!;
        Console.Write(YamlSerializer.Serialize(service with { Replicas = 3 }));
        // name: web
        // replicas: 3
        #endregion
    });
}

#region SharpYamlPrimary
record Service(string Name, int Replicas);
#endregion
