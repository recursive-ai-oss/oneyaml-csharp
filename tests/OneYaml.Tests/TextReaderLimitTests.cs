using System.Text;
using Xunit;

namespace OneYaml.Tests;

/// <summary>A text reader is read only as far as the input limit allows, and the limit counts UTF-8 bytes.</summary>
public class TextReaderLimitTests
{
    private const int Limit = 1000;

    private static readonly YamlSerializerOptions Limited = new() { Limits = YamlLimits.Default with { MaxInputBytes = Limit } };

    /// <summary>Hands out a text in pieces of at most <c>piece</c> chars (and repeats a seed for ever when it has no end), counting what it supplied.</summary>
    private sealed class CountingReader(string text, int piece, bool endless) : TextReader
    {
        private int _at;

        public long Supplied { get; private set; }

        public int ReadToEndCalls { get; private set; }

        public int ReadCalls { get; private set; }

        public override string ReadToEnd()
        {
            ReadToEndCalls++;
            var sb = new StringBuilder();
            char[] part = new char[4096];
            int n;
            while ((n = Read(part, 0, part.Length)) > 0)
            {
                sb.Append(part, 0, n);
            }

            return sb.ToString();
        }

        public override int Read(char[] buffer, int index, int count)
        {
            ReadCalls++;
            int want = Math.Min(Math.Min(count, piece), 1 << 20);
            int n = 0;
            while (n < want)
            {
                if (_at == text.Length)
                {
                    if (!endless)
                    {
                        break;
                    }

                    _at = 0;
                }

                buffer[index + n++] = text[_at++];
            }

            Supplied += n;
            return n;
        }

        public override int Read(Span<char> buffer)
        {
            char[] tmp = new char[buffer.Length];
            int n = Read(tmp, 0, tmp.Length);
            tmp.AsSpan(0, n).CopyTo(buffer);
            return n;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_reader_that_never_ends_stops_one_chunk_past_the_limit(bool many)
    {
        var reader = new CountingReader("a", 1 << 20, endless: true);
        YamlException ex = many
            ? Assert.Throws<YamlException>(() => YamlSerializer.DeserializeAll<object>(reader, Limited))
            : Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<object>(reader, Limited));
        Assert.Equal(YamlErrorCategory.Limit, ex.Category);
        Assert.Contains("MaxInputBytes", ex.Message, StringComparison.Ordinal);
        Assert.InRange(reader.Supplied, Limit + 1, Limit + 4096);
    }

    [Fact]
    public void A_reader_far_larger_than_the_limit_is_not_read_to_its_end()
    {
        var reader = new CountingReader(new string('é', 1_000_000), 100, endless: false);
        YamlException ex = Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<string>(reader, Limited));
        Assert.Equal(YamlErrorCategory.Limit, ex.Category);
        Assert.True(reader.Supplied < 1_000_000);
        Assert.InRange(reader.Supplied, Limit / 2, Limit / 2 + 4096);
    }

