using System.Text;
using Xunit;
using Cs = global::OneYaml.Compat.SharpYaml;
using Real = global::SharpYaml;

namespace OneYaml.Compat.Tests.SharpYamlCompat;

// Special floats and nulls read into numbers, compared with the real library.
public class SpecialFloatTests
{
    private static readonly string[] Texts =
    [
        "Infinity", "-Infinity", "+Infinity", "NaN", "-NaN", "infinity", "INFINITY", "nan", ".inf", "-.inf", "+.inf", ".Inf", ".INF", ".nan", ".NaN", ".NAN",
        "~", "null", "Null", "NULL", "", "# c", "'Infinity'", "\"-Infinity\"", "'NaN'", "'~'", "'null'", "Inf", "-Inf", "1e400",
    ];

    private static string Show(Func<object?> call)
    {
        try
        {
            object? v = call();
            return v switch
            {
                null => "null",
                double d => "double:" + BitConverter.DoubleToInt64Bits(d),
                float f => "float:" + BitConverter.SingleToInt32Bits(f),
                Half h => "half:" + BitConverter.HalfToUInt16Bits(h),
                _ => v.GetType().Name + ":" + v,
            };
        }
        catch (Exception e)
        {
            return "EX " + Differ.Kind(e);
        }
    }

    private static void Compare<T>(string text, StringBuilder diffs)
    {
        string a = Show(() => Real.YamlSerializer.Deserialize<T>(text));
        string b = Show(() => Cs.YamlSerializer.Deserialize<T>(text));
        if (a != b)
        {
            diffs.AppendLine($"{typeof(T).Name} <- [{text}]: real={a} compat={b}");
        }
    }

    private static bool IsNullText(string text) => text is "~" or "null" or "Null" or "NULL";

    // Documented differences, each listed in docs/compat/sharpyaml.md: empty text is the default value here and a
    // NullReferenceException there, a number past the range of the type is an error here and infinity there, a null
    // inside a collection is the default here, and the YAML 1.2 infinity and NaN forms read into Half here only.
    private static bool Documented(string text, Type type, bool inCollection) =>
        text is "" or "# c" or "1e400"
        || (inCollection && IsNullText(text))
        || (text.Contains('.', StringComparison.Ordinal) && (type == typeof(Half) || type == typeof(Half?)));

    private static void Check<T>(string text, bool inCollection, StringBuilder diffs)
    {
        if (!Documented(text, typeof(T), inCollection))
        {
            Compare<T>(inCollection ? "{a: " + text + "}" : text, diffs);
        }
    }

    [Fact]
    public void Special_floats_and_nulls_read_into_numbers_as_the_real_library_reads_them()
    {
        var diffs = new StringBuilder();
        foreach (string text in Texts)
        {
            foreach (bool inCollection in new[] { false, true })
            {
                Check<double>(text, inCollection, diffs);
                Check<float>(text, inCollection, diffs);
                Check<Half>(text, inCollection, diffs);
                Check<double?>(text, inCollection, diffs);
                Check<Half?>(text, inCollection, diffs);
                if (!inCollection)
                {
                    Check<int>(text, inCollection, diffs);
                    Check<long>(text, inCollection, diffs);
                    Check<decimal>(text, inCollection, diffs);
                    Check<bool>(text, inCollection, diffs);
                    Check<int?>(text, inCollection, diffs);
                    Check<Int128>(text, inCollection, diffs);
                    Check<UInt128>(text, inCollection, diffs);
                }
            }
        }

        Assert.True(diffs.Length == 0, diffs.ToString());
    }
}
