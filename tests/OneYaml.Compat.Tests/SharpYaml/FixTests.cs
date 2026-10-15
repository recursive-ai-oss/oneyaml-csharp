using System.Numerics;
using System.Text;
using Xunit;
using Cm = global::OneYaml.Compat.YamlDotNet.Serialization;
using Cs = global::OneYaml.Compat.SharpYaml;
using Oy = global::OneYaml;

namespace OneYaml.Compat.Tests.SharpYamlCompat;

// The SharpYaml package reads the first document of a text, reads streams strictly and never turns a number that does
// not fit into infinity.
public class SharpYamlFixTests
{
    // ---- a % line is content unless it follows a document end ----

    [Theory]
    [InlineData("---\nscalar\n%YAML 1.2\n---\nx\n", "scalar %YAML 1.2")]
    [InlineData("a\n%x\n%y\n---\nb\n", "a %x %y")]
    [InlineData("--- |\nfoo\n%bar\n---\nx\n", "foo\n%bar\n")]
    [InlineData("a\n...\n%YAML 1.2\n---\nb\n", "a")]
    [InlineData("a\n...\n# note\n%YAML 1.2\n---\nb\n", "a")]
    public void A_percent_line_belongs_to_the_first_document_unless_a_document_end_comes_first(string yaml, string expected)
    {
        Assert.Equal(expected, Cs.YamlSerializer.Deserialize<string>(yaml));
        Assert.Equal(expected, Cs.YamlSerializer.Deserialize<string>(yaml.AsSpan()));
        Assert.Equal(expected, Cs.YamlSerializer.Deserialize<string>(new StringReader(yaml)));
    }

    // ---- streams are decoded strictly ----

    [Fact]
    public void A_stream_with_bytes_that_are_not_valid_utf8_is_a_syntax_error()
    {
        byte[] bytes = [.. "a: x"u8, 0xFF, .. "y\nb: 2\n"u8];
        Assert.Throws<Cs.SyntaxErrorException>(() => Cs.YamlSerializer.Deserialize<Dictionary<string, string>>(new MemoryStream(bytes)));
        Assert.False(Cs.YamlSerializer.TryDeserialize(new MemoryStream(bytes), out Dictionary<string, string>? value));
        Assert.Null(value);
    }