    [Theory]
    [InlineData("é", 2, 1)]
    [InlineData("é", 2, 4096)]
    [InlineData("€", 3, 1)]
    [InlineData("😀", 4, 1)]
    [InlineData("😀", 4, 3)]
    [InlineData("😀", 4, 4096)]
    public void The_limit_counts_utf8_bytes_exactly(string unit, int bytesPerUnit, int piece)
    {
        // Padding with ASCII makes the text exactly the limit when the unit does not divide it.
        string atLimit = string.Concat(Enumerable.Repeat(unit, Limit / bytesPerUnit)) + new string('x', Limit % bytesPerUnit);
        Assert.Equal(Limit, Encoding.UTF8.GetByteCount(atLimit));

        Assert.Equal(atLimit, YamlSerializer.Deserialize<string>(new CountingReader(atLimit, piece, endless: false), Limited));
        Assert.Equal([atLimit], YamlSerializer.DeserializeAll<string>(new CountingReader(atLimit, piece, endless: false), Limited));

        string over = atLimit + unit;
        YamlException ex = Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<string>(new CountingReader(over, piece, endless: false), Limited));
        Assert.Equal(YamlErrorCategory.Limit, ex.Category);
        ex = Assert.Throws<YamlException>(() => YamlSerializer.DeserializeAll<string>(new CountingReader(over, piece, endless: false), Limited));
        Assert.Equal(YamlErrorCategory.Limit, ex.Category);
    }

    [Fact]
    public void A_byte_order_mark_is_not_counted_and_the_text_is_the_same_as_from_a_string()
    {
        string atLimit = new('x', Limit);
        string withMark = "\uFEFF" + atLimit;
        Assert.Equal(atLimit, YamlSerializer.Deserialize<string>(new CountingReader(withMark, 7, endless: false), Limited));
        Assert.Equal(atLimit, YamlSerializer.Deserialize<string>(withMark, Limited));
    }

    [Fact]
    public void Several_documents_within_the_limit_read_as_from_a_string()
    {
        const string Text = "a: 1\n---\nb: é\n---\n- 😀\n";
        var viaReader = YamlSerializer.DeserializeAll<object>(new CountingReader(Text, 3, endless: false), Limited);
        var viaString = YamlSerializer.DeserializeAll<object>(Text, Limited);
        Assert.Equal(viaString.Count, viaReader.Count);
        Assert.Equal(3, viaReader.Count);
    }

    [Fact]
    public void A_source_name_is_kept_on_the_limit_error()
    {
        var options = new YamlSerializerOptions { Limits = Limited.Limits, SourceName = "config.yaml" };
        YamlException ex = Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<object>(new CountingReader("a", 4096, endless: true), options));
        Assert.Equal("config.yaml", ex.SourceName);
    }

    [Fact]
    public void With_no_input_limit_the_reader_is_read_to_its_end_in_one_call()
    {
        var options = new YamlSerializerOptions { Limits = YamlLimits.None };
        var reader = new CountingReader("k: v\n", 1 << 20, endless: false);
        Assert.NotNull(YamlSerializer.Deserialize<object>(reader, options));
        Assert.Equal(5, reader.Supplied);
        Assert.Equal(1, reader.ReadToEndCalls);
    }

    [Fact]
    public void With_a_limit_the_reader_is_not_read_to_its_end_in_one_call()
    {
        var reader = new CountingReader("k: v\n", 1 << 20, endless: false);
        Assert.NotNull(YamlSerializer.Deserialize<object>(reader, Limited));
        Assert.Equal(0, reader.ReadToEndCalls);
        Assert.True(reader.ReadCalls >= 2);
    }

    [Fact]
    public void An_unpaired_surrogate_is_an_encoding_error_at_the_same_place_as_from_a_string()
    {
        foreach (string text in new[] { "a: 1\nb: x\uD800y\n", "a: 1\nb: x\uDC00\n", "k: \uD83D", "\uFEFFab\r\n\uD800", "é€😀: \uD800" })
        {
            foreach (int piece in new[] { 1, 2, 3, 4096 })
            {
                YamlException viaString = Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<object>(text, Limited));
                YamlException viaReader = Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<object>(new CountingReader(text, piece, endless: false), Limited));
                Assert.Equal(YamlErrorCategory.Encoding, viaReader.Category);
                Assert.Equal((viaString.Message, viaString.Line, viaString.Column, viaString.Offset), (viaReader.Message, viaReader.Line, viaReader.Column, viaReader.Offset));
            }
        }
    }

    [Fact]
    public void The_limit_error_wins_over_an_unpaired_surrogate_as_it_does_for_a_string()
    {
        string text = "a: \uD800" + new string('x', 2 * Limit);
        Assert.Equal(YamlErrorCategory.Limit, Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<object>(text, Limited)).Category);
        Assert.Equal(YamlErrorCategory.Limit, Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<object>(new CountingReader(text, 5, endless: false), Limited)).Category);
    }

    [Fact]
    public void A_control_character_is_a_syntax_error_as_from_a_string()
    {
        const string Text = "a: 1\n\u0001b\n";
        YamlException viaString = Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<object>(Text, Limited));
        YamlException viaReader = Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<object>(new CountingReader(Text, 2, endless: false), Limited));
        Assert.Equal((viaString.Category, viaString.Message, viaString.Line, viaString.Column), (viaReader.Category, viaReader.Message, viaReader.Line, viaReader.Column));
    }
}
