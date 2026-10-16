using System.Text;
using OneYaml.Parsing;
using OneYaml.Text;
using Xunit;

namespace OneYaml.Tests;

/// <summary>Reader behaviour at the edges: which characters go where, what may follow a property, and resource bounds.</summary>
public class ReaderEdgeTests
{
    private static EventText.Result Read(string yaml, YamlLimits? limits = null)
        => EventText.Read(Encoding.UTF8.GetBytes(yaml), limits);

    private static string[] Events(string yaml, YamlLimits? limits = null)
    {
        EventText.Result r = Read(yaml, limits);
        Assert.True(r.Error is null, r.Error?.Message);
        return r.Events.ToArray();
    }

    private static YamlException Fails(string yaml, YamlLimits? limits = null)
    {
        EventText.Result r = Read(yaml, limits);
        Assert.NotNull(r.Error);
        return r.Error!;
    }

    // ---- Implicit keys are checked in constant time per token (cost is covered by the benchmarks) ----

    [Theory]
    [InlineData(127, true)]
    [InlineData(1000, false)]
    public void A_very_long_token_after_many_open_keys_is_read_without_error(int brackets, bool untrusted)
    {
        // Every open bracket holds a possible key; one 16 MB scalar follows. Counting the
        // characters of every key again for the whole scalar took seconds.
        YamlLimits limits = untrusted ? YamlLimits.Untrusted : YamlLimits.Default;
        string yaml = new string('[', brackets) + new string('x', 16_000_000) + new string(']', brackets);
        EventText.Result r = Read(yaml, limits);
        Assert.True(r.Error is null, r.Error?.Message);
    }

    // ---- Properties must be separated from what they mark ----

    [Theory]
    [InlineData("!a[b]\n", 1, 3)]
    [InlineData("&a[b]\n", 1, 3)]
    [InlineData("k: !a{b: c}\n", 1, 6)]
    [InlineData("k: &a{b: c}\n", 1, 6)]
    [InlineData("[!a[b]]\n", 1, 4)]
    [InlineData("- &a[b]\n", 1, 5)]
    public void A_flow_collection_cannot_follow_a_property_without_separation(string yaml, int line, int column)
    {
        YamlException ex = Fails(yaml);
        Assert.Equal(YamlErrorCategory.Syntax, ex.Category);
        Assert.Equal(line, ex.Line);
        Assert.Equal(column, ex.Column);
    }

    [Theory]
    [InlineData("!a [b]\n")]
    [InlineData("&a [b]\n")]
    [InlineData("k: !a\n  [b]\n")]
    [InlineData("[!!str, a]\n")]
    [InlineData("[&x]\n")]
    [InlineData("{a: !!str, b: &y}\n")]
    [InlineData("!a\n")]
    [InlineData("&a")]
    public void Properties_followed_by_white_space_or_a_closing_token_are_accepted(string yaml)
    {
        Assert.True(Read(yaml).Error is null, yaml);
    }

    [Theory]
    [InlineData("!a%2 x\n", 1, 5)]
    [InlineData("!a%zz x\n", 1, 4)]
    [InlineData("!<a%zz> x\n", 1, 5)]
    [InlineData("!<a%2z> x\n", 1, 6)]
    [InlineData("%TAG !e! tag:%2\n--- a\n", 1, 16)]
    public void A_percent_escape_in_a_tag_points_at_the_first_bad_digit(string yaml, int line, int column)
    {
        YamlException ex = Fails(yaml);
        Assert.Equal(YamlErrorCategory.Syntax, ex.Category);
        Assert.Equal(line, ex.Line);
        Assert.Equal(column, ex.Column);
    }

    // ---- Which characters are allowed where ----

    [Theory]
    [InlineData("a: \"x\u007Fy\"\n")]
    [InlineData("a: 'x\u007Fy'\n")]
    [InlineData("{\"a\": \"\u007F\"}\n")]
    [InlineData("a: \"x\u0085\u0090\u009Fy\"\n")]
    [InlineData("a: 'x\uFFFE\uFFFFy'\n")]
    [InlineData("a: \"x\uFEFFy\"\n")]
    [InlineData("a: 'x\uFEFFy'\n")]
    [InlineData("\"k\u007F\": v\n")]
    public void Quoted_scalars_may_hold_any_character_but_the_C0_controls(string yaml)
    {
        Assert.True(Read(yaml).Error is null, yaml);
    }

