using System.Numerics;
using System.Text;
using System.Text.Json.Serialization;
using Xunit;

// The tests pass types as values on purpose, and name the overloads without a type argument.
#pragma warning disable CA2263

namespace OneYaml.Tests.Generated;

// Shapes that must bind the same way in both paths. Each runs through reflection and through the generated context.

internal class BaseInit
{
    public string? Name { get; init; }

    public int Count { get; init; }
}

internal sealed class DerivedInit : BaseInit
{
    public string? Extra { get; set; }
}

internal class BaseVirtual
{
    [YamlIgnore]
    public virtual string? Secret { get; set; }

    [YamlRequired]
    public virtual string? Must { get; set; }

    [YamlPropertyName("renamed")]
    public virtual string? Named { get; set; }
}

internal sealed class DerivedVirtual : BaseVirtual
{
    public override string? Secret { get; set; }

    public override string? Must { get; set; }

    public override string? Named { get; set; }
}

internal sealed class DerivedVirtualMarked : BaseVirtual
{
    [YamlIgnore]
    public override string? Named { get; set; }

    [YamlRequired]
    public override string? Secret { get; set; }
}

// The generator warns that it leaves the delegate members out (OY0004, set to no-warn in this project); that is the point of this model.
internal sealed class WithDelegate
{
    public string Name { get; set; } = "";

    public Action? Callback { get; set; }

    public Func<int>? Factory;

    public WithDelegate() => Factory = () => 1;
}

internal sealed class ConstructorAndMembers
{
    public ConstructorAndMembers(string name, int size)
    {
        Name = name;
        Size = size;
    }

    public string Name { get; }

    public int Size { get; }

    public int Extra { get; init; }

    public List<string> Tags { get; set; } = [];

    public string? Note;
}

internal enum SmallEnum : byte
{
    A = 1,
    B = 2,
}

internal enum WideEnum : uint
{
    A = 1,
    Top = uint.MaxValue,
}

internal enum SignedEnum : sbyte
{
    Low = -128,
    Zero = 0,
}

internal enum HugeEnum : ulong
{
    One = 1,
    Top = ulong.MaxValue,
}

internal sealed class EnumRanges
{
    public SmallEnum Small { get; set; }

    public WideEnum Wide { get; set; }

    public SignedEnum Signed { get; set; }

    public HugeEnum Huge { get; set; }
}

internal sealed class Numbers
{
    public float Single { get; set; }

    public double Double { get; set; }

    public decimal Decimal { get; set; }
}

internal sealed class Wide70
{
    public int Slot0 { get; set; }

    public int Slot1 { get; set; }

    public int Slot2 { get; set; }

    public int Slot3 { get; set; }

    public int Slot4 { get; set; }

    public int Slot5 { get; set; }

    public int Slot6 { get; set; }

    public int Slot7 { get; set; }

    public int Slot8 { get; set; }

    public int Slot9 { get; set; }

    public int Slot10 { get; set; }

    public int Slot11 { get; set; }

    public int Slot12 { get; set; }

    public int Slot13 { get; set; }

    public int Slot14 { get; set; }

    public int Slot15 { get; set; }

    public int Slot16 { get; set; }

    public int Slot17 { get; set; }

    public int Slot18 { get; set; }

    public int Slot19 { get; set; }

    public int Slot20 { get; set; }

    public int Slot21 { get; set; }

    public int Slot22 { get; set; }

    public int Slot23 { get; set; }

    public int Slot24 { get; set; }

    public int Slot25 { get; set; }

    public int Slot26 { get; set; }

    public int Slot27 { get; set; }

    public int Slot28 { get; set; }

    public int Slot29 { get; set; }

    public int Slot30 { get; set; }

    public int Slot31 { get; set; }

    public int Slot32 { get; set; }

    public int Slot33 { get; set; }

    public int Slot34 { get; set; }

    public int Slot35 { get; set; }

    public int Slot36 { get; set; }

    public int Slot37 { get; set; }

    public int Slot38 { get; set; }

    public int Slot39 { get; set; }

    public int Slot40 { get; set; }

    public int Slot41 { get; set; }

    public int Slot42 { get; set; }

    public int Slot43 { get; set; }

    public int Slot44 { get; set; }

    public int Slot45 { get; set; }

    public int Slot46 { get; set; }

    public int Slot47 { get; set; }

    public int Slot48 { get; set; }

    public int Slot49 { get; set; }

    public int Slot50 { get; set; }

    public int Slot51 { get; set; }

    public int Slot52 { get; set; }

    public int Slot53 { get; set; }

