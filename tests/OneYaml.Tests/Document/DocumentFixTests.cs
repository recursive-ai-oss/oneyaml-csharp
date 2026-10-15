using System.Numerics;
using System.Text;
using Xunit;

namespace OneYaml.Tests.Document;

/// <summary>Regressions of the document and edit engine, each named for what went wrong.</summary>
public class DocumentFixTests
{
    private static string Edit(string yaml, Action<YamlDocument> edit)
    {
        using YamlDocument doc = YamlDocument.Parse(yaml);
        edit(doc);
        string result = doc.ToString();

        // The output must read back: a structure the edit made invalid fails here.
        using YamlDocument again = YamlDocument.Parse(result);
        return result;
    }

    // ---- A comment between a key (or dash) and a value on a later line stays ----

    [Fact]
    public void Replacing_a_block_collection_with_a_scalar_keeps_the_comment_after_the_key()
    {
        Assert.Equal("a: # why\n  true\nc: 2\n", Edit("a: # why\n  b: 1\nc: 2\n", d => d.Set("/a", true)));
    }

    [Fact]
    public void Replacing_a_block_collection_with_another_keeps_the_comment_after_the_key()
    {
        Assert.Equal("a: # why\n  z: 1\nc: 2\n", Edit("a: # why\n  b: 1\nc: 2\n", d => d.Set("/a", new Dictionary<string, object?> { ["z"] = 1 })));
    }

    [Fact]
    public void Replacing_a_block_collection_under_a_dash_keeps_the_comment_after_the_dash()
    {
        Assert.Equal("- # why\n  3\n- x\n", Edit("- # why\n  b: 1\n- x\n", d => d.Set("/0", 3)));
    }

    [Fact]
    public void Replacing_a_collection_without_a_comment_still_joins_the_key_line()
    {
        Assert.Equal("a: 3\nc: 2\n", Edit("a:\n  b: 1\nc: 2\n", d => d.Set("/a", 3)));
    }

    // ---- Removing an item of a multi-line flow collection ----

    [Fact]
    public void Removing_a_flow_item_removes_its_own_comment_and_keeps_the_previous_one()
    {
        Assert.Equal("s: [\n  a, # one\n  ]\n", Edit("s: [\n  a, # one\n  b # two\n  ]\n", d => d.Remove("/s/1")));
    }

    [Fact]
    public void Removing_the_first_flow_item_keeps_the_second_one_and_its_comment()
    {
        Assert.Equal("s: [\n  b # two\n  ]\n", Edit("s: [\n  a, # one\n  b # two\n  ]\n", d => d.Remove("/s/0")));
    }

    [Fact]
    public void Removing_a_flow_item_that_shares_its_line_removes_the_comma_before_it()
    {
        Assert.Equal("s: [a, c]\n", Edit("s: [a, b, c]\n", d => d.Remove("/s/1")));
    }

    // ---- Wide encodings without a mark ----

