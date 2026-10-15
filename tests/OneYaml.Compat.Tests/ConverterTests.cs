#pragma warning disable IDE0005
using global::OneYaml.Compat.YamlDotNet.Core;
using global::YamlDotNet.Core;
using Xunit;
using Cm = global::OneYaml.Compat.YamlDotNet.Serialization;
using CmCore = global::OneYaml.Compat.YamlDotNet.Core;
using CmEvents = global::OneYaml.Compat.YamlDotNet.Core.Events;
using Rl = global::YamlDotNet.Serialization;
using RlCore = global::YamlDotNet.Core;
using RlEvents = global::YamlDotNet.Core.Events;

namespace OneYaml.Compat.Tests;

public sealed class Money
{
    public decimal Amount { get; set; }

    public string Currency { get; set; } = "";
}

public sealed class Span2
{
    public int From { get; set; }

    public int To { get; set; }
}

public class Wallet
{
    public Money? Cash { get; set; }

    public List<Money>? Notes { get; set; }

    public Span2? Range { get; set; }
}

// The same converter written against each library's interfaces: a scalar form "12.50 USD".
public sealed class RealMoneyConverter : Rl.IYamlTypeConverter
{
    public bool Accepts(Type type) => type == typeof(Money);

    public object? ReadYaml(RlCore.IParser parser, Type type, Rl.ObjectDeserializer rootDeserializer)
    {
        var scalar = RlCore.ParserExtensions.Consume<RlEvents.Scalar>(parser);
        string[] parts = scalar.Value.Split(' ');
        return new Money { Amount = decimal.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture), Currency = parts[1] };
    }

    public void WriteYaml(RlCore.IEmitter emitter, object? value, Type type, Rl.ObjectSerializer serializer)
    {
        var m = (Money)value!;
        emitter.Emit(new RlEvents.Scalar(m.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + m.Currency));
    }
}

public sealed class CompatMoneyConverter : Cm.IYamlTypeConverter
{
    public bool Accepts(Type type) => type == typeof(Money);

    public object? ReadYaml(CmCore.IParser parser, Type type, Cm.ObjectDeserializer rootDeserializer)
    {
        var scalar = CmCore.ParserExtensions.Consume<CmEvents.Scalar>(parser);
        string[] parts = scalar.Value.Split(' ');
        return new Money { Amount = decimal.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture), Currency = parts[1] };
    }

    public void WriteYaml(CmCore.IEmitter emitter, object? value, Type type, Cm.ObjectSerializer serializer)
    {
        var m = (Money)value!;
        emitter.Emit(new CmEvents.Scalar(m.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + m.Currency));
    }
}

// A converter that writes a flow mapping and reads nested values through the deserializer it is given.
public sealed class RealSpanConverter : Rl.IYamlTypeConverter
{
    public bool Accepts(Type type) => type == typeof(Span2);

    public object? ReadYaml(RlCore.IParser parser, Type type, Rl.ObjectDeserializer rootDeserializer)
    {
        parser.Consume<RlEvents.MappingStart>();
        var span = new Span2();
        while (!parser.TryConsume<RlEvents.MappingEnd>(out _))
        {
            string key = parser.Consume<RlEvents.Scalar>().Value;
            int value = (int)rootDeserializer(typeof(int))!;
            if (key == "from")
            {
                span.From = value;
            }
            else
            {
                span.To = value;
            }
        }

        return span;
    }

    public void WriteYaml(RlCore.IEmitter emitter, object? value, Type type, Rl.ObjectSerializer serializer)
    {
        var s = (Span2)value!;
        emitter.Emit(new RlEvents.MappingStart(RlCore.AnchorName.Empty, RlCore.TagName.Empty, true, RlEvents.MappingStyle.Flow));
        emitter.Emit(new RlEvents.Scalar("from"));
        serializer(s.From, typeof(int));
        emitter.Emit(new RlEvents.Scalar("to"));
        serializer(s.To, typeof(int));
        emitter.Emit(new RlEvents.MappingEnd());
    }
}

public sealed class CompatSpanConverter : Cm.IYamlTypeConverter
{
    public bool Accepts(Type type) => type == typeof(Span2);

