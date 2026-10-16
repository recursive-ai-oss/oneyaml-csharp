using System.Text;
using Xunit;
using Cs = global::OneYaml.Compat.SharpYaml;
using Real = global::SharpYaml;

namespace OneYaml.Compat.Tests.SharpYamlCompat;

// Wide number types, long runs of comments after a first document, and error marks in UTF-16 and UTF-32 input.
public class WideAndRetryTests
{
    private sealed class Wide
    {
        public Int128 A { get; set; }

        public UInt128 B { get; set; }

        public Half C { get; set; }
    }

    private static void SameAsReal<T>(T value)
    {
        string real = Real.YamlSerializer.Serialize(value);
        string ours = Cs.YamlSerializer.Serialize(value);
        // Floats are the one difference in text: a whole number or an exponent form is written with a ".0" so that it
        // stays a float when read back. The value is the same, so the real library's text must read as the same value.
        if (value is Half && (real.Contains('E', StringComparison.Ordinal) || !real.Contains('.', StringComparison.Ordinal)))
        {
            Assert.Equal(value, Cs.YamlSerializer.Deserialize<T>(real));
        }
        else
        {
            Assert.Equal(real, ours);
        }

        Assert.NotEqual("{}", ours.Trim());

        T? back = Cs.YamlSerializer.Deserialize<T>(ours);
        Assert.Equal(value, back);
        Assert.Equal(Real.YamlSerializer.Deserialize<T>(ours), back);
    }

    [Fact]
    public void A_wide_number_is_written_and_read_like_the_real_library()
    {
        foreach (Int128 v in new Int128[] { Int128.MaxValue, Int128.MinValue, 0, -5 })
        {
            SameAsReal(v);
        }

        foreach (UInt128 v in new UInt128[] { UInt128.MaxValue, 0, 42 })
        {
            SameAsReal(v);
        }

        foreach (Half v in new Half[] { (Half)1.5, (Half)0.1, Half.MaxValue, Half.Epsilon, (Half)(-2) })
        {
            SameAsReal(v);
        }
    }

