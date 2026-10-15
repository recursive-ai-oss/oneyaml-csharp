using System.Diagnostics.Tracing;
using System.Text;
using OneYaml.Documents;
using OneYaml.Parsing;
using OneYaml.Text;
using Xunit;

namespace OneYaml.Tests;

/// <summary>
/// What the library takes from the shared array pool while it works, counted per thread. A pooled buffer is
/// working memory even when the pool already holds it, so these tests keep small inputs on the stack and
/// keep large buffers no bigger than the input needs.
/// </summary>
public class WorkingMemoryTests
{
    /// <summary>The lengths of the buffers this thread rents from the shared array pools while the counter lives.</summary>
    private sealed class PoolRents : EventListener
    {
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly List<int> _sizes = [];

        [ThreadStatic]
        private static bool s_inside;

        public PoolRents()
        {
            foreach (EventSource source in EventSource.GetSources())
            {
                Enable(source);
            }
        }

        public IReadOnlyList<int> Sizes
        {
            get
            {
                lock (_sizes)
                {
                    return [.. _sizes];
                }
            }
        }

        protected override void OnEventSourceCreated(EventSource eventSource) => Enable(eventSource);

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventName != "BufferRented" || Environment.CurrentManagedThreadId != _thread || s_inside)
            {
                return;
            }