    public object? ReadYaml(CmCore.IParser parser, Type type, Cm.ObjectDeserializer rootDeserializer)
    {
        parser.Consume<CmEvents.MappingStart>();
        var span = new Span2();
        while (!parser.TryConsume<CmEvents.MappingEnd>(out _))
        {
            string key = parser.Consume<CmEvents.Scalar>().Value;
            int value = (int)rootDeserializer(typeof(int))!;
            if (key == "from")
            {
                span.From = value;
            }
            else
            {
                span.To = value;
            }
        }

        return span;
    }

    public void WriteYaml(CmCore.IEmitter emitter, object? value, Type type, Cm.ObjectSerializer serializer)
    {
        var s = (Span2)value!;
        emitter.Emit(new CmEvents.MappingStart(CmCore.AnchorName.Empty, CmCore.TagName.Empty, true, CmEvents.MappingStyle.Flow));
        emitter.Emit(new CmEvents.Scalar("from"));
        serializer(s.From, typeof(int));
        emitter.Emit(new CmEvents.Scalar("to"));
        serializer(s.To, typeof(int));
        emitter.Emit(new CmEvents.MappingEnd());
    }
}

public sealed class RealConvertible : Rl.IYamlConvertible
{
    public string Text { get; set; } = "";

    public void Read(RlCore.IParser parser, Type expectedType, Rl.ObjectDeserializer nestedObjectDeserializer) =>
        Text = "<" + parser.Consume<RlEvents.Scalar>().Value + ">";

    public void Write(RlCore.IEmitter emitter, Rl.ObjectSerializer nestedObjectSerializer) =>
        emitter.Emit(new RlEvents.Scalar(Text.Trim('<', '>')));
}

public sealed class CompatConvertible : Cm.IYamlConvertible
{
    public string Text { get; set; } = "";

    public void Read(CmCore.IParser parser, Type expectedType, Cm.ObjectDeserializer nestedObjectDeserializer) =>
        Text = "<" + parser.Consume<CmEvents.Scalar>().Value + ">";

    public void Write(CmCore.IEmitter emitter, Cm.ObjectSerializer nestedObjectSerializer) =>
        emitter.Emit(new CmEvents.Scalar(Text.Trim('<', '>')));
}

public class ConverterTests
{
    private const string WalletYaml = "Cash: 12.50 USD\nNotes:\n- 5 EUR\n- 1.25 GBP\nRange: {from: 1, to: 9}\n";

    [Fact]
    public void Type_converters_read_and_write_like_the_real_ones()
    {
        var (rd, cd) = Both.Deserializers(
            r => r.WithTypeConverter(new RealMoneyConverter()).WithTypeConverter(new RealSpanConverter()),
            c => c.WithTypeConverter(new CompatMoneyConverter()).WithTypeConverter(new CompatSpanConverter()));
        Assert.Equal(Differ.Dump(rd.Deserialize<Wallet>(WalletYaml)), Differ.Dump(cd.Deserialize<Wallet>(WalletYaml)));

        var (rs, cs) = Both.Serializers(
            r => r.WithTypeConverter(new RealMoneyConverter()).WithTypeConverter(new RealSpanConverter()),
            c => c.WithTypeConverter(new CompatMoneyConverter()).WithTypeConverter(new CompatSpanConverter()));
        var wallet = rd.Deserialize<Wallet>(WalletYaml);
        Assert.Equal(rs.Serialize(wallet), cs.Serialize(wallet));
    }

    [Fact]
    public void Convertible_types_read_and_write_like_the_real_ones()
    {
        var (rd, cd) = Both.Deserializers();
        Assert.Equal("<x>", rd.Deserialize<RealConvertible>("x").Text);
        Assert.Equal("<x>", cd.Deserialize<CompatConvertible>("x").Text);
        var (rs, cs) = Both.Serializers();
        Assert.Equal(rs.Serialize(new RealConvertible { Text = "<y>" }), cs.Serialize(new CompatConvertible { Text = "<y>" }));
        Assert.Equal(rs.Serialize(new List<RealConvertible> { new() { Text = "<a>" } }), cs.Serialize(new List<CompatConvertible> { new() { Text = "<a>" } }));
    }

