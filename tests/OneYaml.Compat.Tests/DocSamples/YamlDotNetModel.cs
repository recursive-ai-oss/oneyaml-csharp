#region YamlDotNetModel
using OneYaml.Compat.YamlDotNet.RepresentationModel;
#endregion
using Xunit;

namespace DocSamples.YamlDotNetModel;

[Collection(nameof(DocSample))]
public class YamlDotNetModelSamples
{
    [Fact]
    public void YamlDotNetModel() => DocSample.Run(nameof(YamlDotNetModel), () =>
    {
        #region YamlDotNetModel
        var stream = new YamlStream();
        stream.Load(new StringReader("# service settings\nname: web\nreplicas: 2   # set by CI\n"));

        var root = (YamlMappingNode)stream.Documents[0].RootNode;
        Console.WriteLine(root["replicas"]); // 2

        var text = new StringWriter();
        stream.Save(text);               // nothing was changed, so the text comes back as it was read
        Console.Write(text);
        // # service settings
        // name: web
        // replicas: 2   # set by CI
        #endregion
    });
}
