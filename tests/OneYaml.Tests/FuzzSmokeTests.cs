using OneYaml.Tests.Fuzzing;
using Xunit;

namespace OneYaml.Tests;

/// <summary>A short, repeatable run of the fuzz checks; the long campaigns use the OneYaml.Fuzz tool.</summary>
public class FuzzSmokeTests
{
    private static readonly string[] s_inlineSeeds =
    [
        "a: 1\nb:\n  - x\n  - {c: d, e: [f, g]}\n",
        "--- !!map\n&a k: *a\n? [x, y]\n: |\n  text\n...\n%YAML 1.2\n---\n'q''s'\n",
        "\"esc\\x41\\u00e9\\\n  line\": >-\n  folded\n   more\n\n  end\n",
        "# c\n- - - a\n  - b\n-\n  - !e!t c\n",
        "{a: 1, b, ? c, : d, [e]: f}\n",
        "key: 'multi\n  line'   # trailing\nother:\tvalue\n",
    ];

    private static List<byte[]> Seeds()
    {
        var seeds = s_inlineSeeds.Select(s => System.Text.Encoding.UTF8.GetBytes(s)).ToList();
        foreach (string id in Corpus.SuiteCaseIds().Take(400))
        {
            seeds.Add(File.ReadAllBytes(Path.Combine(Corpus.SuiteDirectory!, id.Replace('/', Path.DirectorySeparatorChar), "in.yaml")));
        }

        return seeds;
    }

    private static void Run(int iterations, int seed)
    {
        List<byte[]> seeds = Seeds();
        var mutator = new Mutator(seed);
        YamlLimits[] limits = [YamlLimits.Default, YamlLimits.Untrusted];
        for (int i = 0; i < iterations; i++)
        {
            byte[] input = mutator.Mutate(seeds[mutator.Next(seeds.Count)], seeds, 16384);
            try
            {
                ReaderInvariants.Check(input, limits[i % 2]);
            }
            catch (Exception ex) when (ex is not YamlException)
            {
                Assert.Fail($"input {Convert.ToBase64String(input)}: {ex}");
            }
        }
    }

    [Fact]
    public void Mutated_inputs_end_in_events_or_a_yaml_exception()
    {
        Run(3000, seed: 20260101);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void Many_more_mutated_inputs_end_in_events_or_a_yaml_exception()
    {
        Run(200_000, seed: 7);
    }
}
