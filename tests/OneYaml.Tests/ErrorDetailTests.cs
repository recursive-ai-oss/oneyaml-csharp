using System.Collections.Immutable;
using System.Text;
using OneYaml.Tests.Generated;
using Xunit;

// The tests pass types as values on purpose, and name the overloads without a type argument.
#pragma warning disable CA2263

namespace OneYaml.Tests;

// What an error says about the input: bounded text, no forged lines, no phantom keys, and no cost that grows
// with the size of the document when the error is going to be thrown away.
public class ErrorDetailTests
{
    [Fact]
    public void A_huge_key_in_the_path_is_cut_in_the_message_and_whole_in_the_path()
    {
        string key = new('k', 1 << 20);
        YamlException e = Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<Dictionary<string, int>>("? " + key + "\n: notanumber\n"));
        Assert.Equal("/" + key, e.Path);
        Assert.True(e.Message.Length < 600, $"the message is {e.Message.Length} characters");
        Assert.Contains("\u2026", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_key_with_line_breaks_and_escapes_cannot_forge_log_lines()
    {
        const string yaml = "\"a\\nERROR forged\\r\\u001b[31m\\u2028x\": notanumber\n";
        YamlException e = Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<Dictionary<string, int>>(yaml));
        Assert.Contains("\n", e.Path, StringComparison.Ordinal);
        foreach (char c in e.Message)
        {
            Assert.False(char.IsControl(c) || c is '\u2028' or '\u2029', $"the message holds U+{(int)c:X4}");
        }

        Assert.Contains("\\n", e.Message, StringComparison.Ordinal);
        Assert.Contains("\\u001B", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_duplicate_huge_key_is_cut_and_escaped_in_the_message()
    {
        string key = "\"" + new string('k', 5000) + "\\n\"";
        YamlException e = Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<Dictionary<string, int>>("? " + key + "\n: 1\n? " + key + "\n: 2\n"));
        Assert.Equal(YamlErrorCategory.DuplicateKey, e.Category);
        Assert.True(e.Message.Length < 600, $"the message is {e.Message.Length} characters");
        Assert.DoesNotContain('\n', e.Message);
    }

    [Fact]
    public void An_enum_value_in_a_message_is_cut_and_escaped()
    {
        string yaml = "\"" + new string('v', 5000) + "\\n\"\n";
        YamlException e = Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<DayOfWeek>(yaml));
        Assert.True(e.Message.Length < 600, $"the message is {e.Message.Length} characters");
        Assert.DoesNotContain('\n', e.Message);
    }

    [Fact]
    public void A_complex_key_gives_no_path_and_an_empty_string_key_gives_a_slash()
    {
        byte[] complex = Encoding.UTF8.GetBytes("? [a, b]\n: value\n");
        Assert.Null(ErrorPath.Find(complex, Array.IndexOf(complex, (byte)'v'), atCollection: false));

        byte[] empty = Encoding.UTF8.GetBytes("\"\": value\n");
        Assert.Equal("/", ErrorPath.Find(empty, Array.IndexOf(empty, (byte)'v'), atCollection: false));

        byte[] alias = Encoding.UTF8.GetBytes("&k a\n---\n? *k\n: value\n");
        Assert.NotEqual("/", ErrorPath.Find(alias, alias.Length - 6, atCollection: false));
    }

    [Theory]
    [InlineData(typeof(ImmutableArray<int>), "ImmutableArray<int>")]
    [InlineData(typeof(ImmutableQueue<string>), "ImmutableQueue<string>")]
    [InlineData(typeof(Dictionary<string, List<long>>), "Dictionary<string, List<long>>")]
    [InlineData(typeof(int?[]), "int?[]")]
    [InlineData(typeof(DayOfWeek), "DayOfWeek")]
    public void A_type_in_a_message_is_written_the_way_the_language_writes_it(Type type, string expected) =>
        Assert.Equal(expected, ConverterGuards.DescribeType(type));