    [Theory]
    [InlineData("a: \"x\u0001y\"\n")]
    [InlineData("a: 'x\u0000y'\n")]
    public void Quoted_scalars_still_refuse_the_C0_controls(string yaml)
    {
        Assert.Equal(YamlErrorCategory.Syntax, Fails(yaml).Category);
    }

    [Theory]
    [InlineData("a: x\u007Fy\n", 1, 5)]
    [InlineData("a: x\u0085y\n", 0, 0)]
    [InlineData("a: x\u0080y\n", 1, 5)]
    [InlineData("a: x\u009Fy\n", 1, 5)]
    [InlineData("a: x\uFFFEy\n", 1, 5)]
    [InlineData("a: x\uFFFFy\n", 1, 5)]
    [InlineData("a: x\uFEFFy\n", 1, 5)]
    [InlineData("a\uFEFFb: c\n", 1, 2)]
    [InlineData("a: b # x\u007F\n", 1, 9)]
    [InlineData("a: b # x\uFEFF\n", 1, 9)]
    [InlineData("a: |\n  x\u007Fy\n", 2, 4)]
    [InlineData("a: |\n  x\uFEFFy\n", 2, 4)]
    [InlineData("a: >\n  x\n  y\uFEFF\n", 3, 4)]
    [InlineData("a: &x\u007F b\n", 1, 6)]
    [InlineData("a: *x\uFEFF\n", 1, 6)]
    [InlineData("a: x\n  y\u007F\n", 2, 4)]
    [InlineData("[a, x\u007F]\n", 1, 6)]
    [InlineData("%FOO x\u007F\n--- a\n", 1, 7)]
    public void Other_text_refuses_non_printable_characters_and_the_byte_order_mark(string yaml, int line, int column)
    {
        if (line == 0)
        {
            // NEL is printable and plain text.
            Assert.True(Read(yaml).Error is null);
            return;
        }

        YamlException ex = Fails(yaml);
        Assert.Equal(YamlErrorCategory.Syntax, ex.Category);
        Assert.Equal(line, ex.Line);
        Assert.Equal(column, ex.Column);
    }

    [Fact]
    public void A_byte_order_mark_may_stand_in_front_of_a_comment_after_a_bare_document()
    {
        string[] expected = ["+STR", "+DOC", "=VAL :a", "-DOC", "-STR"];
        Assert.Equal(expected, Events("a\n\uFEFF# c\n"));
        Assert.Equal(expected, Events("a\n\uFEFF\n\uFEFF  # c\n# d\n"));
        Assert.Equal(expected, Events("a\r\n\uFEFF# c\r\n"));

        // Text after the comment would put the mark inside the document.
        Assert.Equal(YamlErrorCategory.Syntax, Fails("a: 1\n\uFEFF# c\nb: 2\n").Category);
        Assert.Equal(YamlErrorCategory.Syntax, Fails("a\n\uFEFF# c\n  b\n").Category);
        Assert.Equal(YamlErrorCategory.Syntax, Fails("a\n\uFEFFb\n").Category);
    }

    [Fact]
    public void Many_byte_order_mark_comment_lines_are_read()
    {
        string yaml = "a\n" + string.Concat(Enumerable.Repeat("\uFEFF# c\n", 100_000));
        EventText.Result r = Read(yaml);
        Assert.Null(r.Error);
    }

    // ---- Tabs ----

    [Fact]
    public void A_line_of_only_a_tab_after_a_block_scalar_is_not_content()
    {
        Assert.Equal(["+STR", "+DOC", "+MAP", "=VAL :k", "=VAL |a\\n", "-MAP", "-DOC", "-STR"], Events("k: |\n a\n\t\n"));
        Assert.Equal(["+STR", "+DOC", "+MAP", "=VAL :k", "=VAL |a\\n", "=VAL :b", "=VAL :1", "-MAP", "-DOC", "-STR"], Events("k: |\n a\n\t\nb: 1\n"));

        // A tab followed by text is a tab used as indentation.
        Assert.Equal(YamlErrorCategory.Syntax, Fails("k: |\n a\n\tb\n").Category);
    }

