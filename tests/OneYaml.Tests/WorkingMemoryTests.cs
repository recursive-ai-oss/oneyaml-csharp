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

    [Theory]
    [InlineData("flow")]
    [InlineData("block")]
    public void A_deep_generic_load_rents_each_level_once(string shape)
    {
        const int Depth = 100;
        string yaml = shape == "flow"
            ? string.Concat(Enumerable.Repeat("[", Depth)) + string.Concat(Enumerable.Repeat("]", Depth)) + "\n"
            : string.Concat(Enumerable.Range(0, Depth).Select(i => new string(' ', i) + "k:\n")) + new string(' ', Depth) + "v\n";
        byte[] bytes = Encoding.UTF8.GetBytes(yaml);
        YamlSerializer.Deserialize<object>(bytes);
        using var rents = new PoolRents();
        Assert.NotNull(YamlSerializer.Deserialize<object>(bytes));

        // Growing by doubling rents every size on the way (32, 64 and 128 for 100 levels), which is nearly twice
        // the room the deepest level needs. The frames of the load go into chunks that are never copied, and a run
        // of closing tokens takes one queue slot, so the total stays below what two more doubled arrays would add.
        Assert.True(rents.Sizes.Sum() <= (shape == "flow" ? 1_100 : 600), "rented " + string.Join(", ", rents.Sizes));
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
    public void Line_breaks_colons_and_commas_inside_a_scalar_do_not_reserve_node_storage()
    {
        // A block of comma-separated numbers: a megabyte of line breaks and commas, and three nodes.
        var csv = new StringBuilder("data: |\n");
        while (csv.Length < 1_000_000)
        {
            csv.Append("  1,2,3,4,5,6,7,8\n");
        }

        byte[] bytes = Encoding.UTF8.GetBytes(csv.ToString());
        using (var rents = new PoolRents())
        {
            using DocState state = DocState.Build(bytes, YamlEncoding.Utf8, false, YamlLimits.Default, false);
            Assert.Equal(3, state.NodeCount);
            Assert.True(state.NodeCapacity <= 4_096, "the array holds " + state.NodeCapacity + " nodes");
            // Next to the text, the first node array follows the lines (about 60,000) and not the commas, which would
            // have reserved the 262,144 that a sixth of the text size rounds up to.
            Assert.True(rents.Sizes.Where(size => size < 1 << 20).All(size => size <= 65_536), string.Join(", ", rents.Sizes));
        }

        // Minified JSON in a quoted scalar has flow punctuation, so the guess counts it, but the array is trimmed.
        string json = "k: '[" + string.Join(",", Enumerable.Range(0, 100_000).Select(i => "{\"a\":" + i + "}")).Replace("'", "''") + "]'\n";
        using DocState quoted = DocState.Build(Encoding.UTF8.GetBytes(json), YamlEncoding.Utf8, false, YamlLimits.Default, false);
        Assert.Equal(3, quoted.NodeCount);
        Assert.True(quoted.NodeCapacity <= 4_096, "the array holds " + quoted.NodeCapacity + " nodes");
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

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(16)]
    [InlineData(100)]
    [InlineData(1_000)]
    public void A_run_of_separator_escapes_fits_the_decoded_length_bound_exactly_sized(int pairs)
    {
        // \L and \P are two bytes in the text and three in the value, the only escapes that grow, so a run of
        // them is where a bound that forgets the escapes runs out first.
        string document = "k: \"" + string.Concat(Enumerable.Repeat("\\L\\P", pairs)) + "\"\n";
        using var reader = new YamlReader(Encoding.UTF8.GetBytes(document));
        while (reader.Read() && !(reader.TokenType == YamlTokenType.Scalar && reader.ValueIsEscapedOrFolded))
        {
        }

        int bound = reader.MaxDecodedLength;
        byte[] exact = new byte[bound];
        int written = reader.DecodeScalar(exact);
        Assert.Equal(pairs * 6, written);
        Assert.True(bound >= written, "the bound " + bound + " is below the " + written + " bytes of the value");
        Assert.Equal(string.Concat(Enumerable.Repeat("\u2028\u2029", pairs)), Encoding.UTF8.GetString(exact, 0, written));
    }

    // Writes the prefix so that the first segment has room for exactly `inFirst` more bytes, then the character's bytes
    // across the segment boundary.
    private static PooledBufferWriter SplitAt(byte[] character, int inFirst)
    {
        var writer = new PooledBufferWriter();
        int room = writer.GetSpan(1).Length;
        writer.GetSpan(1)[..(room - inFirst)].Fill((byte)'a');
        writer.Advance(room - inFirst);
        character.AsSpan(0, inFirst).CopyTo(writer.GetSpan(1));
        writer.Advance(inFirst);
        int rest = character.Length - inFirst;
        character.AsSpan(inFirst).CopyTo(writer.GetSpan(rest));
        writer.Advance(rest);
        return writer;
    }

    [Theory]
    [InlineData("\u00e9")]
    [InlineData("\u4e2d")]
    [InlineData("\U0001F642")]
    public void A_character_split_between_segments_is_decoded_whole_at_every_offset(string character)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(character);
        for (int inFirst = 1; inFirst < bytes.Length; inFirst++)
        {
            using PooledBufferWriter writer = SplitAt(bytes, inFirst);
            string expected = Encoding.UTF8.GetString(writer.ToArray());
            Assert.Equal(expected, writer.ToUtf8String());
            Assert.EndsWith(character, expected, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Bytes_written_in_random_pieces_decode_as_the_whole_text_does_even_when_they_are_not_valid_text()
    {
        var random = new Random(20261015);
        byte[][] pieces =
        [
            "a"u8.ToArray(), "\u00e9"u8.ToArray(), "\u4e2d"u8.ToArray(), "\U0001F642"u8.ToArray(),
            [0xE4, 0xB8], [0x80], [0xF0, 0x9F], [0xC3], [0xFF], [0xE0, 0x80, 0xA0], [0xF0, 0x9F, 0x99, 0xF0],
        ];
        for (int round = 0; round < 300; round++)
        {
            using var writer = new PooledBufferWriter();
            int count = random.Next(1, 700);
            for (int i = 0; i < count; i++)
            {
                byte[] piece = pieces[random.Next(pieces.Length)];
                // Pieces are written in two parts at random so that segment ends fall inside characters.
                int firstPart = random.Next(piece.Length + 1);
                piece.AsSpan(0, firstPart).CopyTo(writer.GetSpan(Math.Max(firstPart, 1)));
                writer.Advance(firstPart);
                piece.AsSpan(firstPart).CopyTo(writer.GetSpan(Math.Max(piece.Length - firstPart, 1)));
                writer.Advance(piece.Length - firstPart);
            }

            Assert.Equal(Encoding.UTF8.GetString(writer.ToArray()), writer.ToUtf8String());
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
