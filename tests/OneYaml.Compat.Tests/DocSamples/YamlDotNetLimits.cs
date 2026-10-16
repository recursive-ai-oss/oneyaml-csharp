#region YamlDotNetLimits
using OneYaml.Compat.YamlDotNet.Core;
using OneYaml.Compat.YamlDotNet.Serialization;
#endregion
using Xunit;

namespace DocSamples.YamlDotNetLimits;

[Collection(nameof(DocSample))]
public class YamlDotNetLimitsSamples
{
    [Fact]
    public void YamlDotNetLimits() => DocSample.Run(nameof(YamlDotNetLimits), () =>
    {
        #region YamlDotNetLimits
        string upload = new string('[', 129) + new string(']', 129); // nested 129 deep

        // WithLimits is not in YamlDotNet. It takes the limits of the OneYaml library; Untrusted is its preset for outside input.
        IDeserializer deserializer = new DeserializerBuilder()
            .WithLimits(OneYaml.YamlLimits.Untrusted)
            .Build();
        try
        {
            deserializer.Deserialize<object>(upload);
        }
        catch (YamlException e)
        {
            Console.WriteLine(e.Message); // The nesting depth is larger than the limit MaxDepth (128). (line 1, column 129)
        }

        // The default limits allow the same text.
        Console.WriteLine(new DeserializerBuilder().Build().Deserialize<object>(upload) is not null); // True
        #endregion
    });
}
