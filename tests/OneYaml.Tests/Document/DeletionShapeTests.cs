using System.Globalization;
using System.Text;
using Xunit;

namespace OneYaml.Tests.Document;

/// <summary>
/// Merge patches that delete keys from documents built out of many entry shapes (block scalars, comments,
/// blank lines, flow values). Every state an edit makes is compared with a fresh parse of its bytes by
/// the check in <see cref="SpliceCheck"/>, so a splice that reads differently fails here.
/// </summary>
public class DeletionShapeTests
{
    private static readonly string[] s_shapes =
    [
        "{0}: v{1}\n", "# about {0}\n{0}: v{1}\n", "{0}: |+\n  text {1}\n\n", "{0}: |\n  text {1}\n", "{0}: >-\n  text {1}\n",
        "{0}:\n  - a\n  - b{1}\n", "{0}:\n  n: {1}   # trailing\n  m: 2\n", "{0}: v{1} # same line\n\n", "? {0}\n: v{1}\n",
        "\"{0}\": v{1}\n", "{0}: &a{1} v\n", "{0}: *a0\n", "{0}: multi\n  line {1}\n", "{0}: \"q\n  uoted\"\n\n# x\n",
        "# a\n\n# b\n{0}: z\n", "{0}: [x, {1}]\n", "{0}: {{p: 1}}\n", "{0}:\n", "\n", "  # indented comment\n{0}: z\n",
        "{0}: |-\n   text {1}\n\n  # c\n", "{0}: |\n  text {1}\n  # c\n\n# d\n",
    ];

    [Theory]
    [InlineData(77)]
    [InlineData(78)]
    [InlineData(79)]
    public void A_deletion_from_a_mixed_document_reads_like_a_fresh_parse(int seed) => Run(seed);

    [Theory]
    [Trait("Category", "Slow")]
    [MemberData(nameof(ManySeeds))]
    public void Many_seeds_of_deletions_read_like_a_fresh_parse(int seed) => Run(seed);

    public static IEnumerable<object[]> ManySeeds() => Enumerable.Range(100, 40).Select(i => new object[] { i });

    private static void Run(int seed)
    {
        var rng = new Random(seed);
        var failures = new List<string>();
        for (int round = 0; round < 1500; round++)
        {
            int count = rng.Next(1, 16);
            var sb = new StringBuilder(rng.Next(4) == 0 ? "# head\n\n" : "");
            for (int i = 0; i < count; i++)
            {
                string shape = s_shapes[rng.Next(s_shapes.Length)];
                if (shape == "\n")
                {
                    sb.Append(shape);
                    continue;
                }

                sb.Append(string.Format(CultureInfo.InvariantCulture, shape, "key" + i, i));
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

            var members = new List<string>();
            for (int i = 0; i < count; i++)
            {
                int r = rng.Next(5);
                if (r < 2)
                {
                    members.Add($"\"key{i}\":null");
                }
                else if (r == 2)
                {
                    members.Add($"\"key{i}\":{rng.Next(100)}");
                }
            }

            string patch = "{" + string.Join(",", members.OrderBy(_ => rng.Next())) + "}";
            try
            {
                using var doc = YamlDocument.Parse(yaml, new YamlDocumentOptions { AllowDuplicateKeys = rng.Next(2) == 0 });
                doc.ApplyMergePatch(patch);
            }
            catch (YamlException)
            {
                // A refusal is a result; only a state that disagrees with its bytes is a failure.
            }
            catch (Exception e)
            {
                failures.Add($"{e.GetType().Name}: {e.Message}\n--- yaml\n{yaml.Replace("\r", "\\r", StringComparison.Ordinal)}\n--- patch\n{patch}");
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} failures; first:\n{(failures.Count > 0 ? failures[0] : "")}");
    }
}