    [Fact]
    public void A_mismatched_immutable_target_names_the_type_without_a_backtick()
    {
        YamlException e = Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<ImmutableArray<int>>("a: 1\n"));
        Assert.Contains("ImmutableArray<int>", e.Message, StringComparison.Ordinal);
        Assert.DoesNotContain('`', e.Message);
    }

    [Fact]
    public void A_stream_over_the_size_limit_carries_the_source_name()
    {
        var options = new YamlSerializerOptions { SourceName = "big.yaml", Limits = YamlLimits.Default with { MaxInputBytes = 8 } };
        byte[] bytes = Encoding.UTF8.GetBytes(new string('a', 100));

        using var seekable = new MemoryStream(bytes);
        YamlException e = Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<string>(seekable, options));
        Assert.Equal(YamlErrorCategory.Limit, e.Category);
        Assert.Equal("big.yaml", e.SourceName);
        Assert.StartsWith("big.yaml: ", e.Message, StringComparison.Ordinal);

        using var forward = new ForwardOnly(bytes);
        e = Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<string>(forward, options));
        Assert.Equal("big.yaml", e.SourceName);
    }

    [Fact]
    public async Task An_async_stream_over_the_size_limit_carries_the_source_name()
    {
        var options = new YamlSerializerOptions { SourceName = "big.yaml", Limits = YamlLimits.Default with { MaxInputBytes = 8 } };
        using var forward = new ForwardOnly(Encoding.UTF8.GetBytes(new string('a', 100)));
        YamlException e = await Assert.ThrowsAsync<YamlException>(async () => await YamlSerializer.DeserializeAsync<string>(forward, options, TestContext.Current.CancellationToken));
        Assert.Equal("big.yaml", e.SourceName);
    }

    private sealed class ForwardOnly(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    // ---- Sorted targets ----

    [Fact]
    public void Keys_a_sorted_dictionary_treats_as_equal_are_a_duplicate_key_error_on_both_paths()
    {
        // U+00AD is ignorable under the culture comparer, so the keys differ as text and agree as sorted keys.
        const string yaml = "{a: 1, \"a\\u00AD\": 2}";
        var info = (YamlTypeInfo<ImmutableSortedDictionary<string, int>>)new SortedContext().GetTypeInfo(typeof(ImmutableSortedDictionary<string, int>))!;
        foreach (YamlTypeInfo<ImmutableSortedDictionary<string, int>>? path in new[] { null, info })
        {
            YamlException e = Assert.Throws<YamlException>(() => path is null
                ? YamlSerializer.Deserialize<ImmutableSortedDictionary<string, int>>(yaml)
                : YamlSerializer.Deserialize(yaml, path));
            Assert.Equal(YamlErrorCategory.DuplicateKey, e.Category);
            Assert.Equal(1, e.Line);
        }

        Assert.Equal(2, YamlSerializer.Deserialize<ImmutableSortedDictionary<string, int>>("{a: 1, b: 2}")!.Count);
    }

    public sealed class Row
    {
        public int A { get; set; }
    }

    [Fact]
    public void A_sorted_collection_of_a_type_that_cannot_be_compared_is_refused_when_the_metadata_is_made()
    {
        Assert.Throws<NotSupportedException>(() => YamlSerializer.Deserialize<SortedSet<Row>>("[{a: 1}, {a: 2}]"));
        Assert.Throws<NotSupportedException>(() => YamlSerializer.Deserialize<ImmutableSortedSet<Row>>("[{a: 1}, {a: 2}]"));
        Assert.Throws<NotSupportedException>(() => YamlSerializer.Deserialize<SortedDictionary<Row, int>>("{}"));
        Assert.Throws<NotSupportedException>(() => YamlSerializer.Deserialize<ImmutableSortedDictionary<Row, int>>("{}"));
        Assert.Equal([1, 2], YamlSerializer.Deserialize<SortedSet<int>>("[2, 1]")!);
    }

    // ---- Cost of errors that are caught ----

    private static long ThrowAwayCost(int items)
    {
        byte[] yaml = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, items).Select(i => $"- v{i}\n")));
        YamlSerializerOptions options = new();
        options.MakeReadOnly();
        var reader = new Utf8YamlReader(yaml, options, expandAliases: false) { QuietErrors = true };
        try
        {
            reader.BeginDocument(synthesizeEmptyStream: true);
            reader.Read();
            while (reader.Read() && reader.Type == OneYaml.Parsing.YamlTokenType.Scalar)
            {
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 200; i++)
            {
                YamlException e = reader.ErrorAt(YamlErrorCategory.Binding, "x", yaml.Length - 4);
                Assert.Null(e.Path);
                Assert.Equal(0, e.Line);
            }

            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
        finally
        {
            reader.Dispose();
        }
    }

    [Fact]
    public void An_error_built_to_be_caught_costs_the_same_wherever_in_the_text_it_happens()
    {
        ThrowAwayCost(100);
        long small = ThrowAwayCost(1_000);
        long large = ThrowAwayCost(40_000);
        Assert.True(large < small * 2 + 50_000, $"{small:N0} bytes for 1,000 items, {large:N0} for 40,000");
    }

    [Fact]
    public void A_located_error_still_has_its_line_column_and_path()
    {
        YamlException e = Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<List<int>>("- 1\n- x\n"));
        Assert.Equal(2, e.Line);
        Assert.Equal("/1", e.Path);
    }
}

[YamlSerializable(typeof(ImmutableSortedDictionary<string, int>))]
internal sealed partial class SortedContext : YamlSerializerContext
{
}
