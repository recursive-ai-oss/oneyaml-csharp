using System.Diagnostics;
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
    public void Many_comment_lines_after_a_failing_first_document_do_not_cost_a_parse_each()
    {
        const int Lines = 40_000;
        string yaml = "[\n" + string.Concat(Enumerable.Repeat("#\n", Lines)) + "---\nx\n";
        Type boxed = typeof(object);
        var watch = Stopwatch.StartNew();
        Assert.ThrowsAny<Cs.YamlException>(() => Cs.YamlSerializer.Deserialize<object>(yaml));
        Assert.ThrowsAny<Cs.YamlException>(() => Cs.YamlSerializer.Deserialize(yaml, boxed));
        watch.Stop();

        // One parse of the whole text takes a few milliseconds; a parse per comment line took seconds.
        Assert.True(watch.ElapsedMilliseconds < 2500, watch.ElapsedMilliseconds.ToString());
    }

    [Fact]
    public void A_run_of_comments_and_blank_lines_after_a_good_first_document_is_cut_with_the_next_document()
    {
        string yaml = "a: 1\n" + string.Concat(Enumerable.Repeat("# note\n\n", 3000)) + "%x\n---\nb: 2\n";
        var watch = Stopwatch.StartNew();
        Dictionary<string, int>? read = Cs.YamlSerializer.Deserialize<Dictionary<string, int>>(yaml);
        Assert.True(watch.ElapsedMilliseconds < 2500);
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
}
