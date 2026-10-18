using System.Text;
using Xunit;
using Cs = global::OneYaml.Compat.SharpYaml;
using Oy = global::OneYaml;
using YdnCore = global::OneYaml.Compat.YamlDotNet.Core;
using YdnRep = global::OneYaml.Compat.YamlDotNet.RepresentationModel;

namespace OneYaml.Compat.Tests;

// A leading byte order mark is not a column, and the input size limit holds for the whole text however many documents it has.
public class ByteOrderMarkAndInputLimitTests
{
    // The boxed overloads, which the analyzer would otherwise ask to turn into the generic ones.
    private static readonly Type Target = typeof(object);

    private const string TwoDocuments = "a: 1\n---\nb: 2\n"; // 14 bytes

    private static Stream Utf8Stream(string text, bool mark) =>
        new MemoryStream([.. (mark ? new byte[] { 0xEF, 0xBB, 0xBF } : []), .. Encoding.UTF8.GetBytes(text)]);

    // ---- SharpYaml: the input limit counts the whole text ----

    private static Cs.YamlSerializerOptions Limited(long max) =>
        new() { Limits = Oy.YamlLimits.Default with { MaxInputBytes = max } };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharpYaml_a_text_over_the_limit_is_refused_even_when_its_first_document_fits(bool mark)
    {
        string text = (mark ? "\uFEFF" : "") + TwoDocuments;
        Cs.YamlSerializerOptions limit13 = Limited(13);

        Assert.IsAssignableFrom<Cs.YamlException>(Record.Exception(() => Cs.YamlSerializer.Deserialize<object>(text, limit13)));
        Assert.IsAssignableFrom<Cs.YamlException>(Record.Exception(() => Cs.YamlSerializer.Deserialize<object>(text.AsSpan(), limit13)));
        Assert.IsAssignableFrom<Cs.YamlException>(Record.Exception(() => Cs.YamlSerializer.Deserialize(text, Target, limit13)));
        Assert.IsAssignableFrom<Cs.YamlException>(Record.Exception(() => Cs.YamlSerializer.Deserialize(text.AsSpan(), Target, limit13)));
        Assert.IsAssignableFrom<Cs.YamlException>(Record.Exception(() => Cs.YamlSerializer.Deserialize<object>(new StringReader(text), limit13)));
        Assert.IsAssignableFrom<Cs.YamlException>(Record.Exception(() => Cs.YamlSerializer.Deserialize<object>(Utf8Stream(TwoDocuments, mark), limit13)));
        Assert.IsAssignableFrom<Cs.YamlException>(Record.Exception(() => Cs.YamlSerializer.Deserialize(Utf8Stream(TwoDocuments, mark), Target, limit13)));

        Assert.False(Cs.YamlSerializer.TryDeserialize(text, out object? v1, limit13));
        Assert.Null(v1);
        Assert.False(Cs.YamlSerializer.TryDeserialize(text, Target, out object? v2, limit13));
        Assert.Null(v2);
        Assert.False(Cs.YamlSerializer.TryDeserialize(Utf8Stream(TwoDocuments, mark), out object? v3, limit13));
        Assert.Null(v3);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharpYaml_a_text_exactly_at_the_limit_still_gives_its_first_document(bool mark)
    {
        string text = (mark ? "\uFEFF" : "") + TwoDocuments;
        Cs.YamlSerializerOptions limit14 = Limited(14);

        foreach (object? value in new[]
        {
            Cs.YamlSerializer.Deserialize<object>(text, limit14),
            Cs.YamlSerializer.Deserialize<object>(text.AsSpan(), limit14),
            Cs.YamlSerializer.Deserialize<object>(new StringReader(text), limit14),
            Cs.YamlSerializer.Deserialize<object>(Utf8Stream(TwoDocuments, mark), limit14),
            Cs.YamlSerializer.Deserialize(text, Target, limit14),
        })
        {
            var map = Assert.IsAssignableFrom<System.Collections.IDictionary>(value);
            Assert.Single(map);
        }
    }

    [Fact]
    public void SharpYaml_the_refusal_names_the_source()
    {
        var options = new Cs.YamlSerializerOptions { Limits = Oy.YamlLimits.Default with { MaxInputBytes = 13 }, SourceName = "doc.yaml" };
        var error = Assert.IsAssignableFrom<Cs.YamlException>(Record.Exception(() => Cs.YamlSerializer.Deserialize<object>(TwoDocuments, options)));
        Assert.Equal("doc.yaml", error.SourceName);
        Assert.Contains("MaxInputBytes", error.Message, StringComparison.Ordinal);
    }

    // ---- SharpYaml: marks after a leading mark ----

    public static TheoryData<string> BadTexts() =>
    [
        "\tx: 1\n",
        "a: b: c\n",
        "a: 'x\n",
        "[a, b: [\n",
        "é: ö: 1\n",
        "a: 1\n\tb: 2\n",
    ];

    private static Cs.Mark MarkOf(Func<object?> read) =>
        Assert.IsAssignableFrom<Cs.YamlException>(Record.Exception(read)).Start;

    [Theory]
    [MemberData(nameof(BadTexts))]
    public void SharpYaml_an_error_has_the_same_mark_with_a_leading_byte_order_mark_for_every_input(string text)
    {
        Cs.Mark plain = MarkOf(() => Cs.YamlSerializer.Deserialize<object>(text));

        Assert.Equal(plain, MarkOf(() => Cs.YamlSerializer.Deserialize<object>("\uFEFF" + text)));
        Assert.Equal(plain, MarkOf(() => Cs.YamlSerializer.Deserialize<object>(("\uFEFF" + text).AsSpan())));
        Assert.Equal(plain, MarkOf(() => Cs.YamlSerializer.Deserialize("\uFEFF" + text, Target)));
        Assert.Equal(plain, MarkOf(() => Cs.YamlSerializer.Deserialize<object>(new StringReader("\uFEFF" + text))));
        Assert.Equal(plain, MarkOf(() => Cs.YamlSerializer.Deserialize<object>(Utf8Stream(text, true))));
    }

    [Theory]
    [MemberData(nameof(BadTexts))]
    public void SharpYaml_an_error_in_the_first_of_two_documents_has_the_same_mark_with_a_leading_byte_order_mark(string text)
    {
        string withSecond = text + "---\nz: 1\n";
        Cs.Mark plain = MarkOf(() => Cs.YamlSerializer.Deserialize<object>(withSecond));
        Assert.Equal(plain, MarkOf(() => Cs.YamlSerializer.Deserialize<object>("\uFEFF" + withSecond)));
    }

    [Fact]
    public void SharpYaml_a_directive_after_a_leading_mark_still_gives_the_first_document()
    {
        const string text = "%YAML 1.2\n---\na: 1\n---\nb: 2\n";

        foreach (string input in new[] { text, "\uFEFF" + text })
        {
            var map = Assert.IsAssignableFrom<System.Collections.IDictionary>(Cs.YamlSerializer.Deserialize<object>(input));
            Assert.Equal(["a"], map.Keys.Cast<object>().Select(k => (string)k));
            Assert.Equal(1L, map["a"]);
        }
    }

    // Directives, comments and document ends around the first document: a leading mark must not change which part is read, what
    // it gives, or where an error is. The first three texts are the ones that once failed after a mark.
    public static TheoryData<string> DirectiveTexts() =>
    [
        "%YAML 1.2\n---\na: 1\n...\n%TAG ! tag:x,2000:\n---\nb: 2\n",
        "# c😀\n---\n&a x\n*a\n!!str \"x😀\n- x\n{a: [b\n",
        "# c😀\n\n...\n- x\n\t",
        "a: 1\n...\n# note\n%YAML 1.2\n---\nb\n",
        "a: 1\n%YAML 1.2\n---\nb: 2\n",
        "a: 1\nc: 2\n%YAML 1.2\n---\nb: 2\n",
        "a\n%x\n---\nb\n",
    ];

    private static string Outcome(Func<object?> read)
    {
        try
        {
            return Differ.Dump(read());
        }
        catch (Cs.YamlException error)
        {
            return $"{error.GetType().Name} {error.Start}";
        }
    }

    [Theory]
    [MemberData(nameof(DirectiveTexts))]
    public void SharpYaml_a_text_gives_the_same_outcome_with_a_leading_byte_order_mark_when_it_has_directives_and_document_ends(string text)
    {
        string bom = "\uFEFF" + text;
        string expected = Outcome(() => Cs.YamlSerializer.Deserialize<object>(text));

        Assert.Equal(expected, Outcome(() => Cs.YamlSerializer.Deserialize<object>(bom)));
        Assert.Equal(expected, Outcome(() => Cs.YamlSerializer.Deserialize<object>(bom.AsSpan())));
        Assert.Equal(expected, Outcome(() => Cs.YamlSerializer.Deserialize(bom, Target)));
        Assert.Equal(expected, Outcome(() => Cs.YamlSerializer.Deserialize<object>(new StringReader(bom))));
    }

    [Fact]
    public void SharpYaml_a_percent_line_after_a_block_collection_is_cut_off_after_a_leading_mark_too()
    {
        // The cut is placed from the error's offset, which does not count the mark; the first column of a line is where an
        // off-by-one would land on the line before.
        foreach (string text in new[] { "a: 1\n%YAML 1.2\n---\nb: 2\n", "a: 1\nc: 2\n%YAML 1.2\n---\nb: 2\n" })
        {
            var map = Assert.IsAssignableFrom<System.Collections.IDictionary>(Cs.YamlSerializer.Deserialize<object>("\uFEFF" + text));
            Assert.Equal(1L, map["a"]);
            Assert.DoesNotContain("b", map.Keys.Cast<object>());
        }
    }

    // ---- SharpYaml: counting a very long text ----

    [Fact]
    [Trait("Category", "Slow")]
    public void SharpYaml_a_text_with_more_bytes_than_an_int_holds_is_counted_in_a_long()
    {
        // 720 million three-byte characters: 2.16 billion bytes, which an int cannot hold.
        string text = "a: 1\n---\n" + new string('中', 720_000_000);
        var none = new Cs.YamlSerializerOptions { Limits = Oy.YamlLimits.None };
        var map = Assert.IsAssignableFrom<System.Collections.IDictionary>(Cs.YamlSerializer.Deserialize<object>(text, none));
        Assert.Equal(1L, map["a"]);

        // Under the limit by characters but over it by bytes: counted, and refused as a limit error, not an argument error.
        Cs.YamlSerializerOptions bytesOver = Limited(1_500_000_000);
        Assert.IsAssignableFrom<Cs.YamlException>(Record.Exception(() => Cs.YamlSerializer.Deserialize<object>(text, bytesOver)));
        Assert.IsAssignableFrom<Cs.YamlException>(Record.Exception(() => Cs.YamlSerializer.Deserialize<object>(text.AsSpan(), bytesOver)));
        Assert.IsAssignableFrom<Cs.YamlException>(Record.Exception(() => Cs.YamlSerializer.Deserialize(text, Target, bytesOver)));
        Assert.False(Cs.YamlSerializer.TryDeserialize(text, out object? value, bytesOver));
        Assert.Null(value);

        // Over the limit by characters alone.
        Cs.YamlSerializerOptions charsOver = Limited(64L * 1024 * 1024);
        Assert.IsAssignableFrom<Cs.YamlException>(Record.Exception(() => Cs.YamlSerializer.Deserialize<object>(text, charsOver)));
        Assert.False(Cs.YamlSerializer.TryDeserialize(text, out object? value2, charsOver));
        Assert.Null(value2);

        // Room for three bytes per character: not counted at all, and the first document is read.
        var roomy = Limited(2_200_000_000);
        Assert.IsAssignableFrom<System.Collections.IDictionary>(Cs.YamlSerializer.Deserialize<object>(text, roomy));
    }

    [Fact]
    public void SharpYaml_the_stream_refusal_names_the_source()
    {
        var options = new Cs.YamlSerializerOptions { Limits = Oy.YamlLimits.Default with { MaxInputBytes = 13 }, SourceName = "doc.yaml" };
        var error = Assert.IsAssignableFrom<Cs.YamlException>(Record.Exception(() => Cs.YamlSerializer.Deserialize<object>(Utf8Stream(TwoDocuments, false), options)));
        Assert.Equal("doc.yaml", error.SourceName);
    }

    // ---- YamlDotNet: marks do not depend on a leading mark ----

    private static string Shape(YdnRep.YamlStream stream) =>
        string.Join(";", stream.Documents.SelectMany(d => d.AllNodes).Select(n => $"{n.Start}>{n.End}"));

    private static string Outcome(Func<TextReader> open)
    {
        var stream = new YdnRep.YamlStream();
        try
        {
            stream.Load(open());
            return Shape(stream);
        }
        catch (YdnCore.YamlException error)
        {
            return $"error {error.Start}>{error.End}";
        }
    }

    [Theory]
    [InlineData("a: 1\nb: [x, y]\n")]
    [InlineData("é: ö\nk: [1, 2]\n")]
    [InlineData("[a, b, {c: d}]\n")]
    [InlineData("a: 1\n---\nb: 2\n")]
    [InlineData("\tx: 1\n")]
    [InlineData("a: b: c\n")]
    [InlineData("a: 'x\n")]
    [InlineData("[a, b: [\n")]
    [InlineData("é: ö: 1\n")]
    [InlineData("a: 1\n\tb: 2\n")]
    public void YamlDotNet_nodes_and_errors_have_the_same_marks_with_a_leading_byte_order_mark(string text)
    {
        string expected = Outcome(() => new StringReader(text));

        Assert.Equal(expected, Outcome(() => new StringReader("\uFEFF" + text)));
        Assert.Equal(expected, Outcome(() => new StreamReader(Utf8Stream(text, true), Encoding.UTF8, true)));
    }
}
