#pragma warning disable CA2263 // The tests call the overloads that take a type on purpose.
using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;
using Cs = global::OneYaml.Compat.SharpYaml;
using CsS = global::OneYaml.Compat.SharpYaml.Serialization;
using Rs = global::SharpYaml;

namespace OneYaml.Compat.Tests.SharpYamlCompat;

// The entry points of the serializer: every overload family, the options, and the errors.
public class SharpYamlApiTests
{
    private const string Yaml = "Name: Ada\nAge: 37\nTags: [a, b]\n";

    private static Plain Expected() => Rs.YamlSerializer.Deserialize<Plain>(Yaml)!;

    private static void SameAsReal(Plain? got) => Assert.Equal(Differ.Dump(Expected()), Differ.Dump(got));

    [Fact]
    public void String_stream_reader_and_span_inputs_agree()
    {
        SameAsReal(Cs.YamlSerializer.Deserialize<Plain>(Yaml));
        SameAsReal(Cs.YamlSerializer.Deserialize<Plain>(Yaml.AsSpan()));
        SameAsReal(Cs.YamlSerializer.Deserialize<Plain>(new StringReader(Yaml)));
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(Yaml));
        SameAsReal(Cs.YamlSerializer.Deserialize<Plain>(ms));
        Assert.True(ms.CanRead, "the stream stays open");
        SameAsReal((Plain?)Cs.YamlSerializer.Deserialize(Yaml, typeof(Plain)));
        SameAsReal((Plain?)Cs.YamlSerializer.Deserialize(Yaml.AsSpan(), typeof(Plain)));
        SameAsReal((Plain?)Cs.YamlSerializer.Deserialize(new StringReader(Yaml), typeof(Plain)));
        using var ms2 = new MemoryStream(Encoding.UTF8.GetBytes(Yaml));
        SameAsReal((Plain?)Cs.YamlSerializer.Deserialize(ms2, typeof(Plain)));
    }

    [Fact]
    public void A_byte_order_mark_is_dropped()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(Yaml)];
        SameAsReal(Cs.YamlSerializer.Deserialize<Plain>(new MemoryStream(bytes)));
        SameAsReal(Rs.YamlSerializer.Deserialize<Plain>(new MemoryStream(bytes)));
    }

    [Fact]
    public void Writers_produce_the_same_text()
    {
        Plain value = SyHelp.Sample();
        string text = Cs.YamlSerializer.Serialize(value);
        var sw = new StringWriter();
        Cs.YamlSerializer.Serialize(sw, value);
        Assert.Equal(text, sw.ToString());
        using var ms = new MemoryStream();
        Cs.YamlSerializer.Serialize(ms, value);
        Assert.Equal(text, Encoding.UTF8.GetString(ms.ToArray()));
        Assert.Equal(0, ms.ToArray().Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }) ? 1 : 0);
        var buffer = new ArrayBufferWriter<char>();
        Cs.YamlSerializer.Serialize(buffer, value);
        Assert.Equal(text, buffer.WrittenSpan.ToString());
        Assert.Equal(text, Cs.YamlSerializer.Serialize(value, typeof(Plain)));
        var sw2 = new StringWriter();
        Cs.YamlSerializer.Serialize(sw2, value, typeof(Plain));
        Assert.Equal(text, sw2.ToString());
        var buffer2 = new ArrayBufferWriter<char>();
        Cs.YamlSerializer.Serialize(buffer2, value, typeof(Plain));
        Assert.Equal(text, buffer2.WrittenSpan.ToString());
        using var ms2 = new MemoryStream();
        Cs.YamlSerializer.Serialize(ms2, value, typeof(Plain));
        Assert.Equal(text, Encoding.UTF8.GetString(ms2.ToArray()));
    }

    [Fact]
    public void Serialize_as_a_base_type_writes_that_type()
    {
        object value = new Inherited { Name = "n", Extra = "e" };
        Assert.Equal(
            Rs.YamlSerializer.Serialize(value, typeof(Plain)).Contains("Extra", StringComparison.Ordinal),
            Cs.YamlSerializer.Serialize(value, typeof(Plain)).Contains("Extra", StringComparison.Ordinal));
        Assert.Equal(
            Rs.YamlSerializer.Serialize(value).Contains("Extra", StringComparison.Ordinal),
            Cs.YamlSerializer.Serialize(value).Contains("Extra", StringComparison.Ordinal));
    }

    [Fact]
    public void TryDeserialize_tells_success_from_a_yaml_error()
    {
        Assert.True(Cs.YamlSerializer.TryDeserialize("5", out int five));
        Assert.Equal(5, five);
        Assert.False(Cs.YamlSerializer.TryDeserialize("x", out int none));
        Assert.Equal(0, none);
        Assert.False(Cs.YamlSerializer.TryDeserialize("[1", out List<int>? broken));
        Assert.Null(broken);
        Assert.True(Cs.YamlSerializer.TryDeserialize("5", typeof(int), out object? boxed));
        Assert.Equal(5, boxed);
        Assert.False(Cs.YamlSerializer.TryDeserialize("a: [", typeof(Plain), out object? nothing));
        Assert.Null(nothing);
        Assert.True(Cs.YamlSerializer.TryDeserialize(new StringReader("5"), out int fromReader));
        Assert.Equal(5, fromReader);
        Assert.False(Cs.YamlSerializer.TryDeserialize(new MemoryStream("x"u8.ToArray()), out int fromStream));
        Assert.Equal(0, fromStream);
    }

    [Fact]
    public void Null_arguments_are_refused_the_usual_way()
    {
        Assert.Throws<ArgumentNullException>(() => Cs.YamlSerializer.Deserialize<int>((string)null!));
        Assert.Throws<ArgumentNullException>(() => Cs.YamlSerializer.Deserialize<int>((TextReader)null!));
        Assert.Throws<ArgumentNullException>(() => Cs.YamlSerializer.Deserialize<int>((Stream)null!));
        Assert.Throws<ArgumentNullException>(() => Cs.YamlSerializer.Deserialize("1", (Type)null!));
        Assert.Throws<ArgumentNullException>(() => Cs.YamlSerializer.Serialize((TextWriter)null!, 1));
        Assert.Throws<ArgumentNullException>(() => Cs.YamlSerializer.Serialize((Stream)null!, 1));
        Assert.Throws<ArgumentNullException>(() => Cs.YamlSerializer.Serialize(1, (Type)null!));
        Assert.Null(Cs.YamlSerializer.Deserialize<string>("~"));
    }

    [Fact]
    public void A_text_with_several_documents_gives_the_first()
    {
        string text = "a: 1\n---\nb: 2\n";
        Assert.Equal(Differ.Dump(Rs.YamlSerializer.Deserialize<Dictionary<string, int>>(text)), Differ.Dump(Cs.YamlSerializer.Deserialize<Dictionary<string, int>>(text)));
        Assert.Equal(Differ.Dump(Rs.YamlSerializer.Deserialize<object>("--- a\n--- b\n")), Differ.Dump(Cs.YamlSerializer.Deserialize<object>("--- a\n--- b\n")));
        Assert.Equal("a", Cs.YamlSerializer.Deserialize("--- a\n--- b\n", typeof(string)));
        Assert.Equal(1, Cs.YamlSerializer.Deserialize<Dictionary<string, int>>("a: 1\n---\nb: [\n")!["a"]);
    }

    [Theory]
    [InlineData("a: 1\n---\nb: 2\n")]
    [InlineData("--- a\n--- b\n")]
    [InlineData("---\na: 1\n---\nb: 2\n---\nc: 3\n")]
    [InlineData("%YAML 1.2\n---\na: 1\n...\n%YAML 1.2\n---\nb: 2\n")]
    [InlineData("a: 1\n...\n# only a comment follows\n")]
    [InlineData("# c\n--- \na: 1\n# between\n%TAG ! tag:x:\n---\n!x b\n")]
    [InlineData("a: 1\r\n---\r\nb: 2\r\n")]
    [InlineData("a: 1\n---\n[\n")]
    [InlineData("a: |\n  text\n  ---\n  more\n")]
    [InlineData("a: '---'\nb: ...\n")]
    [InlineData("---\n---\n")]
    [InlineData("---\n...\n---\nx\n")]
    [InlineData("a: 1\n---x\n")]
    public void The_first_document_is_read_as_the_real_library_reads_it(string text)
    {
        Assert.Equal(SyHelp.Outcome(() => Rs.YamlSerializer.Deserialize<object>(text)), SyHelp.Outcome(() => Cs.YamlSerializer.Deserialize<object>(text)));
    }

    [Fact]
    public void A_bare_document_after_an_end_marker_is_a_second_document()
    {
        // The real library refuses this text (a spec-valid one); the package reads the first document.
        Assert.Equal(1L, Cs.YamlSerializer.Deserialize<Dictionary<string, object>>("a: 1\n...\nb: 2\n")!["a"]);
    }

    [Fact]
    public void Options_have_the_defaults_of_the_real_library()
    {
        var real = new Rs.YamlSerializerOptions();
        var compat = new Cs.YamlSerializerOptions();
        Assert.Equal(real.PropertyNamingPolicy, compat.PropertyNamingPolicy);
        Assert.Equal(real.WriteIndented, compat.WriteIndented);
        Assert.Equal(real.IndentSize, compat.IndentSize);
        Assert.Equal(real.PropertyNameCaseInsensitive, compat.PropertyNameCaseInsensitive);
        Assert.Equal(real.UseSchema, compat.UseSchema);
        Assert.Equal(real.MaxDepth, compat.MaxDepth);
        Assert.Equal(real.BlockSequenceMappingStyle.ToString(), compat.BlockSequenceMappingStyle.ToString());
        Assert.Equal(real.BlockSequenceSequenceStyle.ToString(), compat.BlockSequenceSequenceStyle.ToString());
        Assert.Equal(real.DefaultIgnoreCondition.ToString(), compat.DefaultIgnoreCondition.ToString());
        Assert.Equal(real.MappingOrder.ToString(), compat.MappingOrder.ToString());
        Assert.Equal(real.DuplicateKeyHandling.ToString(), compat.DuplicateKeyHandling.ToString());
        Assert.Equal(real.Schema.ToString(), compat.Schema.ToString());
        Assert.Equal(real.ReferenceHandling.ToString(), compat.ReferenceHandling.ToString());
        Assert.Equal(real.UnmappedMemberHandling, compat.UnmappedMemberHandling);
        Assert.Equal(real.PreferredObjectCreationHandling, compat.PreferredObjectCreationHandling);
        Assert.Same(Cs.YamlSerializerOptions.Default, Cs.YamlSerializerOptions.Default);
        Assert.Equal(Enum.GetNames<Rs.YamlIgnoreCondition>(), Enum.GetNames<Cs.YamlIgnoreCondition>());
        Assert.Equal(Enum.GetNames<Rs.YamlSchemaKind>(), Enum.GetNames<Cs.YamlSchemaKind>());
        Assert.Equal(Enum.GetNames<Rs.YamlDuplicateKeyHandling>(), Enum.GetNames<Cs.YamlDuplicateKeyHandling>());
        Assert.Equal(Enum.GetNames<Rs.YamlReferenceHandling>(), Enum.GetNames<Cs.YamlReferenceHandling>());
        Assert.Equal(Enum.GetNames<Rs.YamlMappingOrderPolicy>(), Enum.GetNames<Cs.YamlMappingOrderPolicy>());
        Assert.Equal(Enum.GetNames<Rs.YamlSequenceItemStyle>(), Enum.GetNames<Cs.YamlSequenceItemStyle>());
        Assert.Equal(Enum.GetNames<Rs.YamlStyle>(), Enum.GetNames<Cs.YamlStyle>());
    }

    [Fact]
    public void An_indent_size_changes_the_text_and_the_text_still_reads()
    {
        Plain value = SyHelp.Sample();
        foreach (int size in new[] { 1, 2, 3, 4, 8 })
        {
            string text = Cs.YamlSerializer.Serialize(value, new Cs.YamlSerializerOptions { IndentSize = size });
            Assert.Equal(Differ.Dump(value), Differ.Dump(Cs.YamlSerializer.Deserialize<Plain>(text)));
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => new Cs.YamlSerializerOptions { IndentSize = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new Cs.YamlSerializerOptions { IndentSize = 9 });
    }

    [Fact]
    public void WriteIndented_false_still_writes_valid_yaml()
    {
        // The real library writes the nested lines flush left, which is not YAML that reads back.
        Plain value = SyHelp.Sample();
        string text = Cs.YamlSerializer.Serialize(value, new Cs.YamlSerializerOptions { WriteIndented = false });
        Assert.Equal(Differ.Dump(value), Differ.Dump(Cs.YamlSerializer.Deserialize<Plain>(text)));
    }

    [Fact]
    public void Sorted_order_sorts_by_the_written_key()
    {
        string text = Cs.YamlSerializer.Serialize(new Attributed(), new Cs.YamlSerializerOptions { MappingOrder = Cs.YamlMappingOrderPolicy.Sorted, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        string[] keys = [.. text.Split('\n').Where(l => l.Length > 0 && l[0] != ' ').Select(l => l.Split(':')[0])];
        Assert.Equal([.. keys.OrderBy(k => k, StringComparer.Ordinal)], keys);
        Assert.Equal(Differ.Dump(new Attributed()), Differ.Dump(Cs.YamlSerializer.Deserialize<Attributed>(text, new Cs.YamlSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })));
    }

    [Fact]
    public void Options_the_engine_cannot_honour_are_refused_loudly()
    {
        Assert.Throws<NotSupportedException>(() => Cs.YamlSerializer.Serialize(1, new Cs.YamlSerializerOptions { DictionaryKeyPolicy = JsonNamingPolicy.CamelCase }));
        Assert.Throws<NotSupportedException>(() => Cs.YamlSerializer.Serialize(1, new Cs.YamlSerializerOptions { DuplicateKeyHandling = Cs.YamlDuplicateKeyHandling.LastWins }));
        Assert.Throws<NotSupportedException>(() => Cs.YamlSerializer.Serialize(1, new Cs.YamlSerializerOptions { PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate }));
        Assert.Throws<NotSupportedException>(() => Cs.YamlSerializer.Serialize(1, new Cs.YamlSerializerOptions { UseSchema = true, Schema = Cs.YamlSchemaKind.Json }));
        Assert.Throws<NotSupportedException>(() => Cs.YamlSerializer.Serialize(1, new Cs.YamlSerializerOptions { UnsafeAllowDeserializeFromTagTypeName = true }));
        Assert.Throws<NotSupportedException>(() => Cs.YamlSerializer.Serialize(1, new Cs.YamlSerializerOptions { DefaultIgnoreCondition = Cs.YamlIgnoreCondition.Always }));

        // Options at their defaults, and the ones that change nothing here, are accepted.
        _ = Cs.YamlSerializer.Serialize(1, new Cs.YamlSerializerOptions
        {
            BlockSequenceMappingStyle = Cs.YamlSequenceItemStyle.Compact,
            BlockSequenceSequenceStyle = Cs.YamlSequenceItemStyle.Expanded,
            ScalarStylePreferences = new Cs.YamlScalarStylePreferences(),
            ReferenceHandling = Cs.YamlReferenceHandling.None,
            TypeInfoResolver = Cs.ReflectionYamlTypeInfoResolver.Default,
            SourceName = "x.yaml",
        });
    }

    [Fact]
    public void Type_infos_serialize_and_read()
    {
        var options = new Cs.YamlSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        Cs.YamlTypeInfo info = Cs.ReflectionYamlTypeInfoResolver.Default.GetTypeInfo(typeof(Plain), options)!;
        Assert.Equal(typeof(Plain), info.Type);
        Assert.Same(options, info.Options);
        var typed = (Cs.YamlTypeInfo<Plain>)info;
        string text = Cs.YamlSerializer.Serialize(new Plain { Name = "Ada" }, typed);
        Assert.StartsWith("name: Ada", text, StringComparison.Ordinal);
        Assert.Equal("Ada", Cs.YamlSerializer.Deserialize(text, typed)!.Name);
        Assert.Equal("Ada", ((Plain)Cs.YamlSerializer.Deserialize(text, info)!).Name);
        Assert.Equal(text, Cs.YamlSerializer.Serialize((object)new Plain { Name = "Ada" }, info));
    }

    private sealed class NamedContext : CsS.YamlSerializerContext
    {
        public NamedContext()
            : base(new Cs.YamlSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
        {
        }
    }

    [Fact]
    public void A_context_carries_options()
    {
        var context = new NamedContext();
        string text = Cs.YamlSerializer.Serialize(new Plain { Name = "Ada" }, context);
        Assert.StartsWith("name: Ada", text, StringComparison.Ordinal);
        Assert.Equal("Ada", Cs.YamlSerializer.Deserialize<Plain>(text, context)!.Name);
        Assert.Equal("Ada", ((Plain)Cs.YamlSerializer.Deserialize(text, typeof(Plain), context)!).Name);
        Assert.True(Cs.YamlSerializer.TryDeserialize(text, context, out Plain? read));
        Assert.Equal("Ada", read!.Name);
        Cs.YamlSerializerOptions other = context.CreateOptions(o => new Cs.YamlSerializerOptions(o) { IndentSize = 4 });
        Assert.Equal(4, other.IndentSize);
        Assert.Equal(JsonNamingPolicy.CamelCase, other.PropertyNamingPolicy);
    }

    [Fact]
    public void Errors_are_the_exception_types_of_the_real_library()
    {
        foreach (string text in new[] { "a: b: c", "[1, 2", "a: 'x", "\ta: 1", "a: *missing", "- - [", "a: {b", "? ? a", "a:\n  b: 1\n c: 2\n" })
        {
            Exception real = Assert.ThrowsAny<Exception>(() => Rs.YamlSerializer.Deserialize<object>(text));
            Exception compat = Assert.ThrowsAny<Exception>(() => Cs.YamlSerializer.Deserialize<object>(text));
            Assert.IsAssignableFrom<Cs.YamlException>(compat);
            Assert.True(real is Rs.YamlException, text);
        }

        var error = Assert.Throws<Cs.SyntaxErrorException>(() => Cs.YamlSerializer.Deserialize<object>("a: b: c"));

        // The place is close to where the real library puts it, not always the same character.
        Assert.Equal(0, error.Start.Line);
        Assert.InRange(error.Start.Column, 3, 6);
        Assert.Equal(error.Start.Column, error.Start.Index);
        Assert.StartsWith($"({error.Start})", error.Message, StringComparison.Ordinal);

        // Lines count from zero and columns count characters, however many bytes a character takes.
        var wide = Assert.ThrowsAny<Cs.YamlException>(() => Cs.YamlSerializer.Deserialize<object>("é: 1\né: [\n"));
        Assert.InRange(wide.Start.Line, 1, 2);
        Assert.True(wide.Start.Index >= 5, wide.Message);

        var named = Assert.ThrowsAny<Cs.YamlException>(() => Cs.YamlSerializer.Deserialize<object>("a: b: c", new Cs.YamlSerializerOptions { SourceName = "app.yaml" }));
        Assert.Equal("app.yaml", named.SourceName);
        Assert.StartsWith("app.yaml: ", named.Message, StringComparison.Ordinal);

        var binding = Assert.Throws<Cs.YamlException>(() => Cs.YamlSerializer.Deserialize<Plain>("Age: x"));
        Assert.Equal(0, binding.Start.Line);
        Assert.InRange(binding.Start.Column, 4, 6);
    }

    [Fact]
    public void Exception_types_can_be_made_like_the_real_ones()
    {
        var start = new Cs.Mark(1, 2, 3);
        var end = new Cs.Mark(4, 5, 6);
        var made = new Cs.YamlException("file", start, end, "msg");
        var real = new Rs.YamlException("file", new Rs.Mark(1, 2, 3), new Rs.Mark(4, 5, 6), "msg");
        Assert.Equal(real.Message, made.Message);
        Assert.Equal(real.Start.ToString(), made.Start.ToString());
        Assert.Equal(new Rs.Mark(1, 2, 3).Index, start.Index);
        Assert.Equal(real.SourceName, made.SourceName);
        Assert.Equal(new Rs.YamlException("plain").Message, new Cs.YamlException("plain").Message);
        Assert.Equal(new Rs.YamlException(new Rs.Mark(1, 2, 3), new Rs.Mark(4, 5, 6), "m").Message, new Cs.YamlException(start, end, "m").Message);
        Assert.Equal(Rs.Mark.Empty.ToString(), Cs.Mark.Empty.ToString());
        Assert.IsAssignableFrom<Cs.YamlException>(new Cs.SyntaxErrorException(start, end, "m"));
        Assert.IsAssignableFrom<Cs.YamlException>(new Cs.SemanticErrorException(start, end, "m"));
    }

    [Fact]
    public void Attributes_exist_with_the_same_shape()
    {
        Assert.Equal("n", new CsS.YamlPropertyNameAttribute("n").Name);
        Assert.Equal(Cs.YamlIgnoreCondition.Always, new CsS.YamlIgnoreAttribute().Condition);
        Assert.Equal(Rs.YamlIgnoreCondition.Always, new Rs.Serialization.YamlIgnoreAttribute().Condition);
        Assert.Equal(3, new CsS.YamlPropertyOrderAttribute(3).Order);
        Assert.Equal(typeof(int), new CsS.YamlSerializableAttribute(typeof(int)).Type);
        Assert.Null(new CsS.YamlSerializableAttribute(typeof(int)).TypeInfoPropertyName);
        Assert.Equal(Cs.YamlSequenceItemStyle.Expanded, new CsS.YamlBlockSequenceItemStyleAttribute(Cs.YamlSequenceItemStyle.Expanded).MappingStyle);
        _ = new CsS.YamlRequiredAttribute();
        _ = new CsS.YamlIncludeAttribute();
        _ = new CsS.YamlConstructorAttribute();
        Assert.True(typeof(CsS.YamlAttribute).IsAbstract);
    }
}
