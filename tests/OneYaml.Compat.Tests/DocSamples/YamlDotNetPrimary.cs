#region YamlDotNetPrimary
using OneYaml;
#endregion
using Xunit;

namespace DocSamples.YamlDotNetPrimary;

[Collection(nameof(DocSample))]
public class YamlDotNetPrimarySamples
{
    [Fact]
    public void YamlDotNetPrimary() => DocSample.Run(nameof(YamlDotNetPrimary), () =>
    {
        #region YamlDotNetPrimary
        // camelCase keys and skipping unknown keys are the defaults; no builder is needed.
        var service = YamlSerializer.Deserialize<Service>("name: web\nreplicas: 2\nlegacy: true\n")!;
        Console.Write(YamlSerializer.Serialize(service));
        // name: web
        // replicas: 2
        #endregion
    });

    [Fact]
    public void YamlDotNetStreamToDocument() => DocSample.Run(nameof(YamlDotNetStreamToDocument), () =>
    {
        #region YamlDotNetStreamToDocument
        // Instead of YamlStream: load, change one value, save, and keep the comments.
        using var doc = YamlDocument.Parse("# service settings\nname: web\nreplicas: 2   # set by CI\n");
        doc.Root["replicas"] = 3;
        Console.Write(doc);
        // # service settings
        // name: web
        // replicas: 3   # set by CI
        #endregion
    });
}

#region YamlDotNetPrimary
class Service
{
    public string Name { get; set; } = "";
    public int Replicas { get; set; }
}
#endregion
