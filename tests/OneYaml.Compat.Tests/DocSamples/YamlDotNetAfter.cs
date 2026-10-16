#region YamlDotNetAfter
using OneYaml.Compat.YamlDotNet.Serialization;
using OneYaml.Compat.YamlDotNet.Serialization.NamingConventions;
#endregion
using Xunit;

namespace DocSamples.YamlDotNetAfter;

[Collection(nameof(DocSample))]
public class YamlDotNetAfterSamples
{
    [Fact]
    public void YamlDotNetAfter() => DocSample.Run(nameof(YamlDotNetAfter), () =>
    {
        #region YamlDotNetAfter
        IDeserializer deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        var service = deserializer.Deserialize<Service>("name: web\nreplicas: 2\nlegacy: true\n");

        ISerializer serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();
        Console.Write(serializer.Serialize(service));
        // name: web
        // replicas: 2
        #endregion
    });
}

#region YamlDotNetAfter
class Service
{
    public string Name { get; set; } = "";
    public int Replicas { get; set; }
}
#endregion
