using System.Text;
using Xunit;
using CmNaming = global::OneYaml.Compat.YamlDotNet.Serialization.NamingConventions;
using RlNaming = global::YamlDotNet.Serialization.NamingConventions;

namespace OneYaml.Compat.Tests;

public class NamingTests
{
    private static readonly string[] Names =
    [
        "Name", "name", "FirstName", "firstName", "first_name", "first-name", "HTTPServerName", "HTTPServer", "URL", "aBC", "ABc", "A", "a", "X1", "x1Y2", "A1B2C3", "_private", "__x", "x__y", "Foo_Bar",
        "foo-Bar", "FOO_BAR", "fooBAR", "IOStream", "Id", "ID", "MyID", "MyIDs", "user2Name", "Éa", "éÉ", "a b", "A B", "v1.2", "Trailing_", "-lead", "Two__Under", "x--y", "x-_y", "x_-y", "a_1", "1_a", "_", "__", "-", "a-", "Ab_cD_eF", "camelCaseAndMore", "PascalCaseAndMore",
    ];

    [Fact]
    public void Conventions_map_names_like_the_real_ones()
    {
        var diffs = new StringBuilder();
        var pairs = new (string Label, Func<string, string> Real, Func<string, string> Compat, Func<string, string> RealReverse, Func<string, string> CompatReverse)[]
        {
            ("camel", RlNaming.CamelCaseNamingConvention.Instance.Apply, CmNaming.CamelCaseNamingConvention.Instance.Apply, RlNaming.CamelCaseNamingConvention.Instance.Reverse, CmNaming.CamelCaseNamingConvention.Instance.Reverse),
            ("pascal", RlNaming.PascalCaseNamingConvention.Instance.Apply, CmNaming.PascalCaseNamingConvention.Instance.Apply, RlNaming.PascalCaseNamingConvention.Instance.Reverse, CmNaming.PascalCaseNamingConvention.Instance.Reverse),
            ("underscored", RlNaming.UnderscoredNamingConvention.Instance.Apply, CmNaming.UnderscoredNamingConvention.Instance.Apply, RlNaming.UnderscoredNamingConvention.Instance.Reverse, CmNaming.UnderscoredNamingConvention.Instance.Reverse),
            ("hyphenated", RlNaming.HyphenatedNamingConvention.Instance.Apply, CmNaming.HyphenatedNamingConvention.Instance.Apply, RlNaming.HyphenatedNamingConvention.Instance.Reverse, CmNaming.HyphenatedNamingConvention.Instance.Reverse),
            ("lower", RlNaming.LowerCaseNamingConvention.Instance.Apply, CmNaming.LowerCaseNamingConvention.Instance.Apply, RlNaming.LowerCaseNamingConvention.Instance.Reverse, CmNaming.LowerCaseNamingConvention.Instance.Reverse),
            ("null", RlNaming.NullNamingConvention.Instance.Apply, CmNaming.NullNamingConvention.Instance.Apply, RlNaming.NullNamingConvention.Instance.Reverse, CmNaming.NullNamingConvention.Instance.Reverse),
        };
        foreach (var (label, real, compat, realReverse, compatReverse) in pairs)
        {
            foreach (string name in Names)
            {
                string a = Try(real, name);
                string b = Try(compat, name);
                if (a != b)
                {
                    diffs.AppendLine($"{label}.Apply('{name}'): real='{a}' compat='{b}'");
                }

                a = Try(realReverse, name);
                b = Try(compatReverse, name);
                if (a != b)
                {
                    diffs.AppendLine($"{label}.Reverse('{name}'): real='{a}' compat='{b}'");
                }
            }
        }

        Assert.True(diffs.Length == 0, diffs.ToString());
    }

    [Fact]
    public void An_empty_name_stays_empty_where_the_real_library_crashes()
    {
        // YamlDotNet throws IndexOutOfRangeException from the case conversions on an empty name. A member never has one.
        Assert.Throws<IndexOutOfRangeException>(() => RlNaming.CamelCaseNamingConvention.Instance.Apply(string.Empty));
        Assert.Equal(string.Empty, CmNaming.CamelCaseNamingConvention.Instance.Apply(string.Empty));
        Assert.Equal(string.Empty, CmNaming.UnderscoredNamingConvention.Instance.Apply(string.Empty));
    }

    private static string Try(Func<string, string> f, string s)
    {
        try
        {
            return f(s);
        }
        catch (Exception e)
        {
            return "EX " + e.GetType().Name;
        }
    }
}
