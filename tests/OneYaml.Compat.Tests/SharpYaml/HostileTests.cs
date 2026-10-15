using System.Text;
using Xunit;
using Cs = global::OneYaml.Compat.SharpYaml;
using Rs = global::SharpYaml;

namespace OneYaml.Compat.Tests.SharpYamlCompat;

// Hostile and odd inputs: the package must end in its own YamlException (or an argument error), never in another exception
// type, and never by running out of stack.
public class SharpYamlHostileTests
{
    private static readonly Type ListType = typeof(List<object>);

    private static void Contract(Action call)
    {
        try
        {
            call();
        }
        catch (Cs.YamlException)
        {
        }
    }

    [Fact]
    public void Deep_nesting_is_a_limit_error_not_a_crash()
    {
        foreach (int depth in new[] { 100, 5_000, 200_000 })
        {
            string flow = new string('[', depth) + new string(']', depth);
            var block = new StringBuilder();
            for (int i = 0; i < Math.Min(depth, 20_000); i++)
            {
                block.Append(' ', i).Append("k:\n");
            }

            string maps = string.Concat(Enumerable.Repeat("{a: ", depth)) + "1" + new string('}', depth);
            foreach (string text in new[] { flow, block.ToString(), maps })
            {
                Assert.ThrowsAny<Cs.YamlException>(() => Cs.YamlSerializer.Deserialize<object>(text));
                Assert.ThrowsAny<Cs.YamlException>(() => Cs.YamlSerializer.Deserialize<List<object>>(text));
                Assert.ThrowsAny<Cs.YamlException>(() => Cs.YamlSerializer.Deserialize<Cycle>(text));
                Assert.False(Cs.YamlSerializer.TryDeserialize<object>(text, out _));
            }
        }
    }

    [Fact]
    public void A_depth_option_is_honoured_and_never_lifted_past_the_library_limit()
    {
        string text = string.Concat(Enumerable.Repeat("[", 70)) + string.Concat(Enumerable.Repeat("]", 70));
        Assert.ThrowsAny<Cs.YamlException>(() => Cs.YamlSerializer.Deserialize<object>(text));
        _ = Cs.YamlSerializer.Deserialize<object>(text, new Cs.YamlSerializerOptions { MaxDepth = 200 });
        string deep = new string('[', 5_000) + new string(']', 5_000);
        Assert.ThrowsAny<Cs.YamlException>(() => Cs.YamlSerializer.Deserialize<object>(deep, new Cs.YamlSerializerOptions { MaxDepth = int.MaxValue }));
        Assert.ThrowsAny<Cs.YamlException>(() => Cs.YamlSerializer.Deserialize<object>("[[[1]]]", new Cs.YamlSerializerOptions { MaxDepth = 2 }));
    }

    [Fact]
    public void An_alias_bomb_is_a_limit_error()
    {
        var sb = new StringBuilder("a0: &a0 [x, x, x, x, x, x, x, x, x, x]\n");
        for (int i = 1; i < 12; i++)
        {
            sb.Append('a').Append(i).Append(": &a").Append(i).Append(" [");
            sb.Append(string.Join(", ", Enumerable.Repeat("*a" + (i - 1), 10)));
            sb.Append("]\n");
        }

        string bomb = sb.ToString();
        Assert.ThrowsAny<Cs.YamlException>(() => Cs.YamlSerializer.Deserialize<object>(bomb));
        Assert.ThrowsAny<Cs.YamlException>(() => Cs.YamlSerializer.Deserialize<Dictionary<string, List<object>>>(bomb));
        Assert.ThrowsAny<Cs.YamlException>(() => Cs.YamlSerializer.Deserialize<Dictionary<string, List<List<List<List<List<List<List<List<List<List<List<string>>>>>>>>>>>>>(bomb));
    }

