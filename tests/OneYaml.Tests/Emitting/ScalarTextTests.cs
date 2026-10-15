using System.Buffers;
using System.Text;
using OneYaml.Emitting;
using Xunit;

namespace OneYaml.Tests.Emitting;

/// <summary>The text scan that decides which styles a string can use, against a character-by-character reference.</summary>
public class ScalarTextTests
{
    // Decodes every character and asks the rules one by one: the plain definition of the traits.
    private static TextTraits Reference(ReadOnlySpan<byte> text)
    {
        TextTraits traits = TextTraits.None;
        while (!text.IsEmpty)
        {
            OperationStatus status = Rune.DecodeFromUtf8(text, out Rune rune, out int used);
            if (status != OperationStatus.Done)
            {
                traits |= TextTraits.NeedsEscape;
                text = text.Slice(Math.Max(used, 1));
                continue;
            }

            int cp = rune.Value;
            if (cp == '\n')
            {
                traits |= TextTraits.LineBreak;
            }
            else if (cp == '\t')
            {
                traits |= TextTraits.Tab;
            }
            else if (ScalarText.MustEscape(cp))
            {
                traits |= TextTraits.NeedsEscape;
            }

            text = text.Slice(used);
        }

        return traits;
    }

    private static void AssertSame(byte[] bytes)
    {
        TextTraits want = Reference(bytes);
        TextTraits got = ScalarText.Analyze(bytes);
        Assert.True(want == got, $"{Convert.ToHexString(bytes)}: reference {want}, scan {got}");
        if (System.Text.Unicode.Utf8.IsValid(bytes))
        {
            TextTraits trusted = ScalarText.Analyze(bytes, validUtf8: true);
            Assert.True(want == trusted, $"{Convert.ToHexString(bytes)}: reference {want}, scan of known-valid text {trusted}");
        }
        else
        {
            // Claiming validity for malformed text must still not read past the end.
            _ = ScalarText.Analyze(bytes, validUtf8: true);
        }
    }

    [Fact]
    public void Every_boundary_code_point_is_classified_like_the_reference_alone_and_inside_text()
    {
        int[] points =
        [
            0, 8, 9, 10, 13, 0x1F, 0x20, 0x7E, 0x7F, 0x80, 0x84, 0x85, 0x9F, 0xA0, 0xFF, 0x100, 0x7FF, 0x800, 0x2027, 0x2028, 0x2029, 0x202A,
            0xD7FF, 0xE000, 0xFEFE, 0xFEFF, 0xFF00, 0xFFFD, 0xFFFE, 0xFFFF, 0x10000, 0x1F600, 0x10FFFF,
        ];
        foreach (int cp in points)
        {
            string s = char.ConvertFromUtf32(cp);
            foreach (string text in new[] { s, "a" + s, s + "b", "caf\u00E9 " + s + " \u4E2D\u6587", s + s, new string('x', 100) + s + new string('\u00E9', 40) })
            {
                AssertSame(Encoding.UTF8.GetBytes(text));
            }
        }
    }

    [Fact]
    public void Random_valid_and_malformed_bytes_are_classified_like_the_reference()
    {
        var random = new Random(4);
        // Bytes weighted towards the ones that start the interesting characters.
        byte[] alphabet = [0x41, 0x20, 0x0A, 0x09, 0x0D, 0x01, 0x7F, 0xC2, 0xC3, 0x80, 0x85, 0x9F, 0xA0, 0xE2, 0x28, 0xA8, 0xA9, 0xEF, 0xBB, 0xBF, 0xBE, 0xF0, 0x9F, 0x98, 0x80, 0xE4, 0xB8, 0xAD, 0xFF, 0xC0, 0xED, 0xA0];
        for (int i = 0; i < 200_000; i++)
        {
            byte[] bytes = new byte[random.Next(0, 24)];
            for (int j = 0; j < bytes.Length; j++)
            {
                bytes[j] = random.Next(4) == 0 ? (byte)random.Next(256) : alphabet[random.Next(alphabet.Length)];
            }

            AssertSame(bytes);
        }
    }

    [Fact]
    public void Random_valid_text_is_classified_like_the_reference()
    {
        var random = new Random(5);
        int[] ranges = [0x20, 0x7E, 0x80, 0x9F, 0xA0, 0x7FF, 0x800, 0x2000, 0x2020, 0x202F, 0xFE00, 0xFFFF, 0x10000, 0x10FFFF];
        for (int i = 0; i < 20_000; i++)
        {
            var sb = new StringBuilder();
            int length = random.Next(0, 80);
            for (int j = 0; j < length; j++)
            {
                int r = random.Next(ranges.Length / 2) * 2;
                int cp = random.Next(ranges[r], ranges[r + 1] + 1);
                if (cp is >= 0xD800 and <= 0xDFFF)
                {
                    cp = 0x41;
                }

                sb.Append(char.ConvertFromUtf32(cp));
            }

            AssertSame(Encoding.UTF8.GetBytes(sb.ToString()));
        }
    }
}
