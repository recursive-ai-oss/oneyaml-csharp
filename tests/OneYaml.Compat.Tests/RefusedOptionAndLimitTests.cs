using System.Numerics;
using System.Text.Json;
using Xunit;
using Cs = global::OneYaml.Compat.SharpYaml;
using Ydn = global::OneYaml.Compat.YamlDotNet;
using YdnCore = global::OneYaml.Compat.YamlDotNet.Core;
using YdnModel = global::OneYaml.Compat.YamlDotNet.RepresentationModel;
using YdnSer = global::OneYaml.Compat.YamlDotNet.Serialization;

namespace OneYaml.Compat.Tests;

// Options a compat package cannot honour are refused where they are first used, not accepted and dropped; and both
// packages can be told to read within stricter limits.
public class RefusedOptionAndLimitTests
{
    private static string Nested(int depth) => new string('[', depth) + new string(']', depth);

    // ---- SharpYaml: options that would change nothing ----

    [Theory]
    [InlineData("BlockSequenceMappingStyle")]
    [InlineData("BlockSequenceSequenceStyle")]
    [InlineData("ScalarStylePreferences")]
    [InlineData("ScalarStylePreferencesQuoted")]
    [InlineData("ReferenceHandling")]
    public void SharpYaml_options_that_change_nothing_are_refused_and_named(string which)
    {
        Cs.YamlSerializerOptions options = which switch
        {
            "BlockSequenceMappingStyle" => new() { BlockSequenceMappingStyle = Cs.YamlSequenceItemStyle.Expanded },
            "BlockSequenceSequenceStyle" => new() { BlockSequenceSequenceStyle = Cs.YamlSequenceItemStyle.Compact },
            "ScalarStylePreferences" => new() { ScalarStylePreferences = new Cs.YamlScalarStylePreferences { PreferPlainStyle = true } },
            "ScalarStylePreferencesQuoted" => new() { ScalarStylePreferences = new Cs.YamlScalarStylePreferences { PreferQuotedForAmbiguousScalars = true } },
            _ => new() { ReferenceHandling = Cs.YamlReferenceHandling.Preserve },
        };

        NotSupportedException ex = Assert.Throws<NotSupportedException>(() => Cs.YamlSerializer.Serialize(new Dictionary<string, int> { ["a"] = 1 }, options));
        Assert.Contains(which.Replace("Quoted", string.Empty, StringComparison.Ordinal), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SharpYaml_default_valued_layout_options_are_accepted()
    {
        var options = new Cs.YamlSerializerOptions
        {
            BlockSequenceMappingStyle = Cs.YamlSequenceItemStyle.Default,
            BlockSequenceSequenceStyle = Cs.YamlSequenceItemStyle.Default,
            ScalarStylePreferences = new Cs.YamlScalarStylePreferences(),
        };

        Assert.Equal("a: 1\n", Cs.YamlSerializer.Serialize(new Dictionary<string, int> { ["a"] = 1 }, options));
    }

    [Fact]
    public void SharpYaml_write_indented_false_is_refused_when_writing_and_not_when_reading()
    {
        var options = new Cs.YamlSerializerOptions { WriteIndented = false };

        NotSupportedException ex = Assert.Throws<NotSupportedException>(() => Cs.YamlSerializer.Serialize(new Dictionary<string, int> { ["a"] = 1 }, options));
        Assert.Contains("WriteIndented", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, Cs.YamlSerializer.Deserialize<Dictionary<string, int>>("a: 1\n", options)!["a"]);
        Assert.Equal("a: 1\n", Cs.YamlSerializer.Serialize(new Dictionary<string, int> { ["a"] = 1 }, new Cs.YamlSerializerOptions { WriteIndented = true }));
    }

    [Fact]
    public void SharpYaml_reference_handling_still_reads_aliases_and_only_writing_is_refused()
    {
        var options = new Cs.YamlSerializerOptions { ReferenceHandling = Cs.YamlReferenceHandling.Preserve };

        var read = Cs.YamlSerializer.Deserialize<Dictionary<string, List<int>>>("a: &x [1]\nb: *x\n", options);
        Assert.Equal([1], read!["b"]);
        Assert.Throws<NotSupportedException>(() => Cs.YamlSerializer.Serialize(read, options));
    }

    // ---- SharpYaml: limits ----

    [Fact]
    public void SharpYaml_limits_apply_to_every_text_and_stream_read()
    {
        var strict = new Cs.YamlSerializerOptions { Limits = new global::OneYaml.YamlLimits { MaxDepth = 5 } };
        string deep = Nested(6);

        Assert.ThrowsAny<Cs.YamlException>(() => Cs.YamlSerializer.Deserialize<object>(deep, strict));
        Assert.ThrowsAny<Cs.YamlException>(() => Cs.YamlSerializer.Deserialize<object>(deep.AsSpan(), strict));
        Assert.ThrowsAny<Cs.YamlException>(() => Cs.YamlSerializer.Deserialize<object>(new StringReader(deep), strict));
        Assert.ThrowsAny<Cs.YamlException>(() => Cs.YamlSerializer.Deserialize<object>(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(deep)), strict));
        Assert.False(Cs.YamlSerializer.TryDeserialize(deep, out object? _, strict));

        // Without the option the same text is read.
        Assert.NotNull(Cs.YamlSerializer.Deserialize<object>(deep));
        Assert.NotNull(Cs.YamlSerializer.Deserialize<object>(Nested(5), strict));
    }

    [Fact]
    public void SharpYaml_limits_cover_input_size_and_a_copied_options_object()
    {
        var strict = new Cs.YamlSerializerOptions { Limits = new global::OneYaml.YamlLimits { MaxInputBytes = 10 } };
        var copy = new Cs.YamlSerializerOptions(strict);

        Assert.ThrowsAny<Cs.YamlException>(() => Cs.YamlSerializer.Deserialize<Dictionary<string, string>>("key: a value longer than ten bytes\n", copy));
        Assert.Equal("1", Cs.YamlSerializer.Deserialize<Dictionary<string, string>>("k: 1\n", copy)!["k"]);
    }

    [Fact]
    public void SharpYaml_untrusted_limits_refuse_an_alias_expansion_the_default_allows()
    {
        // Each level doubles: 16 levels charge about 524,000 nodes, over the untrusted budget of 100,000 and under the
        // default budget of a million.
        string yaml = BombOf(16);
        var untrusted = new Cs.YamlSerializerOptions { Limits = global::OneYaml.YamlLimits.Untrusted };

        Assert.NotNull(Cs.YamlSerializer.Deserialize<object>(yaml));
        Assert.ThrowsAny<Cs.YamlException>(() => Cs.YamlSerializer.Deserialize<object>(yaml, untrusted));
    }

    private static string BombOf(int levels)
    {
        var sb = new System.Text.StringBuilder("a0: &a0 [x, x]\n");
        for (int i = 1; i <= levels; i++)
        {
            sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"a{i}: &a{i} [*a{i - 1}, *a{i - 1}]\n");
        }

        return sb.ToString();
    }