    [Fact]
    public void Tag_mapping_and_attribute_overrides_behave_like_the_real_ones()
    {
        var (rs, cs) = Both.Serializers(
            r => r.WithTagMapping("!person", typeof(Person)).WithAttributeOverride<Person>(p => p.Age, new Rl.YamlMemberAttribute { Alias = "years" }),
            c => c.WithTagMapping("!person", typeof(Person)).WithAttributeOverride<Person>(p => p.Age, new Cm.YamlMemberAttribute { Alias = "years" }));
        var p = new Person { Name = "x", Age = 4 };
        Assert.Equal(rs.Serialize(p), cs.Serialize(p));

        var (rd, cd) = Both.Deserializers(
            r => r.WithTagMapping("!person", typeof(Person)).IgnoreUnmatchedProperties(),
            c => c.WithTagMapping("!person", typeof(Person)).IgnoreUnmatchedProperties());
        const string yaml = "!person\nName: z\nunknown: 1\n";
        Assert.Equal(Differ.Dump(rd.Deserialize<object>(yaml)), Differ.Dump(cd.Deserialize<object>(yaml)));
    }

    [Fact]
    public void Multi_document_reading_through_a_parser_matches()
    {
        const string yaml = "a: 1\n---\na: 2\n---\na: 3\n";
        var (rd, cd) = Both.Deserializers();
        var realParser = new RlCore.Parser(new StringReader(yaml));
        var compatParser = new CmCore.Parser(new StringReader(yaml));
        realParser.Consume<RlEvents.StreamStart>();
        compatParser.Consume<CmEvents.StreamStart>();
        var real = new List<string>();
        var compat = new List<string>();
        while (realParser.Accept<RlEvents.DocumentStart>(out _))
        {
            real.Add(Differ.Dump(rd.Deserialize<Dictionary<string, int>>(realParser)));
        }

        while (compatParser.Accept<CmEvents.DocumentStart>(out _))
        {
            compat.Add(Differ.Dump(cd.Deserialize<Dictionary<string, int>>(compatParser)));
        }

        Assert.Equal(3, real.Count);
        Assert.Equal(real, compat);
    }