    [Fact]
    public void Aliases_are_followed_within_the_limits()
    {
        const string Text = "base: &b {x: 1, y: [a, b]}\nuse: *b\nlist: [*b, *b]\n";
        object? read = Cs.YamlSerializer.Deserialize<object>(Text);
        var map = Assert.IsAssignableFrom<IDictionary<string, object?>>(read);
        Assert.Equal(Differ.Dump(map["base"]), Differ.Dump(map["use"]));
        Assert.Equal(2, ((System.Collections.IList)map["list"]!).Count);
        var typed = Cs.YamlSerializer.Deserialize<Dictionary<string, object>>(Text);
        Assert.Equal(3, typed!.Count);
    }

    [Fact]
    public void A_key_longer_than_the_implicit_limit_is_written_so_that_it_reads_back()
    {
        string key = new string('k', 3_000);
        var value = new Dictionary<string, int> { [key] = 1, ["short"] = 2, [new string('é', 2_000)] = 3, ["multi\nline"] = 4, [""] = 5 };
        string text = Cs.YamlSerializer.Serialize(value);
        Dictionary<string, int>? back = Cs.YamlSerializer.Deserialize<Dictionary<string, int>>(text);
        Assert.Equal(value.OrderBy(p => p.Key, StringComparer.Ordinal), back!.OrderBy(p => p.Key, StringComparer.Ordinal));
        _ = OneYaml.YamlDocument.Parse(text);
    }

    [Fact]
    public void Odd_text_values_round_trip()
    {
        string[] values =
        [
            "", " ", "\n", "a\nb", "a\r\nb", "\ttab", "trailing\t", "é", "日本語", "😀", "\u0001", "\u007f", "\u0085", "\u2028", "# not a comment", "key: value", "- item", "? q", "---", "...", "%TAG", "&a", "*a", "!tag", "|", ">", "'", "\"", "{", "[", "~", "null", "Null", "true", "1", "1.5", "0x10", "1e5", ".inf", "yes", "no", "on", "off", "y", "n", "<<", "=", "@", "`", new string('x', 100_000), "a: b\n  c",
        ];
        string text = Cs.YamlSerializer.Serialize(values);
        string[]? back = Cs.YamlSerializer.Deserialize<string[]>(text);
        Assert.Equal(values, back);
        string[]? viaReal = Rs.YamlSerializer.Deserialize<string[]>(text);
        Assert.Equal(values.Length, viaReal!.Length);
    }

    [Fact]
    public void A_cycle_in_the_object_graph_is_an_error_not_a_crash()
    {
        var cycle = new Cycle { Value = 1 };
        cycle.Next = cycle;
        Assert.ThrowsAny<Exception>(() => Rs.YamlSerializer.Serialize(cycle));
        Assert.ThrowsAny<Cs.YamlException>(() => Cs.YamlSerializer.Serialize(cycle));
        var list = new List<object>();
        list.Add(list);
        Assert.ThrowsAny<Cs.YamlException>(() => Cs.YamlSerializer.Serialize(list));
    }

    [Fact]
    public void Garbage_input_ends_in_the_package_exception()
    {
        var random = new Random(1234);
        string alphabet = "ab: -[]{},#&*!|>'\"%@`\t\n\r\\?~0123.e+_é";
        for (int i = 0; i < 3_000; i++)
        {
            var sb = new StringBuilder();
            int length = random.Next(0, 60);
            for (int j = 0; j < length; j++)
            {
                sb.Append(alphabet[random.Next(alphabet.Length)]);
            }

            string text = sb.ToString();
            Contract(() => Cs.YamlSerializer.Deserialize<object>(text));
            Contract(() => Cs.YamlSerializer.Deserialize<Plain>(text));
            Contract(() => Cs.YamlSerializer.Deserialize<List<Dictionary<string, int>>>(text));
            Contract(() => Cs.YamlSerializer.Deserialize<Point>(text));
        }
    }