    // ---- YamlDotNet: EnsureRoundtrip ----

    [Fact]
    public void YamlDotNet_EnsureRoundtrip_is_refused_when_the_serializer_is_used()
    {
#pragma warning disable CS0618 // The call is the thing under test.
        YdnSer.ISerializer serializer = new YdnSer.SerializerBuilder().EnsureRoundtrip().Build();
#pragma warning restore CS0618

        NotSupportedException ex = Assert.Throws<NotSupportedException>(() => serializer.Serialize(new Person { Name = "z" }));
        Assert.Contains("EnsureRoundtrip", ex.Message, StringComparison.Ordinal);
        Assert.Throws<NotSupportedException>(() => serializer.Serialize(new StringWriter(), new Person()));
    }

    [Fact]
    public void YamlDotNet_EnsureRoundtrip_is_marked_obsolete_as_a_warning()
    {
        System.Reflection.MethodInfo method = typeof(YdnSer.SerializerBuilder).GetMethod(nameof(YdnSer.SerializerBuilder.EnsureRoundtrip))!;
        ObsoleteAttribute? obsolete = (ObsoleteAttribute?)Attribute.GetCustomAttribute(method, typeof(ObsoleteAttribute));

        Assert.NotNull(obsolete);
        Assert.False(obsolete.IsError);
        Assert.Contains("NotSupportedException", obsolete.Message, StringComparison.Ordinal);
    }

