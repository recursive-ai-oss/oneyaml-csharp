using Xunit;
using Cm = global::OneYaml.Compat.YamlDotNet;
using Rl = global::YamlDotNet;

namespace OneYaml.Compat.Tests;

// A syntax error after non-ASCII text is placed where YamlDotNet places it: Index counts characters, not UTF-8 bytes.
public class NonAsciiErrorMarkTests
{
    public static TheoryData<string> Texts() => new()
    {
        "é: 1\nb: [1, 2\n",
        "ключ: значение\nlist: [a, b\n",
        "k: тест\nb: @x\n",
        "a: 1\né: [x\n",
        "日本語: 1\nb: {a: 1\n",
        "x: 😀\ny: [1\n",
    };

    [Theory]
    [MemberData(nameof(Texts))]
    public void A_syntax_error_after_non_ASCII_text_has_the_mark_YamlDotNet_gives(string text)
    {
        Rl.Core.YamlException real = Assert.ThrowsAny<Rl.Core.YamlException>(() => new Rl.RepresentationModel.YamlStream().Load(new StringReader(text)));
        Cm.Core.YamlException compat = Assert.ThrowsAny<Cm.Core.YamlException>(() => new Cm.RepresentationModel.YamlStream().Load(new StringReader(text)));

        Assert.Equal((real.Start.Line, real.Start.Column, real.Start.Index), (compat.Start.Line, compat.Start.Column, compat.Start.Index));
    }

    [Theory]
    [MemberData(nameof(Texts))]
    public void The_mark_is_the_same_when_the_text_comes_from_the_deserializer(string text)
    {
        Rl.Core.YamlException real = Assert.ThrowsAny<Rl.Core.YamlException>(() => new Rl.Serialization.DeserializerBuilder().Build().Deserialize<object>(new StringReader(text)));
        Cm.Core.YamlException compat = Assert.ThrowsAny<Cm.Core.YamlException>(() => new Cm.Serialization.DeserializerBuilder().Build().Deserialize<object>(new StringReader(text)));

        Assert.Equal((real.Start.Line, real.Start.Column, real.Start.Index), (compat.Start.Line, compat.Start.Column, compat.Start.Index));
    }
}