    public int Slot54 { get; set; }

    public int Slot55 { get; set; }

    public int Slot56 { get; set; }

    public int Slot57 { get; set; }

    public int Slot58 { get; set; }

    public int Slot59 { get; set; }

    public int Slot60 { get; set; }

    public int Slot61 { get; set; }

    public int Slot62 { get; set; }

    public int Slot63 { get; set; }

    public int Slot64 { get; set; }

    public int Slot65 { get; set; }

    public int Slot66 { get; set; }

    public int Slot67 { get; set; }

    public int Slot68 { get; set; }

    public int Slot69 { get; set; }
}

internal sealed class IgnoreCaseMap : Dictionary<string, int>
{
    public IgnoreCaseMap()
        : base(StringComparer.OrdinalIgnoreCase)
    {
    }
}

internal sealed class RefConstructor
{
    public RefConstructor(ref int count) => Count = count;

    public RefConstructor()
    {
    }

    public int Count { get; set; }
}

internal sealed class MismatchedConstructor
{
    public MismatchedConstructor(long count) => Count = (int)count;

    public int Count { get; }
}

public class BindingFixTests
{
    private static readonly ModelsContext s_context = new();

    public static TheoryData<Type, string, string> Cases()
    {
        var data = new TheoryData<Type, string, string>();
        void Add(Type type, string yaml, string expected) => data.Add(type, yaml, expected);

        Add(typeof(DerivedInit), "name: a\ncount: 3\nextra: e", "a|3|e");
        Add(typeof(BaseInit), "name: b", "b|0");

        // Attributes on an overridden property do not carry over to the override (as in System.Text.Json).
        Add(typeof(DerivedVirtual), "secret: s\nmust: m\nrenamed: r\nnamed: n", "secret=s must=m named=n");
        Add(typeof(DerivedVirtual), "{}", "secret= must= named=");
        Add(typeof(BaseVirtual), "secret: s\nmust: m\nrenamed: r", "secret= must=m named=r");
        Add(typeof(BaseVirtual), "{}", "error Binding");
        Add(typeof(DerivedVirtualMarked), "named: n\nmust: m", "error Binding");
        Add(typeof(DerivedVirtualMarked), "secret: s\nmust: m\nrenamed: r\nnamed: x", "secret=s must=m named=");

        Add(typeof(WithDelegate), "name: n\ncallback: {}\nfactory: [1]", "n");
        Add(typeof(ConstructorAndMembers), "name: n\nsize: 4\nextra: 2\ntags: [a]\nnote: z", "n|4|2|a|z");
        Add(typeof(ConstructorAndMembers), "name: n", "n|0|0||");

        Add(typeof(EnumRanges), "small: 2", "B 0 0 0");
        Add(typeof(EnumRanges), "small: 1000", "error Binding");
        Add(typeof(EnumRanges), "small: -1", "error Binding");
        Add(typeof(EnumRanges), "wide: 4294967295", "0 Top 0 0");
        Add(typeof(EnumRanges), "wide: 4294967297", "error Binding");
        Add(typeof(EnumRanges), "wide: -1", "error Binding");
        Add(typeof(EnumRanges), "signed: -128", "0 0 Low 0");
        Add(typeof(EnumRanges), "signed: 128", "error Binding");
        Add(typeof(EnumRanges), "huge: 18446744073709551615", "0 0 0 Top");
        Add(typeof(EnumRanges), "huge: 18446744073709551616", "error Binding");
        Add(typeof(EnumRanges), "huge: -1", "error Binding");

        Add(typeof(Numbers), "single: 1e39", "error Binding");
        Add(typeof(Numbers), "single: 3.5e38", "error Binding");
        Add(typeof(Numbers), "single: .inf", "∞ 0 0");
        Add(typeof(Numbers), "single: 3.0e38", "3E+38 0 0");
        Add(typeof(Numbers), "double: 1e400", "error Binding");
        Add(typeof(Numbers), "double: -1e400", "error Binding");
        Add(typeof(Numbers), "double: -.inf", "0 -∞ 0");
        Add(typeof(Numbers), "double: 0x" + new string('f', 300), "error Binding");
        Add(typeof(Numbers), "decimal: 1e-30", "0 0 0.0000000000000000000000000000");
        Add(typeof(Numbers), "decimal: 1e30", "error Binding");
        Add(typeof(Numbers), "decimal: 1.2345678901234567890123456789012", "0 0 1.2345678901234567890123456789");
        Add(typeof(Numbers), "decimal: 99999999999999999999999999999999", "error Binding");

        // Skipping an unmapped value that is an alias replays it; the members after it still bind.
        Add(typeof(Person), "defs: &d {a: [1, {b: 2}], c: 3}\nname: n\nalso: *d\nage: 4", "n|4");
        Add(typeof(RefConstructor), "count: 4", "4");
        Add(typeof(IgnoreCaseMap), "a: 1\nA: 2", "error DuplicateKey");
        Add(typeof(IgnoreCaseMap), "a: 1\nb: 2", "a=1,b=2");
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Both_paths_give_the_expected_result(Type type, string yaml, string expected)
    {
        string reflected = Describe(() => YamlSerializer.Deserialize(yaml, type));
        string generated = Describe(() => YamlSerializer.Deserialize(yaml, s_context.GetTypeInfo(type)!));

        Assert.Equal(expected, reflected);
        Assert.Equal(expected, generated);
    }

    [Fact]
    public void An_alias_key_for_a_collection_is_a_complex_key_error_even_when_it_holds_one_scalar()
    {
        foreach (string yaml in new[] { "s: &a [x]\nm: {*a : 1}\n", "s: &a [&b x]\nm: {*a : 1}\n", "s: &a {k: &b x}\nm: {*a : 1}\n", "s: &a [[x]]\nm: {*a : 1}\n" })
        {
            YamlException reflected = Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<object>(yaml));
            YamlException generated = Assert.Throws<YamlException>(() => YamlSerializer.Deserialize(yaml, s_context.Object));

            Assert.Equal(YamlErrorCategory.Binding, reflected.Category);
            Assert.Equal(reflected.Category, generated.Category);
        }

        // A scalar anchor still works as a key.
        var ok = (Dictionary<string, object?>)YamlSerializer.Deserialize<object>("s: &a x\nm: {*a : 1}\n")!;
        Assert.Equal(1L, ((Dictionary<string, object?>)ok["m"]!)["x"]);
    }

