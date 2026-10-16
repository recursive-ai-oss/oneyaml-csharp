using System.Text;
using OneYaml.Parsing;
using OneYaml.Text;
using Xunit;

namespace OneYaml.Tests;

/// <summary>Behaviour of the reader and the input layer on small, named inputs.</summary>
public class ReaderBehaviorTests
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

    // ---- Byte order marks ----

    [Fact]
    public void A_byte_order_mark_in_the_middle_of_a_document_is_an_error_at_the_text_after_it()
    {
        YamlException ex = Fails("a: 1\n\uFEFFb: 2\n");
        Assert.Equal(YamlErrorCategory.Syntax, ex.Category);
        Assert.Equal(2, ex.Line);
        Assert.Equal(1, ex.Column);
    }

    [Fact]
    public void A_byte_order_mark_is_content_inside_a_quoted_scalar_and_an_error_in_a_plain_one()
    {
        Assert.Equal(["+STR", "+DOC", "+MAP", "=VAL :a", "=VAL \"x\uFEFFy", "-MAP", "-DOC", "-STR"], Events("a: \"x\uFEFFy\"\n"));
        YamlException ex = Fails("a: x\uFEFFy\n");
        Assert.Equal(YamlErrorCategory.Syntax, ex.Category);
        Assert.Equal(5, ex.Column);
    }

    [Fact]
    public void A_byte_order_mark_may_start_a_document_after_an_end_marker_or_before_a_start_marker()
    {
        Assert.Equal(["+STR", "+DOC", "=VAL :a", "-DOC ...", "+DOC", "=VAL :b", "-DOC", "-STR"], Events("a\n...\n\uFEFFb\n"));
        Assert.Equal(["+STR", "+DOC ---", "=VAL :a", "-DOC", "+DOC ---", "=VAL :b", "-DOC", "-STR"], Events("---\na\n\uFEFF---\nb\n"));
    }

    [Fact]
    public void The_leading_byte_order_mark_is_kept_as_a_fact_and_not_counted_in_positions()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF, (byte)'a', (byte)':', (byte)' ', (byte)'b', (byte)':', (byte)' ', (byte)'c'];
        using YamlInput input = YamlInput.FromBytes(bytes, YamlLimits.Default);
        Assert.True(input.HasByteOrderMark);
        Assert.Equal(YamlEncoding.Utf8, input.Encoding);
        Assert.Equal(7, input.Utf8Text.Length);
        YamlException ex = Fails("\uFEFFa: b: c");
        Assert.Equal(1, ex.Line);
        Assert.Equal(6, ex.Column);
    }

    // ---- Encodings ----

    [Theory]
    [InlineData(new byte[] { 0x00, 0x00, 0xFE, 0xFF, 0x00, 0x00, 0x00, 0x61 }, "Utf32BigEndian", true)]
    [InlineData(new byte[] { 0xFF, 0xFE, 0x00, 0x00, 0x61, 0x00, 0x00, 0x00 }, "Utf32LittleEndian", true)]
    [InlineData(new byte[] { 0xFE, 0xFF, 0x00, 0x61 }, "Utf16BigEndian", true)]
    [InlineData(new byte[] { 0xFF, 0xFE, 0x61, 0x00 }, "Utf16LittleEndian", true)]
    [InlineData(new byte[] { 0xEF, 0xBB, 0xBF, 0x61 }, "Utf8", true)]
    [InlineData(new byte[] { 0x00, 0x00, 0x00, 0x61 }, "Utf32BigEndian", false)]
    [InlineData(new byte[] { 0x61, 0x00, 0x00, 0x00 }, "Utf32LittleEndian", false)]
    [InlineData(new byte[] { 0x00, 0x61, 0x00, 0x62 }, "Utf16BigEndian", false)]
    [InlineData(new byte[] { 0x61, 0x00, 0x62, 0x00 }, "Utf16LittleEndian", false)]
    [InlineData(new byte[] { 0x61, 0x62 }, "Utf8", false)]
    public void The_encoding_is_found_from_the_mark_or_the_null_bytes(byte[] bytes, string expectedName, bool hasMark)
    {
        using YamlInput input = YamlInput.FromBytes(bytes, YamlLimits.Default);
        Assert.Equal(Enum.Parse<YamlEncoding>(expectedName), input.Encoding);
        Assert.Equal(hasMark, input.HasByteOrderMark);
        Assert.Equal((byte)'a', input.Utf8Text[0]);
    }

    [Theory]
    [InlineData(new byte[] { 0x00, 0x00, 0xFE, 0xFF, 0x00, 0x11, 0x00, 0x00 })] // UTF-32BE above U+10FFFF
    [InlineData(new byte[] { 0xFF, 0xFE, 0x00, 0x00, 0x00, 0xD8, 0x00, 0x00 })] // UTF-32LE surrogate
    [InlineData(new byte[] { 0x00, 0x00, 0x00, 0x61, 0x00, 0x00 })] // UTF-32BE cut short
    [InlineData(new byte[] { 0xFF, 0xFE, 0x61, 0x00, 0x62 })] // UTF-16LE odd length
    [InlineData(new byte[] { 0xFE, 0xFF, 0xD8, 0x00, 0x00, 0x61 })] // UTF-16BE high surrogate unpaired
    [InlineData(new byte[] { 0x00, 0x61, 0xDC, 0x00 })] // UTF-16BE low surrogate alone
    [InlineData(new byte[] { 0x61, 0xC0, 0xAF })] // overlong UTF-8
    [InlineData(new byte[] { 0x61, 0xED, 0xA0, 0x80 })] // UTF-8 encoded surrogate
    [InlineData(new byte[] { 0x61, 0xF4, 0x90, 0x80, 0x80 })] // above U+10FFFF
    [InlineData(new byte[] { 0x61, 0x80 })] // lone continuation byte
    [InlineData(new byte[] { 0x61, 0xE2, 0x82 })] // truncated sequence
    public void Malformed_input_is_an_encoding_error_and_never_replaced(byte[] bytes)
    {
        EventText.Result r = EventText.Read(bytes);
        Assert.NotNull(r.Error);
        Assert.Equal(YamlErrorCategory.Encoding, r.Error!.Category);
        Assert.Empty(r.Events);
    }

    [Fact]
    public void A_utf_8_error_points_at_the_bad_byte_with_a_line_and_column()
    {
        byte[] bytes = [.. "a: 1\nb: x"u8.ToArray(), 0xFF, (byte)'y'];
        EventText.Result r = EventText.Read(bytes);
        Assert.Equal(YamlErrorCategory.Encoding, r.Error!.Category);
        Assert.Equal(2, r.Error.Line);
        Assert.Equal(5, r.Error.Column);
        Assert.Equal(9, r.Error.Offset);
    }

    [Fact]
    public void Text_with_an_unpaired_surrogate_is_an_encoding_error()
    {
        var ex = Assert.Throws<YamlException>(() =>
        {
            using YamlInput input = YamlInput.FromChars("a: \uD800", YamlLimits.Default);
        });
        Assert.Equal(YamlErrorCategory.Encoding, ex.Category);
        Assert.Equal(1, ex.Line);
        Assert.Equal(4, ex.Column);
    }

    [Fact]
    public void Control_characters_are_refused()
    {
        foreach (string text in new[] { "a: \u0001", "a: \u007F", "a: \u0080", "a: \uFFFE", "a: \u0000" })
        {
            Assert.Equal(YamlErrorCategory.Syntax, Fails(text).Category);
        }

        // NEL, the other C1 control the grammar allows, is plain text.
        Assert.Equal("=VAL :\u0085", Events("a: \u0085\n")[4]);
    }

    [Fact]
    public void Utf_16_and_utf_32_give_the_same_events_as_utf_8()
    {
        string yaml = "a: \"\U0001F600 é\"\nb: [1, 2]\n# note\n";
        string[] expected = Events(yaml);
        foreach (Encoding encoding in new Encoding[] { new UnicodeEncoding(false, true), new UnicodeEncoding(true, true), new UnicodeEncoding(false, false), new UnicodeEncoding(true, false), new UTF32Encoding(false, true), new UTF32Encoding(true, true), new UTF32Encoding(false, false), new UTF32Encoding(true, false) })
        {
            byte[] bytes = [.. encoding.GetPreamble(), .. encoding.GetBytes(yaml)];
            EventText.Result r = EventText.Read(bytes);
            Assert.True(r.Error is null, encoding.EncodingName + " " + r.Error?.Message);
            Assert.Equal(expected, r.Events.ToArray());
        }
    }

    [Fact]
    public void A_stream_larger_than_the_input_limit_is_a_limit_error()
    {
        YamlException ex = Fails("a: 1\n", YamlLimits.Default with { MaxInputBytes = 4 });
        Assert.Equal(YamlErrorCategory.Limit, ex.Category);
    }

    // ---- Tabs ----

    [Theory]
    [InlineData("a:\n\tb: c\n")]
    [InlineData("\ta: b\n")]
    [InlineData("a:\n\tb\n")]
    [InlineData("a:\n  - x\n\t- y\n")]
    [InlineData("- \t- a\n")]
    [InlineData("-\t-\n")]
    [InlineData("a: |\n\t\nb: 1\n")]
    [InlineData("- [\n\tfoo,\n foo\n ]\n")]
    [InlineData("\t? a\n")]
    [InlineData("\t: a\n")]
    public void A_tab_is_not_indentation(string yaml)
    {
        Assert.Equal(YamlErrorCategory.Syntax, Fails(yaml).Category);
    }

    [Theory]
    [InlineData("a:\tb\n")]
    [InlineData("a: b\t# c\n")]
    [InlineData("a:\n  \tb\n")]
    [InlineData("- a\t\n- b\n")]
    [InlineData("a\t: b\n")]
    [InlineData("- [\n\t\n foo\n ]\n")]
    [InlineData("\tfoo\n")]
    public void A_tab_is_separation_where_a_value_or_comment_follows(string yaml)
    {
        Assert.True(Read(yaml).Error is null, yaml);
    }

    // ---- Tags and comments ----

    [Fact]
    public void A_hash_inside_a_tag_is_part_of_the_tag_and_a_hash_after_a_space_is_a_comment()
    {
        Assert.Equal(["+STR", "+DOC", "=VAL <!foo#bar> :a", "-DOC", "-STR"], Events("!foo#bar a\n"));
        Assert.Equal(["+STR", "+DOC", "=VAL <!t> :b", "-DOC", "-STR"], Events("!t #comment\nb\n"));
        Assert.Equal(["+STR", "+DOC", "=VAL <tag:yaml.org,2002:str#x> :a", "-DOC", "-STR"], Events("!!str#x a\n"));
    }

    [Fact]
    public void A_hash_without_a_space_before_it_is_not_a_comment()
    {
        Assert.Equal(["+STR", "+DOC", "=VAL :a#b", "-DOC", "-STR"], Events("a#b\n"));
        Assert.Equal(YamlErrorCategory.Syntax, Fails("\"a\"#b\n").Category);
        Assert.Equal(YamlErrorCategory.Syntax, Fails("[a,#b]\n").Category);
    }

    [Fact]
    public void Tag_handles_resolve_through_directives_and_percent_escapes_stay_unless_ascii()
    {
        Assert.Equal(["+STR", "+DOC ---", "=VAL <tag:x,1:a b> :v", "-DOC", "-STR"], Events("%TAG !e! tag:x,1:\n--- !e!a%20b v\n"));
        Assert.Equal(["+STR", "+DOC ---", "=VAL <tag:x,1:%C3%A9> :v", "-DOC", "-STR"], Events("%TAG !e! tag:x,1:\n--- !e!%C3%A9 v\n"));
        Assert.Equal(["+STR", "+DOC", "=VAL <!> :v", "-DOC", "-STR"], Events("! v\n"));
        Assert.Equal(["+STR", "+DOC", "=VAL <tag:x:y> :v", "-DOC", "-STR"], Events("!<tag:x:y> v\n"));
        Assert.Equal(YamlErrorCategory.Syntax, Fails("!e!a v\n").Category);
    }

    [Fact]
    public void Tag_directives_apply_to_their_own_document_only()
    {
        EventText.Result r = Read("%TAG !e! tag:x,1:\n--- !e!a v\n...\n--- !e!a v\n");
        Assert.NotNull(r.Error);
        Assert.Equal(["+STR", "+DOC ---", "=VAL <tag:x,1:a> :v", "-DOC ..."], r.Events.Take(4).ToArray());
    }

    // ---- Documents ----

    [Fact]
    public void Directives_need_an_end_marker_before_them_and_a_start_marker_after()
    {
        Assert.Equal(YamlErrorCategory.Syntax, Fails("a: b\n%YAML 1.2\n---\nc\n").Category);
        Assert.Equal(YamlErrorCategory.Syntax, Fails("%YAML 1.2\nfoo\n").Category);
        Assert.Equal(YamlErrorCategory.Syntax, Fails("%YAML 1.2\n%YAML 1.2\n---\n").Category);
        Assert.Equal(YamlErrorCategory.Syntax, Fails("%YAML 2.0\n---\n").Category);
        Assert.Equal(["+STR", "+DOC", "=VAL :a", "-DOC ...", "+DOC ---", "=VAL :b", "-DOC", "-STR"], Events("a\n...\n%YAML 1.2\n---\nb\n"));
    }

    [Fact]
    public void A_bare_second_document_needs_an_end_marker_first()
    {
        Assert.Equal(["+STR", "+DOC", "=VAL :a", "-DOC ...", "+DOC", "=VAL :b", "-DOC", "-STR"], Events("a\n...\nb\n"));
    }

    [Fact]
    public void An_unfinished_document_is_not_ended_before_the_error()
    {
        EventText.Result r = Read("[a, b]]\n");
        Assert.NotNull(r.Error);
        Assert.DoesNotContain("-DOC", r.Events);
    }

    // ---- Scalar values ----

    [Theory]
    [InlineData("a: \"x\\ty\\u00e9\\x41\\U0001F600\\L\\N\\_\\0\\\\\\/\\\"\"\n", "=VAL \"x\\ty\u00e9A\U0001F600\u2028\u0085\u00a0\\0\\\\/\"")]
    [InlineData("a: \"one\n  two\n\n  three\"\n", "=VAL \"one two\\nthree")]
    [InlineData("a: \"one \\\n  two\"\n", "=VAL \"one two")]
    [InlineData("a: \"keep \\ \\t\\\n  end\"\n", "=VAL \"keep  \\t" + "end")]
    [InlineData("a: 'it''s\n  folded'\n", "=VAL 'it's folded")]
    [InlineData("a: one\n  two  \n\n\n  three\n", "=VAL :one two\\n\\nthree")]
    [InlineData("a: |\n  x\n   y\n\n  z\n", "=VAL |x\\n y\\n\\nz\\n")]
    [InlineData("a: |-\n  x\n\n", "=VAL |x")]
    [InlineData("a: |+\n  x\n\n\n", "=VAL |x\\n\\n\\n")]
    [InlineData("a: >\n  one\n  two\n\n  three\n   more\n  after\n", "=VAL >one two\\nthree\\n more\\nafter\\n")]
    [InlineData("a: >-\n  one\n  two\n", "=VAL >one two")]
    [InlineData("a: |2\n   x\n", "=VAL | x\\n")]
    [InlineData("a: |\r\n  x\r\n  y\r\n", "=VAL |x\\ny\\n")]
    [InlineData("a: \"p\r\n\r\n q\"\r\n", "=VAL \"p\\nq")]
    public void Scalar_values_are_unescaped_folded_and_chomped(string yaml, string expectedValue)
    {
        string[] events = Events(yaml);
        Assert.Equal(expectedValue, events[4]);
    }

    [Fact]
    public void Plain_scalars_stop_at_colon_space_and_hash_and_flow_indicators_only_in_flow()
    {
        Assert.Equal("=VAL :a:b,c[d]{e}", Events("a:b,c[d]{e}\n")[2]);
        Assert.Equal(["+STR", "+DOC", "+SEQ []", "=VAL :a:b", "=VAL :c", "-SEQ", "-DOC", "-STR"], Events("[a:b, c]\n"));
        Assert.Equal(YamlErrorCategory.Syntax, Fails(",a\n").Category);
        Assert.Equal(YamlErrorCategory.Syntax, Fails("@a\n").Category);
        Assert.Equal(YamlErrorCategory.Syntax, Fails("`a\n").Category);
        Assert.Equal(YamlErrorCategory.Syntax, Fails("%a\n").Category);
    }

    [Fact]
    public void A_flow_mapping_key_may_span_lines_but_a_block_key_may_not()
    {
        Assert.Equal(["+STR", "+DOC", "+MAP {}", "=VAL :multi line", "=VAL :v", "-MAP", "-DOC", "-STR"], Events("{ multi\n  line: v }\n"));
        Assert.Equal(YamlErrorCategory.Syntax, Fails("multi\n  line: v\n").Category);
    }

    [Fact]
    public void An_implicit_key_of_1024_characters_is_accepted_and_1025_is_not()
    {
        Assert.Equal(1024, Events(new string('k', 1024) + ": v\n")[3].Length - "=VAL :".Length);
        YamlException ex = Fails(new string('k', 1025) + ": v\n");
        Assert.Equal(YamlErrorCategory.Syntax, ex.Category);
        Assert.Equal(1027, ex.Column);

        // Characters count, not bytes.
        Assert.Equal(1024, Events(new string('é', 1024) + ": v\n")[3].Length - "=VAL :".Length);
    }

    // ---- Properties, events, positions ----

    [Fact]
    public void Properties_and_positions_are_reported_with_the_event()
    {
        byte[] bytes = "&a !!str x\n"u8.ToArray();
        using YamlInput input = YamlInput.FromBytes(bytes, YamlLimits.Default);
        var reader = new YamlReader(input.Utf8Text);
        try
        {
            Assert.True(reader.Read());
            Assert.True(reader.Read());
            Assert.True(reader.Read());
            Assert.Equal(YamlTokenType.Scalar, reader.TokenType);
            Assert.Equal("a", Encoding.UTF8.GetString(reader.Anchor));
            Assert.Equal(1, reader.AnchorStart);
            Assert.Equal("!!str", Encoding.UTF8.GetString(reader.RawTag));
            Assert.Equal("tag:yaml.org,2002:str", reader.GetResolvedTag());
            Assert.Equal(ScalarStyle.Plain, reader.ScalarStyle);
            Assert.Equal(9, reader.Start);
            Assert.Equal(10, reader.End);
            Assert.Equal("x", Encoding.UTF8.GetString(reader.RawScalar));
            Assert.False(reader.ValueIsEscapedOrFolded);
            Assert.Equal(0, reader.Depth);
        }
        finally
        {
            reader.Dispose();
        }
    }

    [Fact]
    public void Positions_count_characters_outside_the_basic_plane_once()
    {
        YamlException ex = Fails("a: \U0001F600\n b: c\n");
        Assert.Equal(2, ex.Line);
    }

    [Fact]
    public void Comments_are_recorded_only_when_asked()
    {
        byte[] bytes = "a: 1 # one\n# two\nb: |  # three\n  x\n"u8.ToArray();
        using YamlInput input = YamlInput.FromBytes(bytes, YamlLimits.Default);
        var off = new YamlReader(input.Utf8Text);
        var on = new YamlReader(input.Utf8Text, new YamlReaderOptions { RecordComments = true });
        try
        {
            while (off.Read())
            {
            }

            while (on.Read())
            {
            }

            Assert.Equal(0, off.CommentCount);
            Assert.Equal(3, on.CommentCount);
            (int start, int end) = on.GetComment(0);
            Assert.Equal("# one", Encoding.UTF8.GetString(bytes.AsSpan(start, end - start)));
            (start, end) = on.GetComment(1);
            Assert.Equal("# two", Encoding.UTF8.GetString(bytes.AsSpan(start, end - start)));
            (start, end) = on.GetComment(2);
            Assert.Equal("# three", Encoding.UTF8.GetString(bytes.AsSpan(start, end - start)));
        }
        finally
        {
            off.Dispose();
            on.Dispose();
        }
    }

    // ---- Limits and resources ----

    [Fact]
    public void Nesting_depth_is_limited_and_the_limit_is_exact()
    {
        string Nested(int depth) => string.Concat(Enumerable.Repeat("[", depth)) + string.Concat(Enumerable.Repeat("]", depth));
        Assert.True(Read(Nested(1000)).Error is null);
        Assert.Equal(YamlErrorCategory.Limit, Fails(Nested(1001)).Category);
        Assert.True(Read(Nested(128), YamlLimits.Untrusted).Error is null);
        Assert.Equal(YamlErrorCategory.Limit, Fails(Nested(129), YamlLimits.Untrusted).Category);
        string blocks = string.Concat(Enumerable.Range(0, 1001).Select(_ => "- ")) + "x\n";
        Assert.Equal(YamlErrorCategory.Limit, Fails(blocks).Category);
    }

    [Fact]
    public void Scalar_size_and_document_count_are_limited()
    {
        Assert.Equal(YamlErrorCategory.Limit, Fails("a: " + new string('x', 100) + "\n", YamlLimits.Default with { MaxScalarBytes = 99 }).Category);
        Assert.True(Read("a: " + new string('x', 99) + "\n", YamlLimits.Default with { MaxScalarBytes = 99 }).Error is null);
        Assert.Equal(YamlErrorCategory.Limit, Fails("--- a\n--- b\n--- c\n", YamlLimits.Default with { MaxDocuments = 2 }).Category);
        Assert.True(Read("--- a\n--- b\n", YamlLimits.Default with { MaxDocuments = 2 }).Error is null);
    }

    [Theory]
    [InlineData("block")]
    [InlineData("flow")]
    [InlineData("flow-mapping")]
    public void Very_deep_nesting_needs_no_stack(string shape)
    {
        // A thread with a small stack proves nothing recurses with the input depth.
        const int Depth = 200_000;
        string yaml = shape switch
        {
            "block" => string.Concat(Enumerable.Repeat("- ", Depth)) + "x\n",
            "flow" => string.Concat(Enumerable.Repeat("[", Depth)) + string.Concat(Enumerable.Repeat("]", Depth)),
            _ => string.Concat(Enumerable.Repeat("{a: ", Depth)) + "x" + string.Concat(Enumerable.Repeat("}", Depth)),
        };
        int events = 0;
        Exception? failure = null;
        var thread = new Thread(
            () =>
            {
                try
                {
                    EventText.Result r = Read(yaml, YamlLimits.None);
                    Assert.Null(r.Error);
                    events = r.Events.Count;
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            },
            256 * 1024);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
        Assert.True(events > Depth);
    }

    [Fact]
    public void Flow_collections_and_keys_of_extreme_shapes_are_read()
    {
        // Each of these made a reader that rechecks every open key at every token take minutes; they must still read.
        var shapes = new Dictionary<string, string>
        {
            ["deep flow"] = string.Concat(Enumerable.Repeat("[", 100_000)) + string.Concat(Enumerable.Repeat("]", 100_000)),
            ["deep flow mappings"] = string.Concat(Enumerable.Repeat("{", 100_000)) + string.Concat(Enumerable.Repeat("}", 100_000)),
            ["deep flow keys"] = string.Concat(Enumerable.Repeat("[a, ", 50_000)) + string.Concat(Enumerable.Repeat("]", 50_000)),
            ["wide flow mapping"] = "{" + string.Concat(Enumerable.Range(0, 100_000).Select(i => "k" + i + ": v, ")) + "}",
            ["wide flow sequence"] = "[" + string.Concat(Enumerable.Repeat("a, ", 300_000)) + "]",
            ["single pairs"] = "[" + string.Concat(Enumerable.Repeat("a: b, ", 100_000)) + "]",
            ["long flow scalar"] = "[" + string.Join(' ', Enumerable.Repeat("word", 100_000)) + "]",
            ["many keys"] = string.Concat(Enumerable.Range(0, 100_000).Select(i => "key" + i + ": 1\n")),
        };
        foreach ((string name, string yaml) in shapes)
        {
            EventText.Result r = Read(yaml, YamlLimits.None);
            Assert.True(r.Error is null, name + ": " + r.Error?.Message);
        }
    }

    [Fact]
    public void Reading_events_allocates_nothing_once_the_buffers_are_pooled()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(
            "# config\nname: x\nitems:\n  - {a: 1, b: [1, 2]}\n  - \"q\\n\"\n  - |\n    text\n  - &r y\n  - *r\n---\nother: 1\n");
        long Run()
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            using YamlInput input = YamlInput.FromBytes(bytes, YamlLimits.Default);
            var reader = new YamlReader(input.Utf8Text);
            try
            {
                int n = 0;
                while (reader.Read())
                {
                    n += reader.End;
                }

                Assert.True(n > 0);
            }
            finally
            {
                reader.Dispose();
            }

            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        for (int i = 0; i < 5; i++)
        {
            Run();
        }

        Assert.Equal(0, Run());
    }
}
