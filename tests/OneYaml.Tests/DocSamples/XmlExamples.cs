using OneYaml;
using Xunit;

namespace DocSamples.Xml;

/// <summary>The examples in the XML documentation of the main entry points.</summary>
[Collection(nameof(DocSample))]
public class XmlExamples
{
    [Fact]
    public void XmlSerialize() => DocSample.Run(nameof(XmlSerialize), () =>
    {
        #region XmlSerialize
        var config = new Config(3, "make build\nmake test\n");
        Console.Write(YamlSerializer.Serialize(config));
        // replicas: 3
        // script: |
        //   make build
        //   make test
        #endregion
    });

    [Fact]
    public void XmlDeserialize() => DocSample.Run(nameof(XmlDeserialize), () =>
    {
        #region XmlDeserialize
        var server = YamlSerializer.Deserialize<Server>("host: example.com\nport: 8080\n")!;
        Console.WriteLine(server); // Server { Host = example.com, Port = 8080 }
        #endregion
    });

    [Fact]
    public void XmlParse() => DocSample.Run(nameof(XmlParse), () =>
    {
        #region XmlParse
        using var doc = YamlDocument.Parse("name: web   # the public site\nreplicas: 2\n");
        doc.Root["replicas"] = 3;
        Console.Write(doc);
        // name: web   # the public site
        // replicas: 3
        #endregion
    });

    [Fact]
    public void XmlKeyIndexer() => DocSample.Run(nameof(XmlKeyIndexer), () =>
    {
        #region XmlKeyIndexer
        using var doc = YamlDocument.Parse("spec:\n  replicas: 2  # scaled by CI\n");
        YamlNode spec = doc.Root["spec"];
        Console.WriteLine(spec["replicas"].GetValue<int>()); // 2
        spec["replicas"] = 3;
        spec["paused"] = false;
        Console.Write(doc);
        // spec:
        //   replicas: 3  # scaled by CI
        //   paused: false
        #endregion
    });

    [Fact]
    public void XmlItemIndexer() => DocSample.Run(nameof(XmlItemIndexer), () =>
    {
        #region XmlItemIndexer
        using var doc = YamlDocument.Parse("ports: [80, 443]\n");
        YamlNode ports = doc.Root["ports"];
        ports[0] = 8080;
        ports[ports.Count] = 8443; // the count appends
        Console.Write(doc);
        // ports: [8080, 443, 8443]
        #endregion
    });

    [Fact]
    public void XmlSet() => DocSample.Run(nameof(XmlSet), () =>
    {
        #region XmlSet
        using var doc = YamlDocument.Parse("spec:\n  image: web:1.4   # pinned\n");
        doc.Set("/spec/image", "web:1.5");
        doc.Set("/spec/ports", new[] { 80, 443 });
        Console.Write(doc);
        // spec:
        //   image: web:1.5   # pinned
        //   ports:
        //     - 80
        //     - 443
        #endregion
    });

    [Fact]
    public void XmlGet() => DocSample.Run(nameof(XmlGet), () =>
    {
        #region XmlGet
        using var doc = YamlDocument.Parse("spec:\n  replicas: 2\n  ports: [80, 443]\n");
        int replicas = doc.Get<int>("/spec/replicas");
        int https = doc.Get<int>("/spec/ports/1");
        Console.WriteLine($"{replicas} {https}"); // 2 443
        #endregion
    });

    [Fact]
    public void XmlApplyPatch() => DocSample.Run(nameof(XmlApplyPatch), () =>
    {
        #region XmlApplyPatch
        using var doc = YamlDocument.Parse("name: web  # site\nreplicas: 2\n");
        doc.ApplyPatch("""[{ "op": "replace", "path": "/replicas", "value": 3 }]""");
        Console.Write(doc);
        // name: web  # site
        // replicas: 3
        #endregion
    });

    [Fact]
    public void XmlApplyMergePatch() => DocSample.Run(nameof(XmlApplyMergePatch), () =>
    {
        #region XmlApplyMergePatch
        using var doc = YamlDocument.Parse("name: web\nlabels:\n  tier: frontend\n  legacy: true\n");
        doc.ApplyMergePatch("""{ "labels": { "legacy": null }, "replicas": 3 }""");
        Console.Write(doc);
        // name: web
        // labels:
        //   tier: frontend
        // replicas: 3
        #endregion
    });

    [Fact]
    public void XmlUntrusted() => DocSample.Run(nameof(XmlUntrusted), () =>
    {
        #region XmlUntrusted
        var options = new YamlSerializerOptions { Limits = YamlLimits.Untrusted };
        try
        {
            YamlSerializer.Deserialize<object>(new string('[', 200) + new string(']', 200), options);
        }
        catch (YamlException e) when (e.Category == YamlErrorCategory.Limit)
        {
            Console.WriteLine("refused: too deep"); // refused: too deep
        }
        #endregion
    });
}

#region XmlSerialize
record Config(int Replicas, string Script);
#endregion

#region XmlDeserialize
record Server(string Host, int Port);
#endregion
