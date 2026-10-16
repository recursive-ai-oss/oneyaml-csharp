using System.Text;
using Xunit;
using Cs = global::OneYaml.Compat.SharpYaml;
using YdnCore = global::OneYaml.Compat.YamlDotNet.Core;
using YdnSer = global::OneYaml.Compat.YamlDotNet.Serialization;

namespace OneYaml.Compat.Tests;

// Shared alias targets are charged to the limits on every use, and the limits a caller sets apply to every way of
// handing the input over.
public class SharedAliasAndInputLimitTests
{
    // Level 0 holds `width` scalars; each next level holds `width` aliases of the one before: width^levels in all.
    private static string Bomb(int levels, int width)
    {
        var sb = new StringBuilder("a0: &a0 [" + string.Join(", ", Enumerable.Repeat("x", width)) + "]\n");
        for (int i = 1; i <= levels; i++)
        {
            sb.Append('a').Append(i).Append(": &a").Append(i).Append(" [").Append(string.Join(", ", Enumerable.Repeat("*a" + (i - 1), width))).Append("]\n");
        }

        return sb.ToString();
    }

    public sealed class Chain
    {
        public List<object>? a0 { get; set; }

        public List<object>? a1 { get; set; }

        public List<object>? a2 { get; set; }

        public List<object>? a3 { get; set; }

        public List<object>? a4 { get; set; }

        public List<object>? a5 { get; set; }

        public List<object>? a6 { get; set; }

        public List<object>? a7 { get; set; }

        public List<object>? a8 { get; set; }

        public List<object>? a9 { get; set; }
    }

    [Fact]
    public void YamlDotNet_a_billion_laughs_input_is_refused_under_default_and_untrusted_limits()
    {
        string yaml = Bomb(9, 9);
        foreach (YdnSer.IDeserializer deserializer in new[]
        {
            new YdnSer.DeserializerBuilder().Build(),
            new YdnSer.DeserializerBuilder().WithLimits(global::OneYaml.YamlLimits.Untrusted).Build(),
        })
        {
            Exception ex = Assert.ThrowsAny<YdnCore.YamlException>(() => deserializer.Deserialize<object>(yaml));
            Assert.Contains("expands to more than the configured limit", ex.Message, StringComparison.Ordinal);
            Assert.ThrowsAny<YdnCore.YamlException>(() => deserializer.Deserialize<Dictionary<string, List<object>>>(yaml));
            Assert.ThrowsAny<YdnCore.YamlException>(() => deserializer.Deserialize<Chain>(yaml));
        }
    }

    [Fact]
    public void YamlDotNet_a_small_shared_alias_still_gives_the_same_instance()
    {
        YdnSer.IDeserializer deserializer = new YdnSer.DeserializerBuilder().WithLimits(global::OneYaml.YamlLimits.Untrusted).Build();

        var map = deserializer.Deserialize<Dictionary<string, object>>("a: &x [1, 2]\nb: *x\nc: *x\n")!;
        Assert.Same(map["a"], map["b"]);
        Assert.Same(map["a"], map["c"]);

        var chain = deserializer.Deserialize<Chain>(Bomb(2, 3))!;
        Assert.Same(chain.a0, chain.a1![0]);
        Assert.Same(chain.a1, chain.a2![2]);
    }

    [Fact]
    public void YamlDotNet_a_parser_made_without_limits_is_checked_against_the_deserializers_limits()
    {
        var limits = new global::OneYaml.YamlLimits { MaxInputBytes = 40, MaxScalarBytes = 8, MaxDocuments = 1 };
        YdnSer.IDeserializer strict = new YdnSer.DeserializerBuilder().WithLimits(limits).Build();

        // Over the input limit, though the parser read it without limits.
        var tooLong = new YdnCore.Parser(new StringReader("a: " + new string('x', 60) + "\n"));
        Assert.ThrowsAny<YdnCore.YamlException>(() => strict.Deserialize<Dictionary<string, string>>(tooLong));

        // Under the input limit, over the scalar limit.
        var bigScalar = new YdnCore.Parser(new StringReader("a: " + new string('x', 20) + "\n"));
        Assert.ThrowsAny<YdnCore.YamlException>(() => strict.Deserialize<Dictionary<string, string>>(bigScalar));

        // Too many documents.
        var documents = new YdnCore.Parser(new StringReader("a: 1\n---\nb: 2\n"));
        Assert.ThrowsAny<YdnCore.YamlException>(() => strict.Deserialize<Dictionary<string, string>>(documents));

        // Input within every limit is read.
        var fine = new YdnCore.Parser(new StringReader("a: b\n"));
        Assert.Equal("b", strict.Deserialize<Dictionary<string, string>>(fine)!["a"]);
    }

