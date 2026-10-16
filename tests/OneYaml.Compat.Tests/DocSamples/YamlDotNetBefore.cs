#region YamlDotNetBefore
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
#endregion
using Xunit;

namespace DocSamples.YamlDotNetBefore;

[Collection(nameof(DocSample))]
public class YamlDotNetBeforeSamples
{
    [Fact]
    public void YamlDotNetBefore() => DocSample.Run(nameof(YamlDotNetBefore), () =>
    {
        #region YamlDotNetBefore
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

#region YamlDotNetBefore
class Service
{
    public string Name { get; set; } = "";
    public int Replicas { get; set; }
}
#endregion