    [Fact]
    public void Wide_numbers_as_members_match_the_real_library()
    {
        var value = new Wide { A = Int128.MinValue + 1, B = UInt128.MaxValue - 1, C = (Half)0.5 };
        var realOptions = new Real.YamlSerializerOptions { PropertyNameCaseInsensitive = true };
        string real = Real.YamlSerializer.Serialize(value, realOptions);
        string ours = Cs.YamlSerializer.Serialize(value);
        Assert.Equal(real, ours);
        Wide back = Cs.YamlSerializer.Deserialize<Wide>(ours, new Cs.YamlSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal(value.A, back.A);
        Assert.Equal(value.B, back.B);
        Assert.Equal(value.C, back.C);
    }

    [Fact]
    public void Many_comment_lines_after_a_failing_first_document_still_report_the_error()
    {
        const int Lines = 40_000;
        string yaml = "[\n" + string.Concat(Enumerable.Repeat("#\n", Lines)) + "---\nx\n";
        Type boxed = typeof(object);
        Assert.ThrowsAny<Cs.YamlException>(() => Cs.YamlSerializer.Deserialize<object>(yaml));
        Assert.ThrowsAny<Cs.YamlException>(() => Cs.YamlSerializer.Deserialize(yaml, boxed));
    }

    [Fact]
    public void A_run_of_comments_and_blank_lines_after_a_good_first_document_is_cut_with_the_next_document()
    {
        string yaml = "a: 1\n" + string.Concat(Enumerable.Repeat("# note\n\n", 3000)) + "%x\n---\nb: 2\n";
        Dictionary<string, int>? read = Cs.YamlSerializer.Deserialize<Dictionary<string, int>>(yaml);
        Assert.Single(read!);
        Assert.Equal(1, read!["a"]);
    }

    [Fact]
    public void The_error_mark_for_a_bad_character_in_utf16_counts_characters_not_bytes()
    {
        byte[] bytes = [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("a: 1\nb: 2\nc: x"), 0x00, 0xD8, .. Encoding.Unicode.GetBytes("\n")];
        var error = Assert.Throws<Cs.SyntaxErrorException>(() => Cs.YamlSerializer.Deserialize<Dictionary<string, string>>(new MemoryStream(bytes)));
        Assert.Equal(2, error.Start.Line);
        Assert.Equal(4, error.Start.Column);
    }

    [Fact]
    public void The_error_mark_for_a_bad_character_in_utf32_counts_characters_not_bytes()
    {
        byte[] bytes = [0xFF, 0xFE, 0, 0, .. new UTF32Encoding(false, false).GetBytes("a: 1\nb: "), 0x00, 0x00, 0x11, 0x00, .. new UTF32Encoding(false, false).GetBytes("\n")];
        var error = Assert.Throws<Cs.SyntaxErrorException>(() => Cs.YamlSerializer.Deserialize<Dictionary<string, string>>(new MemoryStream(bytes)));
        Assert.Equal(1, error.Start.Line);
        Assert.Equal(3, error.Start.Column);
    }

    [Theory]
    [InlineData("a\n%x\n#c\n%YAML 1.2\n---\nb\n", "a %x")]
    [InlineData("a\n%\n# c\n%YAML 1.2\n---\nb\n", "a %")]
    [InlineData("a\n  t\n%YAML 1.2\n#c\n%YAML 1.2\n---\nb\n", "a t %YAML 1.2")]
    [InlineData("a\n%x\n\n#c\n%YAML 1.2\n---\nb\n", "a %x")]
    public void A_percent_line_that_continues_a_plain_scalar_stays_in_the_first_document_like_the_real_library(string yaml, string expected)
    {
        Type boxed = typeof(object);
        Assert.Equal(expected, Real.YamlSerializer.Deserialize<object>(yaml));
        Assert.Equal(expected, Cs.YamlSerializer.Deserialize<object>(yaml));
        Assert.Equal(expected, Cs.YamlSerializer.Deserialize(yaml, boxed));
    }

    [Theory]
    [InlineData("a: 1\n# c\n%YAML 1.2\n---\nb: 2\n")]
    [InlineData("a: 1\n\n# c\n\n%YAML 1.2\n---\nb: 2\n")]
    [InlineData("a: 1\n%TAG ! tag:example.com,2000:\n---\nb: 2\n")]
    public void A_directive_after_a_block_collection_belongs_to_the_next_document_like_the_real_library(string yaml)
    {
        Dictionary<string, int>? real = Real.YamlSerializer.Deserialize<Dictionary<string, int>>(yaml);
        Dictionary<string, int>? ours = Cs.YamlSerializer.Deserialize<Dictionary<string, int>>(yaml);
        Assert.Equal(real, ours);
        Assert.Equal(1, ours!.Single().Value);
    }

    [Fact]
    public void Many_comment_lines_between_a_first_scalar_and_a_directive_end_the_first_document_there()
    {
        Type boxed = typeof(object);
        string yaml = "a\n" + string.Concat(Enumerable.Repeat("#\n", 40_000)) + "%YAML 1.2\n---\nb\n";
        Assert.Equal("a", Cs.YamlSerializer.Deserialize<object>(yaml));
        Assert.Equal("a", Cs.YamlSerializer.Deserialize(yaml, boxed));
    }

    [Theory]
    [InlineData("\"5\"", 5)]
    [InlineData("'5'", 5)]
    [InlineData("\"-7\"", -7)]
    public void A_quoted_whole_number_is_read_into_the_wide_types_like_the_real_library(string yaml, int expected)
    {
        Assert.Equal((Int128)expected, Real.YamlSerializer.Deserialize<Int128>(yaml));
        Assert.Equal((Int128)expected, Cs.YamlSerializer.Deserialize<Int128>(yaml));
        Assert.Equal((Half)expected, Real.YamlSerializer.Deserialize<Half>(yaml));
        Assert.Equal((Half)expected, Cs.YamlSerializer.Deserialize<Half>(yaml));
        if (expected >= 0)
        {
            Assert.Equal((UInt128)expected, Real.YamlSerializer.Deserialize<UInt128>(yaml));
            Assert.Equal((UInt128)expected, Cs.YamlSerializer.Deserialize<UInt128>(yaml));
        }
        else
        {
            Assert.ThrowsAny<Exception>(() => Real.YamlSerializer.Deserialize<UInt128>(yaml));
            Assert.ThrowsAny<Cs.YamlException>(() => Cs.YamlSerializer.Deserialize<UInt128>(yaml));
        }
    }

    [Theory]
    [InlineData("\"1.5\"")]
    [InlineData("'0.1'")]
    [InlineData("\"1e3\"")]
    public void A_quoted_decimal_is_read_into_half_like_the_real_library_and_refused_for_the_integer_types(string yaml)
    {
        Assert.Equal(Real.YamlSerializer.Deserialize<Half>(yaml), Cs.YamlSerializer.Deserialize<Half>(yaml));
        Assert.ThrowsAny<Exception>(() => Real.YamlSerializer.Deserialize<Int128>(yaml));
        Assert.ThrowsAny<Cs.YamlException>(() => Cs.YamlSerializer.Deserialize<Int128>(yaml));
        Assert.ThrowsAny<Cs.YamlException>(() => Cs.YamlSerializer.Deserialize<UInt128>(yaml));
    }

    [Fact]
    public void A_quoted_number_that_overflows_half_is_an_error_here_and_infinity_in_the_real_library()
    {
        Assert.True(Half.IsInfinity(Real.YamlSerializer.Deserialize<Half>("\"1e10\"")));
        Assert.ThrowsAny<Cs.YamlException>(() => Cs.YamlSerializer.Deserialize<Half>("\"1e10\""));
    }

    [Fact]
    public void Hexadecimal_integers_and_special_floats_differ_from_the_real_library_in_the_documented_way()
    {
        // Documented in docs/compat/sharpyaml.md: YAML 1.2 hexadecimal integers read into the wide types and Half, and
        // the special floats are written and read in their YAML 1.2 forms.
        Assert.ThrowsAny<Exception>(() => Real.YamlSerializer.Deserialize<Int128>("0x10"));
        Assert.Equal((Int128)16, Cs.YamlSerializer.Deserialize<Int128>("0x10"));
        Assert.ThrowsAny<Exception>(() => Real.YamlSerializer.Deserialize<UInt128>("0x10"));
        Assert.Equal((UInt128)16, Cs.YamlSerializer.Deserialize<UInt128>("0x10"));
        Assert.ThrowsAny<Exception>(() => Real.YamlSerializer.Deserialize<Half>("0x10"));
        Assert.Equal((Half)16, Cs.YamlSerializer.Deserialize<Half>("0x10"));

        Assert.ThrowsAny<Exception>(() => Real.YamlSerializer.Deserialize<Half>(".inf"));
        Assert.True(Half.IsPositiveInfinity(Cs.YamlSerializer.Deserialize<Half>(".inf")));
        Assert.ThrowsAny<Exception>(() => Real.YamlSerializer.Deserialize<Half>(".nan"));
        Assert.True(Half.IsNaN(Cs.YamlSerializer.Deserialize<Half>(".nan")));

        Assert.Equal("NaN\n", Real.YamlSerializer.Serialize(Half.NaN));
        Assert.Equal(".nan\n", Cs.YamlSerializer.Serialize(Half.NaN));
        Assert.Equal("Infinity\n", Real.YamlSerializer.Serialize(Half.PositiveInfinity));
        Assert.Equal(".inf\n", Cs.YamlSerializer.Serialize(Half.PositiveInfinity));
        Assert.Equal("-.inf\n", Cs.YamlSerializer.Serialize(Half.NegativeInfinity));
    }
}