    // ---- YamlDotNet: limits ----

    [Fact]
    public void YamlDotNet_builder_limits_apply_to_text_and_reader_reads()
    {
        YdnSer.IDeserializer strict = new YdnSer.DeserializerBuilder().WithLimits(new global::OneYaml.YamlLimits { MaxDepth = 5 }).Build();
        YdnSer.IDeserializer normal = new YdnSer.DeserializerBuilder().Build();
        string deep = Nested(6);

        Assert.ThrowsAny<YdnCore.YamlException>(() => strict.Deserialize<object>(deep));
        Assert.ThrowsAny<YdnCore.YamlException>(() => strict.Deserialize<object>(new StringReader(deep)));
        Assert.NotNull(normal.Deserialize<object>(deep));
        Assert.NotNull(strict.Deserialize<object>(Nested(5)));
    }

    [Fact]
    public void YamlDotNet_builder_limits_cap_input_size_and_integer_digits()
    {
        var limits = new global::OneYaml.YamlLimits { MaxInputBytes = 20 };
        YdnSer.IDeserializer strict = new YdnSer.DeserializerBuilder().WithLimits(limits).Build();

        Assert.ThrowsAny<YdnCore.YamlException>(() => strict.Deserialize<object>("key: a value that is longer than twenty bytes\n"));
        Assert.ThrowsAny<YdnCore.YamlException>(() => strict.Deserialize<object>(new StringReader(new string('#', 1000) + "\na: 1\n")));

        YdnSer.IDeserializer digits = new YdnSer.DeserializerBuilder().WithLimits(new global::OneYaml.YamlLimits { MaxIntegerDigits = 5 }).Build();
        Assert.Equal((BigInteger)12345, digits.Deserialize<BigInteger>("12345"));
        Assert.ThrowsAny<YdnCore.YamlException>(() => digits.Deserialize<BigInteger>("123456789012"));
        Assert.ThrowsAny<YdnCore.YamlException>(() => digits.Deserialize<object>("!!int 123456789012"));
        Assert.Equal((BigInteger)123456789012, new YdnSer.DeserializerBuilder().Build().Deserialize<BigInteger>("123456789012"));
    }

    [Fact]
    public void YamlDotNet_untrusted_limits_are_stricter_on_depth_than_the_default()
    {
        string deep = Nested(129);
        YdnSer.IDeserializer untrusted = new YdnSer.DeserializerBuilder().WithLimits(global::OneYaml.YamlLimits.Untrusted).Build();

        Assert.ThrowsAny<YdnCore.YamlException>(() => untrusted.Deserialize<object>(deep));
        Assert.NotNull(new YdnSer.DeserializerBuilder().Build().Deserialize<object>(deep));
    }

    [Fact]
    public void YamlDotNet_limits_apply_to_stream_and_parser_loads()
    {
        var limits = new global::OneYaml.YamlLimits { MaxDepth = 5 };
        string deep = Nested(6);

        var stream = new YdnModel.YamlStream();
        Assert.ThrowsAny<YdnCore.YamlException>(() => stream.Load(new StringReader(deep), limits));
        Assert.ThrowsAny<YdnCore.YamlException>(() => new YdnCore.Parser(new StringReader(deep), limits));

        // The same text loads without limits given.
        new YdnModel.YamlStream().Load(new StringReader(deep));

        // A stream loaded under limits saves the unchanged text back.
        var ok = new YdnModel.YamlStream();
        ok.Load(new StringReader("a: [1]\n"), limits);
        var writer = new StringWriter();
        ok.Save(writer);
        Assert.Equal("a: [1]\n", writer.ToString().Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Fact]
    public void YamlDotNet_a_parser_made_under_limits_keeps_them_when_the_deserializer_reads_from_it()
    {
        YdnSer.IDeserializer normal = new YdnSer.DeserializerBuilder().Build();
        var parser = new YdnCore.Parser(new StringReader("a: " + new string('1', 30) + "\n"), new global::OneYaml.YamlLimits { MaxIntegerDigits = 5 });

        Assert.ThrowsAny<YdnCore.YamlException>(() => normal.Deserialize<Dictionary<string, BigInteger>>(parser));
    }
}
