using System.Text;
using Xunit;

namespace OneYaml.Tests;

// Error positions for UTF-16 and UTF-32 input must not depend on the encoding or on a byte order mark.
public class EncodingErrorPositionTests
{
    private static readonly Encoding[] s_wide =
    [
        new UnicodeEncoding(false, true),
        new UnicodeEncoding(true, true),
        new UTF32Encoding(false, true),
        new UTF32Encoding(true, true),
    ];

    private static byte[] Encode(Encoding encoding, string text, bool mark)
    {
        byte[] body = encoding.GetBytes(text);
        if (!mark)
        {
            return body;
        }

        byte[] preamble = encoding.GetPreamble();
        return [.. preamble, .. body];
    }

    private static void Same(YamlException expected, YamlException actual)
    {
        Assert.Equal(expected.Category, actual.Category);
        Assert.Equal(expected.Line, actual.Line);
        Assert.Equal(expected.Column, actual.Column);
        Assert.Equal(expected.Offset, actual.Offset);
    }

    public static TheoryData<int, bool, string> SyntaxCases()
    {
        var data = new TheoryData<int, bool, string>();
        for (int e = 0; e < s_wide.Length; e++)
        {
            foreach (bool mark in new[] { true, false })
            {
                foreach (string text in new[] { "a: [1\nb: 2\n", "a: é\n\tbad: 1\n", "k: 'open\n", "a: 1\n  b: 2\n", "- x\n- y\nz: 1\n" })
                {
                    data.Add(e, mark, text);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(SyntaxCases))]
    public void A_syntax_error_has_the_position_it_has_in_UTF_8(int encodingIndex, bool mark, string text)
    {
        var reference = Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<object>(Encoding.UTF8.GetBytes(text)));
        byte[] bytes = Encode(s_wide[encodingIndex], text, mark);

        Same(reference, Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<object>(bytes)));
        Same(reference, Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<object>(new MemoryStream(bytes))));
        Same(reference, Assert.Throws<YamlException>(() => YamlDocument.Parse(bytes)));
        Same(reference, Assert.Throws<YamlException>(() => YamlDocument.Parse(new MemoryStream(bytes))));
    }

    public static TheoryData<int, bool> Encodings()
    {
        var data = new TheoryData<int, bool>();
        for (int e = 0; e < s_wide.Length; e++)
        {
            data.Add(e, true);
            data.Add(e, false);
        }

        return data;
    }

    // "a: x" then a lone high surrogate: the error is at line 1, column 5 whatever the encoding.
    [Theory]
    [MemberData(nameof(Encodings))]
    public void An_unpaired_surrogate_is_located_without_counting_the_mark(int encodingIndex, bool mark)
    {
        Encoding encoding = s_wide[encodingIndex];
        int unit = encodingIndex < 2 ? 2 : 4;

        // Build the units by hand so the lone surrogate survives encoding.
        bool big = encodingIndex is 1 or 3;
        var list = new List<byte>(mark ? encoding.GetPreamble() : []);
        foreach (int c in new[] { 'a', ':', ' ', 'x', 0xD800, 'b', '\n' })
        {
            byte[] v = unit == 2 ? [(byte)c, (byte)(c >> 8)] : [(byte)c, (byte)(c >> 8), 0, 0];
            if (big)
            {
                Array.Reverse(v);
            }

            list.AddRange(v);
        }

        byte[] input = [.. list];
        int expectedOffset = (mark ? encoding.GetPreamble().Length : 0) + (4 * unit);

        foreach (Action act in new Action[]
        {
            () => YamlSerializer.Deserialize<object>(input),
            () => YamlSerializer.Deserialize<object>(new MemoryStream(input)),
            () => YamlDocument.Parse(input),
            () => YamlDocument.Parse(new MemoryStream(input)),
        })
        {
            var ex = Assert.Throws<YamlException>(act);
            Assert.Equal(YamlErrorCategory.Encoding, ex.Category);
            Assert.Equal(1, ex.Line);
            Assert.Equal(5, ex.Column);
            Assert.Equal(expectedOffset, ex.Offset);
        }
    }

    // The reported case: UTF-16 LE with a mark, "é: " and a lone surrogate.
    [Fact]
    public void A_lone_surrogate_after_a_UTF_16_mark_is_at_column_four()
    {
        byte[] input = [0xFF, 0xFE, 0xE9, 0x00, 0x3A, 0x00, 0x20, 0x00, 0x00, 0xD8, 0x62, 0x00, 0x0A, 0x00];

        var ex = Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<object>(input));

        Assert.Equal(YamlErrorCategory.Encoding, ex.Category);
        Assert.Equal(1, ex.Line);
        Assert.Equal(4, ex.Column);
        Assert.Equal(8, ex.Offset);
    }
}