    private sealed class EndlessStream : Stream
    {
        public long Read1 { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Fill(buffer, (byte)'#', offset, count);
            Read1 += count;
            return count;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class EndlessReader : TextReader
    {
        public long Read1 { get; private set; }

        public override int Read(char[] buffer, int index, int count)
        {
            Array.Fill(buffer, '#', index, count);
            Read1 += count;
            return count;
        }
    }

    [Fact]
    public void SharpYaml_a_stream_or_reader_is_read_only_as_far_as_the_input_limit_allows()
    {
        var options = new Cs.YamlSerializerOptions { Limits = new global::OneYaml.YamlLimits { MaxInputBytes = 1000 } };

        var stream = new EndlessStream();
        Assert.ThrowsAny<Cs.YamlException>(() => Cs.YamlSerializer.Deserialize<object>(stream, options));
        Assert.InRange(stream.Read1, 1001, 1000 + 16384);

        var reader = new EndlessReader();
        Assert.ThrowsAny<Cs.YamlException>(() => Cs.YamlSerializer.Deserialize<object>(reader, options));
        Assert.InRange(reader.Read1, 1001, 1000 + 16384);

        var tryStream = new EndlessStream();
        Assert.False(Cs.YamlSerializer.TryDeserialize(tryStream, out object? _, options));
        Assert.InRange(tryStream.Read1, 1001, 1000 + 16384);
        var tryReader = new EndlessReader();
        Assert.False(Cs.YamlSerializer.TryDeserialize(tryReader, out object? _, options));
        Assert.InRange(tryReader.Read1, 1001, 1000 + 16384);

        // Within the limit the same calls read.
        Assert.Equal("1", Cs.YamlSerializer.Deserialize<Dictionary<string, string>>(new MemoryStream(Encoding.UTF8.GetBytes("k: 1\n")), options)!["k"]);
        Assert.Equal("1", Cs.YamlSerializer.Deserialize<Dictionary<string, string>>(new StringReader("k: 1\n"), options)!["k"]);
    }

    [Fact]
    public void SharpYaml_layout_options_are_refused_when_writing_and_do_not_stop_reading()
    {
        Cs.YamlSerializerOptions[] shared =
        [
            new() { BlockSequenceMappingStyle = Cs.YamlSequenceItemStyle.Expanded },
            new() { BlockSequenceSequenceStyle = Cs.YamlSequenceItemStyle.Compact },
            new() { ScalarStylePreferences = new Cs.YamlScalarStylePreferences { PreferPlainStyle = true } },
            new() { ScalarStylePreferences = new Cs.YamlScalarStylePreferences { PreferQuotedForAmbiguousScalars = true } },
        ];
        string[] names = ["BlockSequenceMappingStyle", "BlockSequenceSequenceStyle", "ScalarStylePreferences", "ScalarStylePreferences"];

        for (int i = 0; i < shared.Length; i++)
        {
            Cs.YamlSerializerOptions options = shared[i];
            var read = Cs.YamlSerializer.Deserialize<Dictionary<string, List<int>>>("a: [1, 2]\n", options)!;
            Assert.Equal([1, 2], read["a"]);

            NotSupportedException ex = Assert.Throws<NotSupportedException>(() => Cs.YamlSerializer.Serialize(read, options));
            Assert.Contains(names[i], ex.Message, StringComparison.Ordinal);
        }
    }
}