            s_inside = true;
            try
            {
                lock (_sizes)
                {
                    _sizes.Add((int)eventData.Payload![1]!);
                }
            }
            finally
            {
                s_inside = false;
            }
        }

        private void Enable(EventSource source)
        {
            if (source.Name == "System.Buffers.ArrayPoolEventSource")
            {
                EnableEvents(source, EventLevel.Verbose, EventKeywords.All);
            }
        }
    }

    [Fact]
    public void A_small_generic_load_takes_nothing_from_the_pool()
    {
        byte[] yaml = Encoding.UTF8.GetBytes("name: demo\nitems: [1, 2, 3]\nnested:\n  a: true\n  b: [x, {y: z}]\n");
        YamlSerializer.Deserialize<object>(yaml);
        using var rents = new PoolRents();
        object? value = YamlSerializer.Deserialize<object>(yaml);
        Assert.NotNull(value);
        Assert.Empty(rents.Sizes);
    }

    [Fact]
    public void A_generic_load_deeper_than_the_stack_space_still_reads_every_level()
    {
        const int Depth = 200;
        string yaml = string.Concat(Enumerable.Repeat("[", Depth)) + "x" + string.Concat(Enumerable.Repeat("]", Depth));
        object? value = YamlSerializer.Deserialize<object>(yaml);
        int seen = 0;
        while (value is List<object?> list)
        {
            value = Assert.Single(list);
            seen++;
        }

        Assert.Equal(Depth, seen);
        Assert.Equal("x", value);
    }

    [Fact]
    public void A_small_dump_rents_one_small_buffer()
    {
        var tree = new Dictionary<string, object?> { ["name"] = "demo", ["items"] = new List<object?> { 1L, 2L, 3L } };
        // The writer sizes its first buffer from the last output on the thread, so one call sets the stage.
        YamlSerializer.SerializeToUtf8Bytes(tree);
        using var rents = new PoolRents();
        YamlSerializer.SerializeToUtf8Bytes(tree);
        Assert.True(rents.Sizes.Sum() <= 512, "rented " + string.Join(", ", rents.Sizes));
    }

    [Fact]
    public void A_long_string_is_transcoded_in_a_buffer_the_size_of_its_bytes()
    {
        string text = new('a', 40_000);
        var tree = new Dictionary<string, object?> { ["text"] = text };
        YamlSerializer.SerializeToUtf8Bytes(tree);
        using var rents = new PoolRents();
        YamlSerializer.SerializeToUtf8Bytes(tree);
        // The worst case of three bytes a character would round up to 131,072.
        Assert.True(rents.Sizes.Max() <= 65_536, "rented " + string.Join(", ", rents.Sizes));
    }

    [Fact]
    public void A_document_of_comments_does_not_reserve_node_storage_for_its_size()
    {
        var text = new StringBuilder("a: 1\n");
        for (int i = 0; i < 2_000; i++)
        {
            text.Append("# a comment line that says nothing at all\n");
        }

        byte[] bytes = Encoding.UTF8.GetBytes(text.ToString());
        using DocState state = DocState.Build(bytes, YamlEncoding.Utf8, false, YamlLimits.Default, false);
        Assert.Equal(3, state.NodeCount);
        Assert.True(state.NodeCapacity <= 4_096, "the array holds " + state.NodeCapacity + " nodes");
    }

    [Fact]
    public void A_document_of_dense_flow_entries_is_counted_without_a_second_node_array()
    {
        // Every comma is an entry, so the first guess has to count them or the array is copied while it grows.
        string yaml = "[" + string.Join(", ", Enumerable.Repeat("a", 5_000)) + "]\n";
        byte[] bytes = Encoding.UTF8.GetBytes(yaml);
        using var rents = new PoolRents();
        using DocState state = DocState.Build(bytes, YamlEncoding.Utf8, false, YamlLimits.Default, false);
        Assert.Equal(5_001, state.NodeCount);
        // The text and the nodes; a short first guess would add a smaller array that is then replaced.
        Assert.True(rents.Sizes.Count(s => s >= 4_096) == 2, string.Join(", ", rents.Sizes));
    }

    [Fact]
    public void The_decoded_length_bound_holds_for_every_style()
    {
        var random = new Random(20261015);
        string[] pieces = ["a", " ", "  ", "\n", "\n\n", "\\L", "\\P", "\\n", "\\u00e9", "\\x41", "é", "\u2028", "''", "\\\n", "b c", "\t", "\r\n"];
        for (int round = 0; round < 3_000; round++)
        {
            var body = new StringBuilder();
            int count = random.Next(1, 40);
            for (int i = 0; i < count; i++)
            {
                body.Append(pieces[random.Next(pieces.Length)]);
            }

            string text = body.ToString();
            foreach (string document in new[]
            {
                "k: \"" + text.Replace("\"", "'") + "\"\n",
                "k: '" + text.Replace("\\", "/").Replace("'", "''") + "'\n",
                "k: |\n  " + text.Replace("\n", "\n  ").Replace("\\", "/") + "\n",
                "k: >-\n  " + text.Replace("\n", "\n  ").Replace("\\", "/") + "\n",
                "k: " + text.Replace("\\", "/").Replace("\n", "\n  ").Replace("'", "").Replace("\"", "") + "\n",
            })
            {
                byte[] bytes = Encoding.UTF8.GetBytes(document);
                YamlReader reader;
                try
                {
                    reader = new YamlReader(bytes);
                }
                catch (YamlException)
                {
                    continue;
                }

                try
                {
                    while (true)
                    {
                        bool more;
                        try
                        {
                            more = reader.Read();
                        }
                        catch (YamlException)
                        {
                            break;
                        }

                        if (!more)
                        {
                            break;
                        }

                        if (reader.TokenType != YamlTokenType.Scalar || !reader.ValueIsEscapedOrFolded)
                        {
                            continue;
                        }

                        int bound = reader.MaxDecodedLength;
                        // A destination of exactly the bound: any write past it throws.
                        byte[] exact = new byte[bound];
                        int written = reader.DecodeScalar(exact);
                        Assert.True(written <= bound, document);
                        Assert.True(bound <= reader.RawScalar.Length + 4 + reader.RawScalar.Count((byte)'\\'), document);
                    }
                }
                finally
                {
                    reader.Dispose();
                }
            }
        }
    }

    [Fact]
    public async Task Output_longer_than_one_buffer_segment_is_the_same_through_every_writer()
    {
        // Several hundred kilobytes of output, with characters of every UTF-8 length, so a segment boundary
        // falls somewhere new for each of the writers.
        var tree = new Dictionary<string, object?>();
        for (int i = 0; i < 6_000; i++)
        {
            tree["key" + i] = "é中🙂 value " + i + new string('x', i % 17);
        }

        byte[] bytes = YamlSerializer.SerializeToUtf8Bytes(tree);
        Assert.True(bytes.Length > 3 * 65_536);
        Assert.Equal(Encoding.UTF8.GetString(bytes), YamlSerializer.Serialize(tree));

        using var viaStream = new MemoryStream();
        YamlSerializer.Serialize(viaStream, tree);
        Assert.Equal(bytes, viaStream.ToArray());

        using var viaAsync = new MemoryStream();
        await YamlSerializer.SerializeAsync(viaAsync, tree, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(bytes, viaAsync.ToArray());

        var back = Assert.IsType<Dictionary<string, object?>>(YamlSerializer.Deserialize<object>(bytes));
        Assert.Equal(tree.Count, back.Count);
        foreach ((string key, object? value) in tree)
        {
            Assert.Equal(value, back[key]);
        }
    }

    [Fact]
    public void A_long_dump_does_not_rent_one_buffer_for_all_of_it()
    {
        string text = new('a', 200_000);
        var tree = new Dictionary<string, object?> { ["a"] = text, ["b"] = text };
        YamlSerializer.SerializeToUtf8Bytes(tree);
        using var rents = new PoolRents();
        YamlSerializer.SerializeToUtf8Bytes(tree);
        // A single array for 400,000 bytes would be 524,288 long.
        Assert.True(rents.Sizes.Max() <= 262_144, "rented " + string.Join(", ", rents.Sizes));
    }
}
