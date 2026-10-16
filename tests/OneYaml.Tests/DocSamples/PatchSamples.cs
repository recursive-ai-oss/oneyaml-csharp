#region PatchJson
using OneYaml;
#endregion
using Xunit;

namespace DocSamples.Patches;

[Collection(nameof(DocSample))]
public class PatchSamples
{
    [Fact]
    public void PatchJson() => DocSample.Run(nameof(PatchJson), () =>
    {
        #region PatchJson
        using var doc = YamlDocument.Parse("""
            name: web          # the public site
            replicas: 2
            labels:
              tier: frontend
            """);

        doc.ApplyPatch("""
            [
              { "op": "test",    "path": "/name",        "value": "web" },
              { "op": "replace", "path": "/replicas",    "value": 3 },
              { "op": "add",     "path": "/labels/team", "value": "core" }
            ]
            """);
        Console.WriteLine(doc);
        // name: web          # the public site
        // replicas: 3
        // labels:
        //   tier: frontend
        //   team: core
        #endregion
    });

    [Fact]
    public void PatchTestNumbers() => DocSample.Run(nameof(PatchTestNumbers), () =>
    {
        #region PatchTestNumbers
        using var doc = YamlDocument.Parse("timeout: 1.0\n");
        try
        {
            doc.ApplyPatch("""[{ "op": "test", "path": "/timeout", "value": 1 }]""");
        }
        catch (YamlException e) when (e.Category == YamlErrorCategory.PatchTest)
        {
            Console.WriteLine(e.Message); // The value at '/timeout' is not the value the test expects.
        }

        doc.ApplyPatch("""[{ "op": "test", "path": "/timeout", "value": 1.0 }]"""); // passes
        #endregion
    });

    [Fact]
    public void PatchAtomic() => DocSample.Run(nameof(PatchAtomic), () =>
    {
        #region PatchAtomic
        const string Src = "name: web\nreplicas: 2\n";
        using var doc = YamlDocument.Parse(Src);
        try
        {
            doc.ApplyPatch("""
                [
                  { "op": "replace", "path": "/replicas", "value": 5 },
                  { "op": "test",    "path": "/name",     "value": "api" }
                ]
                """);
        }
        catch (YamlException e)
        {
            Console.WriteLine($"{e.Category} at operation {e.OperationIndex}"); // PatchTest at operation 1
        }

        Console.WriteLine(doc.ToString() == Src); // True
        #endregion
    });

    [Fact]
    public void PatchBuilder() => DocSample.Run(nameof(PatchBuilder), () =>
    {
        #region PatchBuilder
        var patch = new YamlPatch()
            .Test("/name", "web")
            .Replace("/replicas", 3)
            .Add("/labels/tier", "frontend");

        using var doc = YamlDocument.Parse("name: web\nreplicas: 2\nlabels: {}\n");
        doc.ApplyPatch(patch);
        Console.Write(doc);
        // name: web
        // replicas: 3
        // labels: {tier: frontend}
        #endregion
    });

    [Fact]
    public void PatchMerge() => DocSample.Run(nameof(PatchMerge), () =>
    {
        #region PatchMerge
        using var doc = YamlDocument.Parse("""
            name: web
            labels:
              tier: frontend   # used by the load balancer
              legacy: true
            """);

        doc.ApplyMergePatch("""{ "labels": { "legacy": null, "team": "core" }, "replicas": 3 }""");
        Console.WriteLine(doc);
        // name: web
        // labels:
        //   tier: frontend   # used by the load balancer
        //   team: core
        // replicas: 3
        #endregion
    });
}