    [Fact]
    public void Options_behave_like_the_real_ones()
    {
        var diffs = new List<string>();
        void Check(string label, Func<object?> real, Func<object?> compat)
        {
            string a = Differ.OutcomeOfReal(real);
            string b = Differ.Outcome(compat);
            if (a != b)
            {
                diffs.Add($"{label}: real={a} compat={b}");
            }
        }

        var (rI, cI) = Both.Deserializers(r => r.IgnoreUnmatchedProperties(), c => c.IgnoreUnmatchedProperties());
        Check("ignore unmatched", () => rI.Deserialize<Person>("Name: x\nZip: 1"), () => cI.Deserialize<Person>("Name: x\nZip: 1"));
        var (rC, cC) = Both.Deserializers(r => r.WithCaseInsensitivePropertyMatching(), c => c.WithCaseInsensitivePropertyMatching());
        Check("case insensitive", () => rC.Deserialize<Person>("name: x\nAGE: 2"), () => cC.Deserialize<Person>("name: x\nAGE: 2"));
        var (rD, cD) = Both.Deserializers(r => r.WithDuplicateKeyChecking(), c => c.WithDuplicateKeyChecking());
        Check("duplicate keys object", () => rD.Deserialize<object>("a: 1\na: 2"), () => cD.Deserialize<object>("a: 1\na: 2"));
        Check("duplicate keys poco", () => rD.Deserialize<Person>("Name: 1\nName: 2"), () => cD.Deserialize<Person>("Name: 1\nName: 2"));
        var (rU, cU) = Both.Deserializers(r => r.WithAttemptingUnquotedStringTypeDeserialization(), c => c.WithAttemptingUnquotedStringTypeDeserialization());
        // YamlDotNet reads decimals in this mode as floats and loses digits; the compat package reads them as doubles.
        const string typed = "[1, 1.5, true, null, abc, '1', 0x10, -1, 70000, 5000000000, 18446744073709551615, TRUE, yes, 0b11, 017, 1_000]";
        Check("unquoted typing", () => Differ.Dump(rU.Deserialize<object>(typed)).Replace("Single:", "Double:"), () => Differ.Dump(cU.Deserialize<object>(typed)));
        var (rM, cM) = Both.Deserializers(r => r.WithMaximumRecursion(3), c => c.WithMaximumRecursion(3));
        Check("recursion 3 ok", () => rM.Deserialize<object>("[[1]]"), () => cM.Deserialize<object>("[[1]]"));
        Check("recursion 3 deep", () => rM.Deserialize<object>("[[[[1]]]]"), () => cM.Deserialize<object>("[[[[1]]]]"));
        var (rT, cT) = Both.Deserializers(r => r.WithTypeMapping<IList<int>, List<int>>(), c => c.WithTypeMapping<IList<int>, List<int>>());
        Check("type mapping", () => rT.Deserialize<IList<int>>("[1]"), () => cT.Deserialize<IList<int>>("[1]"));
        var (rE, cE) = Both.Deserializers(r => r.WithEnumNamingConvention(global::YamlDotNet.Serialization.NamingConventions.CamelCaseNamingConvention.Instance), c => c.WithEnumNamingConvention(global::OneYaml.Compat.YamlDotNet.Serialization.NamingConventions.CamelCaseNamingConvention.Instance));
        Check("enum naming", () => rE.Deserialize<Color>("darkBlue"), () => cE.Deserialize<Color>("darkBlue"));
        var (rS, cS) = Both.Serializers(r => r.WithEnumNamingConvention(global::YamlDotNet.Serialization.NamingConventions.CamelCaseNamingConvention.Instance), c => c.WithEnumNamingConvention(global::OneYaml.Compat.YamlDotNet.Serialization.NamingConventions.CamelCaseNamingConvention.Instance));
        Check("enum naming write", () => rS.Serialize(Color.DarkBlue), () => cS.Serialize(Color.DarkBlue));
        var (rF, cF) = Both.Serializers(r => r.IgnoreFields(), c => c.IgnoreFields());
        Check("ignore fields", () => rF.Serialize(new Person { Fld = 4 }), () => cF.Serialize(new Person { Fld = 4 }));
        var (rR, cR) = Both.Serializers(r => r.WithMaximumRecursion(3), c => c.WithMaximumRecursion(3));
        Check("ser recursion", () => rR.Serialize(new List<List<List<List<int>>>>()), () => cR.Serialize(new List<List<List<List<int>>>>()));
        Check("ser recursion deep", () => rR.Serialize(new List<List<List<List<int>>>> { new() { new() { new() { 1 } } } }), () => cR.Serialize(new List<List<List<List<int>>>> { new() { new() { new() { 1 } } } }));
        var (rJ, cJ) = Both.Serializers(r => r.EnsureRoundtrip(), c => c.EnsureRoundtrip());
        Check("ensure roundtrip", () => rJ.Serialize(new Person { Name = "z" }), () => cJ.Serialize(new Person { Name = "z" }));
        var (rN, cN) = Both.Serializers(r => r.WithNewLine("\r\n"), c => c.WithNewLine("\r\n"));
        Check("newline", () => rN.Serialize(new Person { Name = "z" }), () => cN.Serialize(new Person { Name = "z" }));
        var (rDe, cDe) = Both.Deserializers();
        Check("depth 130", () => rDe.Deserialize<object>(new string('[', 130) + new string(']', 130)), () => cDe.Deserialize<object>(new string('[', 130) + new string(']', 130)));
        Check("depth 131", () => rDe.Deserialize<object>(new string('[', 131) + new string(']', 131)), () => cDe.Deserialize<object>(new string('[', 131) + new string(']', 131)));
        var (rSe, cSe) = Both.Serializers();
        object Nest(int n) { object v = 1; for (int i = 0; i < n; i++) { v = new List<object> { v }; } return v; }
        Check("ser depth 49", () => rSe.Serialize(Nest(49)), () => cSe.Serialize(Nest(49)));
        Check("ser depth 50", () => rSe.Serialize(Nest(50)), () => cSe.Serialize(Nest(50)));

        Assert.True(diffs.Count == 0, string.Join("\n", diffs));
    }
}
