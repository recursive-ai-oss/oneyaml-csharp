using System.Text;
using Xunit;

// The tests pass types as values on purpose, and name the overloads without a type argument.
#pragma warning disable CA2263

namespace OneYaml.Tests.Generated;

// Each case runs through reflection and through the generated context and must end the same way on both.
public class ReadSideFixTests
{
    private static YamlTypeInfo<T>?[] Paths<T>() => [null, (YamlTypeInfo<T>)new ModelsContext().GetTypeInfo(typeof(T))!];

    private static T Read<T>(string yaml, YamlTypeInfo<T>? info) =>
        info is null ? YamlSerializer.Deserialize<T>(yaml)! : YamlSerializer.Deserialize(yaml, info)!;

    [Fact]
    public void An_init_only_member_of_a_generic_base_binds_on_both_paths()
    {
        foreach (YamlTypeInfo<GenericInitDerived>? info in Paths<GenericInitDerived>())
        {
            GenericInitDerived d = Read("value: 5\nlabel: l\nextra: e\n", info);
            Assert.Equal(5, d.Value);
            Assert.Equal("l", d.Label);
            Assert.Equal("e", d.Extra);
        }
    }

    [Fact]
    public void An_init_only_member_of_a_generic_struct_binds_on_both_paths()
    {
        foreach (YamlTypeInfo<GenericInitStruct<long>>? info in Paths<GenericInitStruct<long>>())
        {
            Assert.Equal(7, Read("item: 7\n", info).Item);
        }
    }

    [Theory]
    [InlineData("keep: k\nother: 1\nother: 2\n")]
    [InlineData("hidden: 1\nhidden: 2\n")]
    [InlineData(": a\n: b\n")]
    [InlineData("~: a\n~: b\n")]
    [InlineData("a: 1\nb: 2\na: 3\n")]
    [InlineData("\"a\\x41\": 1\n\"aA\": 2\n")]
    public void A_repeated_key_is_an_error_even_when_no_member_takes_it(string yaml)
    {
        foreach (YamlTypeInfo<FewMembers>? info in Paths<FewMembers>())
        {
            YamlException ex = Assert.Throws<YamlException>(() => Read(yaml, info));
            Assert.Equal(YamlErrorCategory.DuplicateKey, ex.Category);
        }
    }

    [Fact]
    public void Many_different_skipped_keys_are_fine_and_a_late_repeat_is_found()
    {
        var sb = new StringBuilder("keep: k\n");
        for (int i = 0; i < 100; i++)
        {
            sb.Append('k').Append(i).Append(": ").Append(i).Append('\n');
        }

        string distinct = sb.ToString();
        foreach (YamlTypeInfo<FewMembers>? info in Paths<FewMembers>())
        {
            Assert.Equal("k", Read(distinct, info).Keep);
            YamlException ex = Assert.Throws<YamlException>(() => Read(distinct + "k3: again\n", info));
            Assert.Equal(YamlErrorCategory.DuplicateKey, ex.Category);
        }
    }

    [Fact]
    public void A_finite_float_that_overflows_is_refused_not_read_as_infinity()
    {
        foreach (string yaml in new[] { "1e400", "-1e400", "x: 1e400", "[1.5, 1e999]" })
        {
            YamlException ex = Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<object>(yaml));
            Assert.Contains("range", ex.Message, StringComparison.Ordinal);
        }

        Assert.Equal(double.PositiveInfinity, YamlSerializer.Deserialize<object>(".inf"));
        Assert.Equal(double.NegativeInfinity, YamlSerializer.Deserialize<object>("-.INF"));
        Assert.Equal(1e300, YamlSerializer.Deserialize<object>("1e300"));
    }

    [Theory]
    [InlineData("1e400")]
    [InlineData("-1e400")]
    public void A_typed_number_that_overflows_is_refused(string yaml)
    {
        Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<double>(yaml));
        Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<float>(yaml));
        Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<decimal>(yaml));
    }
}