    [Fact]
    public void Options_without_a_resolver_do_not_fall_back_to_reflection_in_GetTypeInfo()
    {
        var plain = new YamlSerializerOptions();

        Assert.Throws<InvalidOperationException>(() => plain.GetTypeInfo(typeof(Person)));
        Assert.Throws<InvalidOperationException>(() => plain.GetConverter(typeof(int)));

        // The annotated serializer overloads install the reflection resolver themselves.
        Assert.Equal("a", YamlSerializer.Deserialize<Person>("name: a", plain)!.Name);
        Assert.NotNull(plain.TypeInfoResolver);
        Assert.NotNull(plain.GetTypeInfo(typeof(Person)));

        var chosen = new YamlSerializerOptions { TypeInfoResolver = new DefaultYamlTypeInfoResolver() };
        Assert.NotNull(chosen.GetTypeInfo(typeof(Person)));
    }

    [Fact]
    public void An_integer_key_with_hundreds_of_thousands_of_digits_is_read()
    {
        string yaml = "? 0x" + new string('f', 400_000) + "\n: 1\n";
        var options = new YamlSerializerOptions { Limits = YamlLimits.None };

        object? value = YamlSerializer.Deserialize<object>(yaml, options);

        Assert.NotNull(value);

        // Different spellings of one integer are still the same key.
        Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<object>("? 0x" + new string('f', 40) + "\n: 1\n? " + BigInteger.Parse("0" + new string('f', 40), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture) + "\n: 2\n"));
    }

    [Fact]
    public void Nested_merge_keys_under_no_limits_end_in_a_limit_error_not_a_stack_overflow()
    {
        const int Depth = 100_000;
        string yaml = string.Concat(Enumerable.Repeat("<<: {", Depth)) + "name: x" + new string('}', Depth);
        string wrapped = "{" + yaml + "}";
        var options = new YamlSerializerOptions { MergeKeys = true, Limits = YamlLimits.None };

        foreach (Func<object?> read in new Func<object?>[]
        {
            () => YamlSerializer.Deserialize<Person>(wrapped, options),
            () => YamlSerializer.Deserialize<Dictionary<string, string>>(wrapped, options),
            () => YamlSerializer.Deserialize(wrapped, new ModelsContext(options).Person),
        })
        {
            YamlException ex = OnSmallStack(read);
            Assert.Equal(YamlErrorCategory.Limit, ex.Category);
        }
    }

    [Fact]
    public void A_deep_value_under_no_limits_is_refused_on_write_not_a_stack_overflow()
    {
        var root = new Node { Name = "r" };
        Node current = root;
        for (int i = 0; i < 200_000; i++)
        {
            var next = new Node { Name = "n" };
            current.Children.Add(next);
            current = next;
        }

        var options = new YamlSerializerOptions { Limits = YamlLimits.None };
        YamlException ex = OnSmallStack(() => YamlSerializer.Serialize(root, options));
        Assert.Equal(YamlErrorCategory.Limit, ex.Category);
    }

    [Fact]
    public void Constructor_and_member_mixes_write_and_read_back()
    {
        var value = new ConstructorAndMembers("n", 4) { Extra = 2, Tags = ["a"], Note = "z" };

        string text = YamlSerializer.Serialize(value);
        string generatedText = YamlSerializer.Serialize(value, s_context.ConstructorAndMembers);

        Assert.Equal(text, generatedText);
        Assert.Equal("n|4|2|a|z", Describe(() => YamlSerializer.Deserialize<ConstructorAndMembers>(text)));
    }

    [Fact]
    public void A_constructor_parameter_and_its_member_must_agree_in_type()
    {
        Assert.Throws<NotSupportedException>(() => YamlSerializer.Deserialize<MismatchedConstructor>("count: 3"));
    }

    [Fact]
    public void More_than_sixty_four_members_work_with_merge_keys()
    {
        var options = new YamlSerializerOptions { MergeKeys = true };
        const string Yaml = "<<: {slot69: 1, slot65: 2, slot3: 9}\nslot3: 5\nslot66: 7";

        foreach (Wide70? w in new[] { YamlSerializer.Deserialize<Wide70>(Yaml, options), YamlSerializer.Deserialize(Yaml, new ModelsContext(options).Wide70) })
        {
            Assert.Equal((5, 1, 2, 7), (w!.Slot3, w.Slot69, w.Slot65, w.Slot66));
        }

        Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<Wide70>("slot68: 1\nslot68: 2", options));
    }

    [Fact]
    public void Digits_beyond_the_integer_cap_are_a_limit_error_for_every_number_type()
    {
        string digits = new('9', 5000);
        foreach (string target in new[] { "double", "decimal", "big" })
        {
            YamlException ex = target switch
            {
                "double" => Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<Numbers>("double: " + digits)),
                "decimal" => Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<Numbers>("decimal: " + digits)),
                _ => Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<BigInteger>(digits)),
            };
            Assert.Equal(YamlErrorCategory.Limit, ex.Category);
        }

        Assert.Equal(YamlErrorCategory.Limit, Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<object>(digits)).Category);
    }

    private static YamlException OnSmallStack(Func<object?> action)
    {
        YamlException? caught = null;
        Exception? other = null;
        var thread = new Thread(
            () =>
            {
                try
                {
                    action();
                }
                catch (YamlException ex)
                {
                    caught = ex;
                }
                catch (Exception ex)
                {
                    other = ex;
                }
            },
            1024 * 1024);
        thread.Start();
        thread.Join();
        Assert.Null(other);
        return Assert.IsType<YamlException>(caught);
    }

    private static string Describe(Func<object?> read)
    {
        try
        {
            return Show(read());
        }
        catch (YamlException ex)
        {
            return "error " + ex.Category;
        }
    }

    private static string Show(object? value) => value switch
    {
        DerivedInit d => $"{d.Name}|{d.Count}|{d.Extra}",
        BaseInit b => $"{b.Name}|{b.Count}",
        BaseVirtual v => $"secret={v.Secret} must={v.Must} named={v.Named}",
        WithDelegate w => w.Name,
        ConstructorAndMembers c => $"{c.Name}|{c.Size}|{c.Extra}|{string.Join(",", c.Tags)}|{c.Note}",
        EnumRanges e => $"{(e.Small == 0 ? "0" : e.Small.ToString())} {(e.Wide == 0 ? "0" : e.Wide.ToString())} {(e.Signed == 0 ? "0" : e.Signed.ToString())} {(e.Huge == 0 ? "0" : e.Huge.ToString())}",
        Person p => $"{p.Name}|{p.Age}",
        RefConstructor r => r.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
        IgnoreCaseMap m => string.Join(",", m.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => $"{x.Key}={x.Value}")),
        Numbers n => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Fmt(n.Single)} {Fmt(n.Double)} {n.Decimal}"),
        _ => value?.ToString() ?? "null",
    };

    private static string Fmt(double d) => double.IsPositiveInfinity(d) ? "∞" : double.IsNegativeInfinity(d) ? "-∞" : d == 0 ? "0" : d.ToString("G6", System.Globalization.CultureInfo.InvariantCulture);
}
