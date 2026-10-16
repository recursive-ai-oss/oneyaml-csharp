#region SharpYamlLimits
using OneYaml.Compat.SharpYaml;
#endregion
using Xunit;

namespace DocSamples.SharpYamlLimits;

[Collection(nameof(DocSample))]
public class SharpYamlLimitsSamples
{
    [Fact]
    public void SharpYamlLimits() => DocSample.Run(nameof(SharpYamlLimits), () =>
    {
        #region SharpYamlLimits
        // 17 short lines whose aliases double at each level: a small text that stands for a very large value.
        string upload = "a0: &a0 [x, x]\n" + string.Concat(Enumerable.Range(1, 16).Select(i => $"a{i}: &a{i} [*a{i - 1}, *a{i - 1}]\n"));

        // Limits is not in SharpYaml. It takes the limits of the OneYaml library; Untrusted is its preset for outside input.
        var options = new YamlSerializerOptions { Limits = OneYaml.YamlLimits.Untrusted };
        try
        {
            YamlSerializer.Deserialize<object>(upload, options);
        }
        catch (YamlException e)
        {
            Console.WriteLine(e.Message);
            // (Lin: 14, Col: 17, Chr: 293) - (Lin: 14, Col: 17, Chr: 293): Expanding the aliases exceeds the budget set by YamlLimits (AliasNodeFactor 2, AliasNodeFloor 100000, AliasByteFactor 2, AliasByteFloor 8388608). (line 15, column 18)
        }

        // The default limits allow the same text.
        Console.WriteLine(YamlSerializer.Deserialize<object>(upload) is not null); // True
        #endregion
    });
}
