#region LimitsUntrusted
using OneYaml;
#endregion
using System.Text;
using Xunit;

namespace DocSamples.Limits;

[Collection(nameof(DocSample))]
public class LimitsSamples
{
    [Fact]
    public void LimitsUntrusted() => DocSample.Run(nameof(LimitsUntrusted), () =>
    {
        #region LimitsUntrusted
        // Nine levels of ten aliases each: a few hundred bytes that expand to a billion strings.
        var bomb = new StringBuilder("a: &a [x, x, x, x, x, x, x, x, x, x]\n");
        for (char c = 'b'; c <= 'i'; c++)
        {
            string prev = $"*{(char)(c - 1)}";
            bomb.Append($"{c}: &{c} [{string.Join(", ", Enumerable.Repeat(prev, 10))}]\n");
        }

        var options = new YamlSerializerOptions { Limits = YamlLimits.Untrusted };
        try
        {
            YamlSerializer.Deserialize<object>(bomb.ToString(), options);
        }
        catch (YamlException e) when (e.Category == YamlErrorCategory.Limit)
        {
            Console.WriteLine(e.Message);
            // Expanding the aliases exceeds the budget set by YamlLimits (AliasNodeFactor 2, AliasNodeFloor 100000, AliasByteFactor 2, AliasByteFloor 8388608). (line 5, column 36)
        }
        #endregion
    });

    [Fact]
    public void LimitsCustom() => DocSample.Run(nameof(LimitsCustom), () =>
    {
        #region LimitsCustom
        // Start from the preset and change only what your input needs.
        YamlLimits limits = YamlLimits.Untrusted with { MaxInputBytes = 1024 * 1024, MaxDocuments = 1 };

        try
        {
            using var doc = YamlDocument.Parse(new string('#', 2 * 1024 * 1024), new YamlDocumentOptions { Limits = limits });
        }
        catch (YamlException e)
        {
            Console.WriteLine(e.Category); // Limit
        }
        #endregion
    });

    [Fact]
    public void LimitsDepth() => DocSample.Run(nameof(LimitsDepth), () =>
    {
        #region LimitsDepth
        string deep = new string('[', 200) + new string(']', 200);
        try
        {
            YamlSerializer.Deserialize<object>(deep, new YamlSerializerOptions { Limits = YamlLimits.Untrusted });
        }
        catch (YamlException e)
        {
            Console.WriteLine(e.Message); // The nesting depth is larger than the limit MaxDepth (128). (line 1, column 129)
        }
        #endregion
    });
}
