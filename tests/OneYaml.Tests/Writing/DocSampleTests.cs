using Xunit;

namespace OneYaml.Tests.Writing;

/// <summary>The code samples of the write path's documentation, compiled and run. Each mirrors its XML-doc example.</summary>
public class DocSampleTests
{
    [Fact]
    public void Serialize_example()
    {
        var config = new Config(3, "make build\nmake test\n");
        string yaml = YamlSerializer.Serialize(config);

        Assert.Equal(
            """
            replicas: 3
            script: |
              make build
              make test

            """.ReplaceLineEndings("\n"),
            yaml);
        Assert.Equal(config, YamlSerializer.Deserialize<Config>(yaml));
    }
}