    [Fact]
    public void A_wide_input_without_a_mark_must_begin_with_an_ascii_character()
    {
        foreach (Encoding enc in new Encoding[] { new UnicodeEncoding(false, false), new UnicodeEncoding(true, false), new UTF32Encoding(false, false), new UTF32Encoding(true, false) })
        {
            byte[] bytes = enc.GetBytes("é: 1\n");
            YamlException ex = Assert.Throws<YamlException>(() => YamlDocument.Parse(bytes));
            Assert.Equal(YamlErrorCategory.Encoding, ex.Category);
            Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<object>(bytes));
        }
    }

    [Fact]
    public void A_wide_input_without_a_mark_that_begins_with_ascii_is_written_back_unchanged()
    {
        byte[] bytes = new UnicodeEncoding(false, false).GetBytes("a: é\n");
        using YamlDocument doc = YamlDocument.Parse(bytes);
        Assert.Equal(bytes, doc.ToBytes());
    }

    [Fact]
    public void An_edit_that_makes_the_first_character_non_ascii_adds_the_mark()
    {
        byte[] bytes = new UnicodeEncoding(false, false).GetBytes("a\n");
        using YamlDocument doc = YamlDocument.Parse(bytes);
        doc.Set("", "é");
        byte[] result = doc.ToBytes();
        Assert.Equal(new byte[] { 0xFF, 0xFE }, result[..2]);
        using YamlDocument again = YamlDocument.Parse(result);
        Assert.Equal("é", again.Root.GetValue<string>());
    }

    // ---- Duplicate keys ----

    [Fact]
    public void Parse_refuses_a_duplicate_key_unless_allowed()
    {
        YamlException ex = Assert.Throws<YamlException>(() => YamlDocument.Parse("a: 1\na: 2\n"));
        Assert.Equal(YamlErrorCategory.DuplicateKey, ex.Category);
        Assert.Equal(2, ex.Line);

        using YamlDocument doc = YamlDocument.Parse("a: 1\na: 2\n", new YamlDocumentOptions { AllowDuplicateKeys = true });
        Assert.Equal("a: 1\na: 2\n", doc.ToString());
    }

    // ---- Scalars read with the binder's converters ----

    [Fact]
    public void A_decimal_is_read_without_rounding_through_a_double()
    {
        using YamlDocument doc = YamlDocument.Parse("v: 3.14159265358979323846\n");
        Assert.Equal(3.14159265358979323846m, doc.Get<decimal>("/v"));
    }

    [Fact]
    public void Scalars_read_as_the_types_the_binder_reads()
    {
        using YamlDocument doc = YamlDocument.Parse("c: x\nn: 7\nz: ~\nd: 2024-01-02\ng: 3f2504e0-4f89-11d3-9a0c-0305e82c3301\nb: 18446744073709551615\n");
        Assert.Equal('x', doc.Get<char>("/c"));
        Assert.Equal((short)7, doc.Get<short?>("/n"));
        Assert.Null(doc.Get<short?>("/z"));
        Assert.Equal(new DateOnly(2024, 1, 2), doc.Get<DateOnly>("/d"));
        Assert.Equal(Guid.Parse("3f2504e0-4f89-11d3-9a0c-0305e82c3301"), doc.Get<Guid>("/g"));
        Assert.Equal(ulong.MaxValue, doc.Get<ulong>("/b"));
        Assert.False(doc.TryGet("/c", out short _));
        YamlException ex = Assert.Throws<YamlException>(() => doc.Get<byte>("/b"));
        Assert.Equal(YamlErrorCategory.Binding, ex.Category);
    }

    [Fact]
    public void An_integer_with_too_many_digits_is_a_limit_error_for_a_read()
    {
        string digits = new('9', 5000);
        using YamlDocument doc = YamlDocument.Parse("v: " + digits + "\n");
        YamlException ex = Assert.Throws<YamlException>(() => doc.Get<BigInteger>("/v"));
        Assert.Equal(YamlErrorCategory.Limit, ex.Category);
    }

    // ---- Reading a stream ----

    private sealed class EndlessStream : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Fill(buffer, (byte)'a', offset, count);
            BytesRead += count;
            return count;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void A_stream_past_the_input_limit_stops_the_read()
    {
        var stream = new EndlessStream();
        var options = new YamlDocumentOptions { Limits = YamlLimits.Default with { MaxInputBytes = 100_000 } };
        YamlException ex = Assert.Throws<YamlException>(() => YamlDocument.Parse(stream, options));
        Assert.Equal(YamlErrorCategory.Limit, ex.Category);
        Assert.True(stream.BytesRead < 200_000, $"read {stream.BytesRead} bytes");
    }

    [Fact]
    public async Task A_stream_past_the_input_limit_stops_the_async_read()
    {
        var stream = new EndlessStream();
        var options = new YamlDocumentOptions { Limits = YamlLimits.Default with { MaxInputBytes = 100_000 } };
        YamlException ex = await Assert.ThrowsAsync<YamlException>(() => YamlDocument.LoadAsync(stream, options, TestContext.Current.CancellationToken));
        Assert.Equal(YamlErrorCategory.Limit, ex.Category);
        Assert.True(stream.BytesRead < 200_000, $"read {stream.BytesRead} bytes");
    }

    // ---- Walking aliases ----

    private static string Bomb(int levels, int fan)
    {
        var sb = new StringBuilder();
        sb.Append("v0: &a0 [").Append(string.Join(", ", Enumerable.Repeat("x", fan))).Append("]\n");
        for (int i = 1; i < levels; i++)
        {
            sb.Append('v').Append(i).Append(": &a").Append(i).Append(" [")
              .Append(string.Join(", ", Enumerable.Repeat("*a" + (i - 1), fan))).Append("]\n");
        }

        return sb.ToString();
    }

    [Fact]
    public void Walking_an_alias_bomb_through_nodes_ends_in_a_limit_error()
    {
        using YamlDocument doc = YamlDocument.Parse(Bomb(9, 10));
        var stack = new Stack<YamlNode>();
        stack.Push(doc.Root);
        long visited = 0;
        YamlException? limit = null;
        try
        {
            while (stack.Count > 0 && visited < 3_000_000)
            {
                YamlNode n = stack.Pop().Target;
                visited++;
                if (n.Kind is YamlNodeKind.Sequence or YamlNodeKind.Mapping)
                {
                    foreach (YamlNode child in n.Items)
                    {
                        stack.Push(child);
                    }
                }
            }
        }
        catch (YamlException ex)
        {
            limit = ex;
        }

        Assert.NotNull(limit);
        Assert.Equal(YamlErrorCategory.Limit, limit.Category);
        Assert.True(visited < 3_000_000);
    }

    // ---- Tags that use a redefined handle ----

    [Fact]
    public void A_redefined_secondary_handle_no_longer_names_the_core_tags()
    {
        using YamlDocument doc = YamlDocument.Parse("%TAG !! tag:example.com,2000:app/\n---\n!!int 1 - 3\n");
        Assert.Equal("1 - 3", doc.Root.GetValue<string>());
        Assert.Equal("1 - 3", doc.Root.Deserialize<string>());
    }

    [Fact]
    public void A_custom_handle_for_the_core_prefix_names_the_core_tags()
    {
        using YamlDocument doc = YamlDocument.Parse("%TAG !e! tag:yaml.org,2002:\n---\n- !e!int 5\n- !e!str 5\n");
        Assert.Equal(5L, doc.Get<long>("/0"));
        Assert.Equal("5", doc.Get<string>("/1"));
    }

    // ---- A stream without documents ----

    [Theory]
    [InlineData("")]
    [InlineData("# only a comment\n")]
    [InlineData("\n\n")]
    public void A_stream_without_a_document_has_none_and_a_root_that_reads_as_null(string yaml)
    {
        using YamlDocument doc = YamlDocument.Parse(yaml);
        Assert.Empty(doc.Documents);
        Assert.True(doc.Root.IsNull);
        Assert.Equal(yaml, doc.ToString());
    }

    [Fact]
    public void Writing_to_the_root_of_an_empty_stream_makes_the_document()
    {
        using YamlDocument doc = YamlDocument.Parse(string.Empty);
        doc.Root["a"] = 1;
        Assert.Equal("a: 1\n", doc.ToString());
        Assert.Single(doc.Documents);

        using YamlDocument second = YamlDocument.Parse("# note\n");
        second.Root["a"] = 1;
        Assert.Equal("# note\na: 1\n", second.ToString());
    }

    // ---- Keys too long to be implicit ----

    [Fact]
    public void A_flow_mapping_gets_a_long_key_in_the_explicit_form()
    {
        string key = new('k', 1100);
        string result = Edit("{a: 1}\n", d => d.Set("/" + key, 2));
        Assert.Equal("{a: 1, ? " + key + " : 2}\n", result);
        using YamlDocument doc = YamlDocument.Parse(result);
        Assert.Equal(2L, doc.Get<long>("/" + key));
    }

    [Fact]
    public void A_block_mapping_gets_a_long_key_and_a_nested_value_in_the_explicit_form()
    {
        string key = new('k', 1100);
        string result = Edit("a: 1\n", d => d.Set("/" + key, new Dictionary<string, object?> { ["x"] = 1 }));
        Assert.Equal("a: 1\n? " + key + "\n:\n  x: 1\n", result);
    }

    // ---- The header of a block scalar ----

    [Fact]
    public void A_block_scalar_keeps_its_indentation_indicator_while_it_counts_the_right_columns()
    {
        Assert.Equal("a: |2\n  y\n", Edit("a: |2\n   x\n", d => d.Set("/a", "y\n")));
        Assert.Equal("a: |-\n  y\n", Edit("a: |-\n  x\n", d => d.Set("/a", "y")));
    }

    // ---- Integers in patches ----

    [Fact]
    public void A_patch_integer_with_too_many_digits_is_a_limit_error_and_changes_nothing()
    {
        using YamlDocument doc = YamlDocument.Parse("a: 1\n");
        string patch = "[{\"op\":\"add\",\"path\":\"/b\",\"value\":" + new string('9', 5000) + "}]";
        YamlException ex = Assert.Throws<YamlException>(() => doc.ApplyPatch(patch));
        Assert.Equal(YamlErrorCategory.Limit, ex.Category);
        Assert.Equal("a: 1\n", doc.ToString());

        ex = Assert.Throws<YamlException>(() => doc.ApplyMergePatch("{\"b\":" + new string('9', 5000) + "}"));
        Assert.Equal(YamlErrorCategory.Limit, ex.Category);
    }

    // ---- Smaller edits ----

    [Fact]
    public void Writing_to_an_empty_explicit_document_adds_no_blank_line()
    {
        Assert.Equal("---\na: 1\n...\n", Edit("---\n...\n", d => d.Root["a"] = 1));
        Assert.Equal("---\na: 1\n", Edit("---\n", d => d.Root["a"] = 1));
    }

    [Fact]
    public void An_item_added_to_a_multi_line_flow_sequence_goes_after_the_last_comment()
    {
        Assert.Equal("s: [\n  a, # one\n  b, # two\n  c\n  ]\n", Edit("s: [\n  a, # one\n  b # two\n  ]\n", d => d.Add("/s/-", "c")));
        Assert.Equal("s: [\n  a, # one\n  b, # two\n  c,\n  ]\n", Edit("s: [\n  a, # one\n  b, # two\n  ]\n", d => d.Add("/s/-", "c")));
    }

    [Fact]
    public void An_anchor_takes_effect_after_its_node_so_a_node_cannot_alias_itself()
    {
        YamlException ex = Assert.Throws<YamlException>(() => YamlDocument.Parse("a: &a [*a]\n"));
        Assert.Equal(YamlErrorCategory.Syntax, ex.Category);
    }

    [Fact]
    public void Copying_a_value_keeps_an_alias_whose_anchor_comes_before_the_destination()
    {
        Assert.Equal(
            "a: &x 1\nb:\n  k: *x\nc:\n  k: *x\n",
            Edit("a: &x 1\nb:\n  k: *x\n", d => d.ApplyPatch("[{\"op\":\"copy\",\"from\":\"/b\",\"path\":\"/c\"}]")));
    }

    // ---- Views ----

    [Fact]
    public void An_unset_enumerable_enumerates_nothing()
    {
        Assert.Empty(default(YamlNode.EntryEnumerable));
        Assert.Empty(default(YamlNode.ItemEnumerable));
    }

    // ---- A node binds as its text would ----

    [Fact]
    public void A_float_tag_on_an_integer_reads_as_a_float_through_a_node_and_the_serializer()
    {
        using YamlDocument doc = YamlDocument.Parse("v: !!float 1\n");
        object? viaNode = doc.Root["v"].Deserialize<object>();
        object? viaSerializer = YamlSerializer.Deserialize<Dictionary<string, object?>>("v: !!float 1\n")!["v"];
        Assert.IsType<double>(viaNode);
        Assert.Equal(viaSerializer, viaNode);
    }

    [Fact]
    public void An_empty_key_reads_as_the_empty_text_the_serializer_reads()
    {
        const string Yaml = "? \n: 1\n";
        using YamlDocument doc = YamlDocument.Parse(Yaml);
        Dictionary<string, object?> viaNode = doc.Root.Deserialize<Dictionary<string, object?>>()!;
        Dictionary<string, object?> viaSerializer = YamlSerializer.Deserialize<Dictionary<string, object?>>(Yaml)!;
        Assert.Equal(viaSerializer.Keys, viaNode.Keys);
    }

    [Fact]
    public void A_complex_key_is_a_binding_error_at_the_key_through_a_node_and_the_serializer()
    {
        const string Yaml = "? [a, b]\n: 1\n";
        using YamlDocument doc = YamlDocument.Parse(Yaml);
        YamlException fromNode = Assert.Throws<YamlException>(() => doc.Root.Deserialize<object>());
        YamlException fromSerializer = Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<object>(Yaml));
        Assert.Equal(YamlErrorCategory.Binding, fromNode.Category);
        Assert.Equal((fromSerializer.Category, fromSerializer.Line, fromSerializer.Column, fromSerializer.Offset), (fromNode.Category, fromNode.Line, fromNode.Column, fromNode.Offset));
    }

    [Fact]
    public void An_alias_bomb_bound_through_a_node_fails_where_the_serializer_fails()
    {
        string yaml = Bomb(9, 10);
        YamlException fromSerializer = Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<object>(yaml));
        using YamlDocument doc = YamlDocument.Parse(yaml);
        YamlException fromNode = Assert.Throws<YamlException>(() => doc.Root.Deserialize<object>());
        Assert.Equal(YamlErrorCategory.Limit, fromNode.Category);
        Assert.Equal((fromSerializer.Category, fromSerializer.Line, fromSerializer.Column, fromSerializer.Offset), (fromNode.Category, fromNode.Line, fromNode.Column, fromNode.Offset));
    }
}