    [Theory]
    [InlineData("k: a\n\tb\n", 2, 2)]
    [InlineData("k: [a\n\tb]\n", 2, 2)]
    [InlineData("k: [a\n \tb]\n", 0, 0)]
    [InlineData("- [a\n\t b]\n", 2, 3)]
    public void A_tab_does_not_indent_the_continuation_of_a_scalar(string yaml, int line, int column)
    {
        if (line == 0)
        {
            Assert.True(Read(yaml).Error is null, yaml);
            return;
        }

        YamlException ex = Fails(yaml);
        Assert.Equal(YamlErrorCategory.Syntax, ex.Category);
        Assert.Equal(line, ex.Line);
        Assert.Equal(column, ex.Column);
    }

    // ---- Limits and resources ----

    [Fact]
    public void A_run_of_open_flow_mappings_stops_at_the_depth_limit_without_filling_the_queue()
    {
        byte[] yaml = new byte[1_100_000];
        Array.Fill(yaml, (byte)'{');
        EventText.Read(yaml, YamlLimits.Untrusted);
        long before = GC.GetAllocatedBytesForCurrentThread();
        EventText.Result r = EventText.Read(yaml, YamlLimits.Untrusted);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.NotNull(r.Error);
        Assert.Equal(YamlErrorCategory.Limit, r.Error!.Category);
        Assert.Contains("depth", r.Error.Message);
        Assert.True(allocated < 4_000_000, $"allocated {allocated} bytes");
    }

    [Fact]
    public void Open_blocks_count_with_open_flow_collections_towards_the_depth_limit()
    {
        YamlLimits limits = YamlLimits.Default with { MaxDepth = 4 };
        Assert.True(Read("- - - [a]\n", limits).Error is null);
        YamlException ex = Fails("- - - [[a]]\n", limits);
        Assert.Equal(YamlErrorCategory.Limit, ex.Category);
    }

    [Fact]
    public void Arrays_stop_growing_at_the_largest_array_size()
    {
        Assert.Equal(64, YamlReader.NextCapacity(32));
        Assert.Equal(16, YamlReader.NextCapacity(1));
        Assert.Equal(Array.MaxLength, YamlReader.NextCapacity((1 << 30) + 1));
        Assert.Equal(Array.MaxLength, YamlReader.NextCapacity(Array.MaxLength - 1));
        Assert.Equal(-1, YamlReader.NextCapacity(Array.MaxLength));
    }

    [Fact]
    public void A_reader_that_failed_keeps_reporting_the_same_error()
    {
        byte[] bytes = "a: [b\n"u8.ToArray();
        using YamlInput input = YamlInput.FromBytes(bytes, YamlLimits.Default);
        var reader = new YamlReader(input.Utf8Text);
        try
        {
            YamlException? first = null;
            while (first is null)
            {
                try
                {
                    Assert.True(reader.Read());
                }
                catch (YamlException ex)
                {
                    first = ex;
                }
            }

            YamlException? again = null;
            try
            {
                reader.Read();
            }
            catch (YamlException ex)
            {
                again = ex;
            }

            Assert.Same(first, again);
        }
        finally
        {
            reader.Dispose();
        }
    }

    // ---- Input layer ----

    [Fact]
    public void A_string_larger_than_the_limit_is_refused_before_a_buffer_is_made()
    {
        string text = new('a', 4_000_000);
        YamlLimits limits = YamlLimits.Default with { MaxInputBytes = 1000 };
        Assert.Throws<YamlException>(() => YamlInput.FromChars(text, limits));
        long before = GC.GetAllocatedBytesForCurrentThread();
        YamlException ex = Assert.Throws<YamlException>(() => YamlInput.FromChars(text, limits));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(YamlErrorCategory.Limit, ex.Category);
        Assert.True(allocated < 100_000, $"allocated {allocated} bytes");
    }

    [Fact]
    public void Utf_16_text_that_is_larger_as_utf_8_is_checked_against_the_limit()
    {
        // 1,000 characters of three UTF-8 bytes each: 2,000 bytes of UTF-16, 3,000 of UTF-8.
        byte[] bytes = [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(new string('\u4E2D', 1000))];
        YamlException ex = Assert.Throws<YamlException>(() => YamlInput.FromBytes(bytes, YamlLimits.Default with { MaxInputBytes = 2500 }));
        Assert.Equal(YamlErrorCategory.Limit, ex.Category);
        using YamlInput ok = YamlInput.FromBytes(bytes, YamlLimits.Default with { MaxInputBytes = 3000 });
        Assert.Equal(3000, ok.Utf8Text.Length);
    }