    [Fact]
    public void Invalid_suite_inputs_are_refused_with_the_package_exception()
    {
        string? dir = KitData.Directory(KitData.SuiteVariable);
        if (dir is null)
        {
            Assert.Skip($"{KitData.SuiteVariable} is not set");
        }

        int checkedCases = 0;
        foreach (string caseDir in Directory.EnumerateDirectories(dir))
        {
            foreach (string folder in File.Exists(Path.Combine(caseDir, "in.yaml")) ? [caseDir] : Directory.EnumerateDirectories(caseDir))
            {
                if (!File.Exists(Path.Combine(folder, "error")) || !File.Exists(Path.Combine(folder, "in.yaml")))
                {
                    continue;
                }

                string text = File.ReadAllText(Path.Combine(folder, "in.yaml"));
                try
                {
                    _ = Cs.YamlSerializer.Deserialize<object>(text);
                }
                catch (Cs.YamlException)
                {
                }
                catch (Exception e)
                {
                    Assert.Fail($"{folder}: {e.GetType()}: {e.Message}");
                }

                checkedCases++;
            }
        }

        Assert.True(checkedCases > 50, "the invalid cases were found");
    }

    // Every input of the suite (valid and invalid) and of the safety cases goes through every way in, with several target
    // types. Only the package exception may come out.
    [Fact]
    public void Suite_and_safety_inputs_end_in_the_package_exception_through_every_entry_point()
    {
        var inputs = new List<byte[]>();
        string? dir = KitData.Directory(KitData.SuiteVariable);
        if (dir is not null)
        {
            foreach (string file in Directory.EnumerateFiles(dir, "in.yaml", SearchOption.AllDirectories))
            {
                inputs.Add(File.ReadAllBytes(file));
            }
        }

        string? kit = KitData.Directory(KitData.KitVariable);
        if (kit is not null)
        {
            string safety = Path.Combine(kit, "suites", "own", "cases", "safety");
            if (!Directory.Exists(safety))
            {
                throw new InvalidOperationException($"{safety} does not exist");
            }

            foreach (string file in Directory.EnumerateFiles(safety, "input.yaml", SearchOption.AllDirectories))
            {
                inputs.Add(File.ReadAllBytes(file));
            }
        }

        if (inputs.Count == 0)
        {
            Assert.Skip($"{KitData.SuiteVariable} and {KitData.KitVariable} are not set");
        }

        foreach (byte[] bytes in inputs)
        {
            string text = Encoding.UTF8.GetString(bytes);
            Contract(() => Cs.YamlSerializer.Deserialize<object>(text));
            Contract(() => Cs.YamlSerializer.Deserialize<Plain>(text));
            Contract(() => Cs.YamlSerializer.Deserialize<List<Point>>(text));
            Contract(() => Cs.YamlSerializer.Deserialize<Dictionary<string, List<int>>>(text));
            Contract(() => Cs.YamlSerializer.Deserialize<string>(text));
            Contract(() => Cs.YamlSerializer.Deserialize<int>(text));
            Contract(() => Cs.YamlSerializer.Deserialize<Plain>(text.AsSpan()));
            Contract(() => Cs.YamlSerializer.Deserialize(text, ListType));
            Contract(() => Cs.YamlSerializer.Deserialize<Plain>(new StringReader(text)));
            Contract(() => Cs.YamlSerializer.Deserialize<object>(new MemoryStream(bytes)));
            Contract(() => Cs.YamlSerializer.Deserialize<Plain>(new MemoryStream(bytes)));
            Contract(() => Cs.YamlSerializer.TryDeserialize<List<Point>>(text, out _));
            Contract(() => Cs.YamlSerializer.TryDeserialize<Plain>(new MemoryStream(bytes), out _));
        }
    }

    [Fact]
    public void Concurrent_use_of_one_options_object_is_safe()
    {
        var options = new Cs.YamlSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };
        Plain sample = SyHelp.Sample();
        string expected = Cs.YamlSerializer.Serialize(sample, options);
        Parallel.For(0, 64, _ =>
        {
            Assert.Equal(expected, Cs.YamlSerializer.Serialize(sample, options));
            Assert.Equal(Differ.Dump(sample), Differ.Dump(Cs.YamlSerializer.Deserialize<Plain>(expected, options)));
        });

        // Options made fresh each time are cheap enough to use and give the same result.
        Parallel.For(0, 16, _ => Assert.Equal(expected, Cs.YamlSerializer.Serialize(sample, new Cs.YamlSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase })));
    }
}
