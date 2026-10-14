using OneYaml.Tests.Fuzzing;
using Xunit;

namespace OneYaml.Tests;

/// <summary>Mutated documents read generically and into several target types end in a value or a YamlException, nothing else.</summary>
public class BinderFuzzTests
{
    private static readonly string[] s_inlineSeeds =
    [
        "name: Ada\nage: 36\ntags: [a, b]\nnickname: ~\n",
        "&a {name: x, children: [&b {name: y}, *b]}\nz: *a\n",
        "- &x [1, 2, 0x3, 4.5, .inf]\n- *x\n- {<<: *x, k: v}\n- !!binary aGk=\n",
        "? [a, b]\n: c\n1: x\n0x1: y\n",
        "--- 1\n--- two\n---\n- 3\n",
        "a: &k b\n*k : c\n",
    ];

    private static List<byte[]> Seeds()
    {
        var seeds = s_inlineSeeds.Select(s => System.Text.Encoding.UTF8.GetBytes(s)).ToList();
        foreach (string id in Corpus.SuiteCaseIds().Take(300))
        {
            seeds.Add(File.ReadAllBytes(Path.Combine(Corpus.SuiteDirectory!, id.Replace('/', Path.DirectorySeparatorChar), "in.yaml")));
        }

        return seeds;
    }

    private static void Run(int iterations, int seed)
    {
        List<byte[]> seeds = Seeds();
        var mutator = new Mutator(seed);
        YamlSerializerOptions[] options =
        [
            new() { Limits = YamlLimits.Default },
            new() { Limits = YamlLimits.Untrusted, MergeKeys = true, PropertyNameCaseInsensitive = true },
            new() { UnmappedMemberHandling = YamlUnmappedMemberHandling.Disallow },
        ];
        for (int i = 0; i < iterations; i++)
        {
            byte[] input = mutator.Mutate(seeds[mutator.Next(seeds.Count)], seeds, 16384);
            YamlSerializerOptions o = options[i % options.Length];
            Try(input, () => YamlSerializer.DeserializeAll<object>(input, o));
            Try(input, () => YamlSerializer.Deserialize<Person>(input, o));
            Try(input, () => YamlSerializer.Deserialize<Node>(input, o));
            Try(input, () => YamlSerializer.Deserialize<Dictionary<int, List<string>>>(input, o));
            Try(input, () => YamlSerializer.Deserialize<Scalars>(input, o));
        }
    }

    private static void Try(byte[] input, Action read)
    {
        try
        {
            read();
        }
        catch (YamlException)
        {
            // The documented outcome for input that is not valid or does not fit.
        }
        catch (Exception ex)
        {
            Assert.Fail($"input {Convert.ToBase64String(input)}: {ex}");
        }
    }

    [Fact]
    public void Mutated_inputs_bind_or_end_in_a_yaml_exception()
    {
        if (Corpus.SuiteDirectory is null)
        {
            Assert.Skip($"{Corpus.SuiteVariable} is not set");
        }

        Run(2000, seed: 20260102);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void Many_more_mutated_inputs_bind_or_end_in_a_yaml_exception()
    {
        if (Corpus.SuiteDirectory is null)
        {
            Assert.Skip($"{Corpus.SuiteVariable} is not set");
        }

        Run(100_000, seed: 11);
    }
}
