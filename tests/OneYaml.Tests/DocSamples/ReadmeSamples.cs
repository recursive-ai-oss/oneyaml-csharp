#region QuickStart
using OneYaml;
#endregion
using Xunit;

namespace DocSamples.Readme;

[Collection(nameof(DocSample))]
public class ReadmeSamples
{
    [Fact]
    public void QuickStart() => DocSample.Run(nameof(QuickStart), () =>
    {
        #region QuickStart
        string src = """
            # Build settings
            script: |
              make build
            replicas: 2      # the CI job scales this
            """;

        using var doc = YamlDocument.Parse(src);
        doc.Set("/script", "make build\nmake test\n"); // still a block literal
        doc.Root["replicas"] = 3;                      // only the 2 changes
        Console.WriteLine(doc);
        // # Build settings
        // script: |
        //   make build
        //   make test
        // replicas: 3      # the CI job scales this

        var cfg = YamlSerializer.Deserialize<Config>(src)!;
        Console.WriteLine($"{cfg.Replicas} replicas"); // 2 replicas
        #endregion
    });
}

#region QuickStart
record Config(int Replicas, string Script);
#endregion
