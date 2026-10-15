using System.Globalization;
using System.Text;
using OneYaml.Documents;
using Xunit;

namespace OneYaml.Tests.Document;

/// <summary>
/// A merge patch with many null members rebuilds a mapping once, however many keys it removes, and gives
/// the bytes that removing the keys one patch at a time gives.
/// </summary>
public class MergePatchScalingTests
{
    private static string Mapping(int count)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < count; i++)
        {
            sb.Append(CultureInfo.InvariantCulture, $"k{i:D5}: value {i}\n");
        }

        return sb.ToString();
    }

    private static string Patch(IEnumerable<string> keys)
        => "{" + string.Join(",", keys.Select(k => $"\"{k}\":null")) + "}";

    private static byte[] Merge(string yaml, string patch)
    {
        using YamlDocument doc = YamlDocument.Parse(yaml);
        doc.ApplyMergePatch(patch);
        return doc.ToBytes();
    }

    // The reference: one merge patch per key, which never takes the batch path.
    private static byte[] OneByOne(string yaml, IEnumerable<string> keys)
    {
        using YamlDocument doc = YamlDocument.Parse(yaml);
        foreach (string key in keys)
        {
            doc.ApplyMergePatch(Patch([key]));
        }

        return doc.ToBytes();
    }

    [Fact]
    public void Removing_many_keys_rebuilds_the_mapping_once()
    {
        string yaml = Mapping(4000);
        string[] keys = Enumerable.Range(0, 4000).Where(i => i % 3 == 1).Select(i => $"k{i:D5}").ToArray();
        SpliceCheck.ResetThread();
        byte[] merged = Merge(yaml, Patch(keys));

        Assert.Equal(1, SpliceCheck.ThreadSpliced + SpliceCheck.ThreadFull);
        Assert.True(SpliceCheck.ThreadBytes <= 2L * yaml.Length, $"{SpliceCheck.ThreadBytes} bytes parsed for {yaml.Length}");
        Assert.Equal(OneByOne(yaml, keys), merged);
        Assert.Equal(4000 - keys.Length, Encoding.UTF8.GetString(merged).Count(c => c == '\n'));
    }

    [Fact]
    public void Keys_the_mapping_lacks_are_ignored_and_none_found_changes_nothing()
    {
        string yaml = Mapping(50);
        Assert.Equal(Encoding.UTF8.GetBytes(yaml), Merge(yaml, Patch(["nope", "other"])));
        Assert.Equal(OneByOne(yaml, ["k00003", "nope", "k00004"]), Merge(yaml, Patch(["k00003", "nope", "k00004"])));
    }

    [Fact]
    public void Removing_every_key_leaves_an_empty_mapping_as_one_at_a_time_does()
    {
        string yaml = Mapping(5);
        string[] keys = Enumerable.Range(0, 5).Select(i => $"k{i:D5}").ToArray();
        Assert.Equal(OneByOne(yaml, keys), Merge(yaml, Patch(keys)));
    }

    [Fact]
    public void An_ambiguous_key_fails_and_leaves_the_document_unchanged()
    {
        const string Yaml = "a: 1\nb: 2\n\"a\": 3\nc: 4\n";
        using var doc = YamlDocument.Parse(Yaml, new YamlDocumentOptions { AllowDuplicateKeys = true });
        YamlException ex = Assert.Throws<YamlException>(() => doc.ApplyMergePatch("{\"a\":null,\"b\":null}"));
        Assert.Equal(YamlErrorCategory.RefusedAmbiguous, ex.Category);
        Assert.Equal(Encoding.UTF8.GetBytes(Yaml), doc.ToBytes());
    }

    [Fact]
    public void Random_documents_and_patches_give_the_bytes_of_one_patch_per_key()
    {
        var rng = new Random(20261015);
        string[] shapes =
        [
            "{0}: v{1}\n",
            "# about {0}\n{0}: v{1}\n",
            "{0}: |+\n  text {1}\n\n",
            "{0}: |\n  text {1}\n",
            "{0}:\n  - a\n  - b{1}\n",
            "{0}:\n  n: {1}   # trailing\n",
            "{0}: v{1} # same line\n\n",
            "? {0}\n: v{1}\n",
            "\"{0}\": v{1}\n",
            "{0}: &a{1} v\n",
            "{0}: [x, {1}]\n",
        ];
        for (int round = 0; round < 300; round++)
        {
            int count = rng.Next(2, 14);
            var sb = new StringBuilder(rng.Next(4) == 0 ? "# head\n\n" : string.Empty);
            for (int i = 0; i < count; i++)
            {
                sb.Append(string.Format(CultureInfo.InvariantCulture, shapes[rng.Next(shapes.Length)], $"key{i}", i));
                if (rng.Next(5) == 0)
                {
                    sb.Append(rng.Next(2) == 0 ? "\n" : "# between\n");
                }
            }

            string yaml = sb.ToString();
            if (rng.Next(6) == 0)
            {
                yaml = yaml.TrimEnd('\n');
            }

            if (rng.Next(8) == 0)
            {
                yaml = yaml.Replace("\n", "\r\n", StringComparison.Ordinal);
            }

            string[] keys = Enumerable.Range(0, count).Where(_ => rng.Next(3) != 0).Select(i => $"key{i}").OrderBy(_ => rng.Next()).ToArray();
            if (keys.Length < 2)
            {
                continue;
            }

            string patch = Patch(keys);
            byte[]? expected = null;
            YamlException? expectedError = null;
            try
            {
                expected = OneByOne(yaml, keys);
            }
            catch (YamlException ex)
            {
                expectedError = ex;
            }

            if (expectedError is not null)
            {
                YamlException got = Assert.Throws<YamlException>(() => Merge(yaml, patch));
                Assert.Equal(expectedError.Category, got.Category);
                continue;
            }

            Assert.True(expected!.AsSpan().SequenceEqual(Merge(yaml, patch)), $"round {round}\n--- yaml\n{yaml}\n--- patch\n{patch}");
        }
    }
}