    [Fact]
    public void A_very_long_string_is_converted_exactly_across_its_chunks()
    {
        // A surrogate pair that straddles the chunk boundary used while counting.
        char[] chars = new char[(1 << 24) + 100];
        Array.Fill(chars, 'a');
        chars[(1 << 24) - 1] = '\uD83D';
        chars[1 << 24] = '\uDE00';
        using YamlInput input = YamlInput.FromChars(chars, YamlLimits.None);
        Assert.Equal(chars.Length + 2, input.Utf8Text.Length);
    }

    [Fact]
    public void Errors_in_a_string_that_starts_with_a_byte_order_mark_match_the_byte_path()
    {
        var chars = Assert.Throws<YamlException>(() => YamlInput.FromChars("\uFEFFa: \uD800", YamlLimits.Default));
        Assert.Equal(1, chars.Line);
        Assert.Equal(4, chars.Column);
        Assert.Equal(3, chars.Offset);
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xFE, 0x00 })]
    [InlineData(new byte[] { 0xFF, 0xFE, 0x61, 0x00, 0x00, 0xD8 })]
    [InlineData(new byte[] { 0xFF, 0xFE })]
    [InlineData(new byte[] { 0xFE, 0xFF, 0xD8 })]
    [InlineData(new byte[] { 0xFF, 0xFE, 0x00, 0x00, 0x61 })]
    [InlineData(new byte[] { 0xFF, 0xFE, 0x00, 0x00 })]
    [InlineData(new byte[] { 0x00, 0x00, 0xFE, 0xFF, 0x00, 0x00 })]
    public void Short_or_cut_inputs_after_a_mark_give_an_encoding_error_or_an_empty_stream(byte[] bytes)
    {
        EventText.Result r = EventText.Read(bytes);
        if (r.Error is null)
        {
            Assert.Equal(["+STR", "-STR"], r.Events.ToArray());
            return;
        }

        Assert.Equal(YamlErrorCategory.Encoding, r.Error.Category);
        Assert.InRange(r.Error.Offset, 0, bytes.Length);
        Assert.True(r.Error.Line >= 1 && r.Error.Column >= 1);
    }

    // ---- Runs of closing tokens ----

    [Theory]
    [InlineData("[[[a]]]\n")]
    [InlineData("{a: [[b]], c: {d: {e: f}}}\n")]
    [InlineData("[[a]]: b\n")]
    [InlineData("[[[a]], [b]]\n")]
    [InlineData("[ [ [a] ] ,[[b]]]\n")]
    [InlineData("{[[a]]: {b: [c]}}\n")]
    public void Each_closing_bracket_of_a_run_is_reported_where_it_stands(string yaml)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(yaml);
        var starts = new List<int>();
        using YamlInput text = YamlInput.FromBytes(bytes, YamlLimits.Default);
        using var reader = new YamlReader(text.Utf8Text, new YamlReaderOptions());
        while (reader.Read())
        {
            // Only the closers written in the text: a block collection around the flow key ends without a bracket.
            if (reader.TokenType is YamlTokenType.SequenceEnd or YamlTokenType.MappingEnd && reader.Start < bytes.Length && bytes[reader.Start] is (byte)']' or (byte)'}')
            {
                Assert.Equal(reader.Start + 1, reader.End);
                starts.Add(reader.Start);
            }
        }

        var expected = new List<int>();
        for (int i = 0; i < yaml.Length; i++)
        {
            if (yaml[i] is ']' or '}')
            {
                expected.Add(i);
            }
        }

        Assert.Equal(expected, starts);
    }

    [Fact]
    public void Block_levels_that_end_together_give_one_end_event_each()
    {
        Assert.Equal(
            ["+STR", "+DOC", "+MAP", "=VAL :a", "+MAP", "=VAL :b", "+MAP", "=VAL :c", "=VAL :1", "-MAP", "-MAP", "=VAL :d", "=VAL :2", "-MAP", "-DOC", "-STR"],
            Events("a:\n b:\n  c: 1\nd: 2\n"));
        Assert.Equal(
            ["+STR", "+DOC", "+MAP", "=VAL :a", "+SEQ", "+MAP", "=VAL :b", "+SEQ", "=VAL :x", "-SEQ", "-MAP", "-SEQ", "-MAP", "-DOC", "-STR"],
            Events("a:\n- b:\n  - x\n"));
    }

    // ---- A plain key needs white space between its colon and a collection value ----

    [Theory]
    [InlineData("{a:{}}\n", 4)]
    [InlineData("{a:[]}\n", 4)]
    [InlineData("[a:{}]\n", 4)]
    [InlineData("[a:[]]\n", 4)]
    [InlineData("{x: 1, b:{c: d}}\n", 10)]
    [InlineData("{? a:{}}\n", 6)]
    [InlineData("[? a:{}]\n", 6)]
    [InlineData("{? a:[]}\n", 6)]
    [InlineData("{:{}}\n", 3)]
    [InlineData("[:{}]\n", 3)]
    [InlineData("{:[]}\n", 3)]
    public void A_colon_after_a_plain_key_cannot_run_into_a_collection(string yaml, int column)
    {
        YamlException ex = Fails(yaml);
        Assert.Equal(YamlErrorCategory.Syntax, ex.Category);
        Assert.Equal(1, ex.Line);
        Assert.Equal(column, ex.Column);
    }

    [Theory]
    [InlineData("{\"a\":{}}\n")]
    [InlineData("{'a':[]}\n")]
    [InlineData("{[a]:{}}\n")]
    [InlineData("{{a: b}:[c]}\n")]
    [InlineData("[\"a\":{}]\n")]
    [InlineData("{a: {}}\n")]
    [InlineData("{a:\n{}}\n")]
    [InlineData("{a:}\n")]
    [InlineData("{a:, b: 1}\n")]
    [InlineData("[a: []]\n")]
    [InlineData("{? \"a\":{}}\n")]
    [InlineData("{? [a]:{}}\n")]
    [InlineData("{? a: {}}\n")]
    [InlineData("{: {}}\n")]
    [InlineData("[: []]\n")]
    public void A_collection_value_after_a_quoted_key_or_with_white_space_is_still_read(string yaml)
    {
        Events(yaml);
    }

    [Theory]
    [InlineData("- !<!> foo\n", 6)]
    [InlineData("!<!> foo\n", 4)]
    [InlineData("a: !<!>\n", 7)]
    public void A_verbatim_tag_of_a_lone_bang_is_reported_at_the_closing_bracket(string yaml, int column)
    {
        YamlException ex = Fails(yaml);
        Assert.Equal(YamlErrorCategory.Syntax, ex.Category);
        Assert.Equal(1, ex.Line);
        Assert.Equal(column, ex.Column);
    }

    [Fact]
    public void A_verbatim_tag_that_starts_with_a_bang_and_goes_on_is_valid()
    {
        Events("- !<!x> foo\n");
    }

    [Theory]
    [InlineData("- !<$:?> bar\n", 5)]
    [InlineData("- !<tag:a b> bar\n", 10)]
    [InlineData("- !<tag:caf\u00e9> bar\n", 12)]
    [InlineData("- !<tag:a{b}> bar\n", 10)]
    [InlineData("- !<tag:a%2> bar\n", 12)]
    [InlineData("%TAG !e! tag:caf\u00e9\n--- a\n", 17)]
    [InlineData("%TAG !e! tag:a\"b\n--- a\n", 15)]
    public void A_verbatim_tag_or_tag_prefix_with_characters_a_URI_cannot_hold_is_a_syntax_error(string yaml, int column)
    {
        YamlException ex = Fails(yaml);
        Assert.Equal(YamlErrorCategory.Syntax, ex.Category);
        Assert.Equal(column, ex.Column);
    }

    [Theory]
    [InlineData("- !<tag:yaml.org,2002:str> a\n")]
    [InlineData("- !<!local> a\n")]
    [InlineData("- !<tag:a%C3%A9[b],c> a\n")]
    [InlineData("%TAG !e! tag:example.com,2000:app/\n--- !e!foo a\n")]
    [InlineData("%TAG !m! !my-\n--- !m!x a\n")]
    public void A_well_formed_verbatim_tag_or_tag_prefix_is_still_read(string yaml)
    {
        Events(yaml);
    }

    [Fact]
    public void A_document_refuses_a_verbatim_tag_that_is_not_a_URI()
    {
        YamlException ex = Assert.Throws<YamlException>(() => YamlDocument.Parse("- !<$:?> bar\n"));
        Assert.Equal(YamlErrorCategory.Syntax, ex.Category);
        Assert.Equal(5, ex.Column);
    }
}