    [Fact]
    public void The_error_for_a_bad_byte_names_the_line()
    {
        byte[] bytes = [.. "a: 1\nb: "u8, 0xC3, 0x28, .. "\n"u8];
        var error = Assert.Throws<Cs.SyntaxErrorException>(() => Cs.YamlSerializer.Deserialize<Dictionary<string, string>>(new MemoryStream(bytes)));
        Assert.Equal(1, error.Start.Line);
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf8-bom")]
    [InlineData("utf16le-bom")]
    [InlineData("utf16be-bom")]
    [InlineData("utf16le")]
    [InlineData("utf16be")]
    [InlineData("utf32le-bom")]
    [InlineData("utf32be")]
    public void A_stream_in_any_yaml_encoding_reads_the_same_text(string kind)
    {
        const string Text = "name: héllo ☃\ncount: 3\n";
        byte[] bytes = kind switch
        {
            "utf8" => Encoding.UTF8.GetBytes(Text),
            "utf8-bom" => [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(Text)],
            "utf16le-bom" => [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(Text)],
            "utf16be-bom" => [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes(Text)],
            "utf16le" => Encoding.Unicode.GetBytes(Text),
            "utf16be" => Encoding.BigEndianUnicode.GetBytes(Text),
            "utf32le-bom" => [0xFF, 0xFE, 0, 0, .. Encoding.UTF32.GetBytes(Text)],
            _ => new UTF32Encoding(true, false).GetBytes(Text),
        };

        Dictionary<string, string>? read = Cs.YamlSerializer.Deserialize<Dictionary<string, string>>(new MemoryStream(bytes));
        Assert.Equal("héllo ☃", read!["name"]);
        Assert.Equal("3", read["count"]);
    }

    [Fact]
    public void A_stream_with_a_lone_surrogate_in_utf16_is_a_syntax_error()
    {
        byte[] bytes = [0xFF, 0xFE, (byte)'a', 0, (byte)':', 0, (byte)' ', 0, 0x00, 0xD8, (byte)'\n', 0];
        Assert.Throws<Cs.SyntaxErrorException>(() => Cs.YamlSerializer.Deserialize<Dictionary<string, string>>(new MemoryStream(bytes)));
    }

    // ---- a number that does not fit is not infinity ----

    private sealed class Reals
    {
        public double D { get; set; }

        public float F { get; set; }
    }

    [Theory]
    [InlineData("D: 1e400")]
    [InlineData("D: -1e400")]
    [InlineData("F: 1e39")]
    [InlineData("F: -1e39")]
    [InlineData("D: \"1e400\"")]
    [InlineData("F: '1e39'")]
    public void A_finite_literal_that_overflows_is_an_error_not_infinity(string yaml)
    {
        Assert.Throws<Cs.YamlException>(() => Cs.YamlSerializer.Deserialize<Reals>(yaml + "\n", new Cs.YamlSerializerOptions { PropertyNameCaseInsensitive = true }));
    }

    [Fact]
    public void Quoted_numbers_still_read_and_real_infinity_still_reads()
    {
        var options = new Cs.YamlSerializerOptions { PropertyNameCaseInsensitive = true };
        Reals read = Cs.YamlSerializer.Deserialize<Reals>("D: \"2.5\"\nF: '1e3'\n", options)!;
        Assert.Equal(2.5, read.D);
        Assert.Equal(1000f, read.F);
        Reals inf = Cs.YamlSerializer.Deserialize<Reals>("D: .inf\nF: -.inf\n", options)!;
        Assert.True(double.IsPositiveInfinity(inf.D));
        Assert.True(float.IsNegativeInfinity(inf.F));
    }

    // ---- the real library, for the differences the package documents ----

    [Fact]
    public void The_real_library_differs_where_the_documentation_says_it_does()
    {
        var options = new global::SharpYaml.YamlSerializerOptions { PropertyNameCaseInsensitive = true };

        // Overflowing numbers become infinity.
        Assert.True(double.IsPositiveInfinity(global::SharpYaml.YamlSerializer.Deserialize<Reals>("D: 1e400\n", options)!.D));
        Assert.True(float.IsPositiveInfinity(global::SharpYaml.YamlSerializer.Deserialize<Reals>("F: 1e39\n", options)!.F));

        // Bytes that are not valid UTF-8 end in a decoder exception, not a YamlException.
        byte[] bytes = [.. "a: x"u8, 0xFF, .. "y\n"u8];
        Assert.Throws<DecoderFallbackException>(() => global::SharpYaml.YamlSerializer.Deserialize<Dictionary<string, string>>(new MemoryStream(bytes)));

        // UTF-16 without a byte order mark is not detected.
        Assert.ThrowsAny<global::SharpYaml.YamlException>(() => global::SharpYaml.YamlSerializer.Deserialize<Dictionary<string, string>>(new MemoryStream(Encoding.Unicode.GetBytes("a: b\n"))));

        // A % line inside a block scalar before the next document is an error.
        Assert.ThrowsAny<global::SharpYaml.YamlException>(() => global::SharpYaml.YamlSerializer.Deserialize<string>("--- |\nfoo\n%bar\n---\nx\n"));

        // Empty text into a value type is a NullReferenceException.
        Assert.Throws<NullReferenceException>(() => global::SharpYaml.YamlSerializer.Deserialize<int>(""));
    }

    // ---- wording and the multi-document signal ----

    [Fact]
    public void The_engine_still_words_the_multi_document_refusal_the_way_the_package_looks_for()
    {
        Oy.YamlException error = Assert.Throws<Oy.YamlException>(() => Oy.YamlSerializer.Deserialize<int>("1\n---\n2\n"));
        Assert.True(Cs.ErrorMap.IsMultiDocument(error));
    }
}

public class YamlDotNetKeyFixTests
{
    [Fact]
    public void A_number_key_longer_than_an_implicit_key_is_written_raw_and_reads_back_as_a_number()
    {
        BigInteger key = BigInteger.Pow(10, 1100);
        string yaml = new Cm.Serializer().Serialize(new Dictionary<BigInteger, int> { [key] = 1 });

        Assert.StartsWith("? 1" + new string('0', 1100), yaml, StringComparison.Ordinal);
        Dictionary<BigInteger, int>? back = Oy.YamlSerializer.Deserialize<Dictionary<BigInteger, int>>(yaml);
        Assert.Equal(new Dictionary<BigInteger, int> { [key] = 1 }, back);
    }

    [Fact]
    public void A_string_key_that_looks_like_a_number_is_still_quoted_when_long()
    {
        string key = new('1', 1100);
        string yaml = new Cm.Serializer().Serialize(new Dictionary<string, int> { [key] = 1 });
        Assert.Equal(new Dictionary<string, int> { [key] = 1 }, Oy.YamlSerializer.Deserialize<Dictionary<string, int>>(yaml));
    }
}
