using System.Text;
using Xunit;

namespace OneYaml.Compat.Tests;

public class ScalarMatrixTests
{
    private static readonly string[] Texts =
    [
        "1", "-5", "+5", "0x1F", "0o17", "017", "0b101", "1_000", "1e3", "1.5", ".inf", "-.inf", ".nan", "true", "True", "yes", "on", "y", "n", "off",
        "false", "null", "~", "", "abc", "'12'", "\"12\"", "'null'", "2020-01-02", "2020-01-02T03:04:05Z", "00:00:05", "1:30", "a", "256", "300000000000",
        "-0", "0", "00", "08", "1,000", " 5", "5 ", "1.", ".5", "Infinity", "NaN", "0.1", "9223372036854775807", "9223372036854775808", "18446744073709551615", "-129",
        "2001-12-14t21:59:43.10-05:00", "1.2.3", "http://x.y/z", "9f5b8f1c-7c1e-4a8a-9d7e-4c6f6a0b1f2a", "A", "b", "FooBar", "fooBar", "A, B", "99",
    ];

    private enum Color { A, B, FooBar }

    [Flags]
    private enum Bits { None = 0, One = 1, Two = 2 }

    private static readonly Type[] Targets =
    [
        typeof(int), typeof(long), typeof(short), typeof(byte), typeof(sbyte), typeof(uint), typeof(ulong), typeof(double), typeof(float), typeof(decimal),
        typeof(bool), typeof(string), typeof(char), typeof(object), typeof(int?), typeof(bool?), typeof(double?), typeof(Color), typeof(Bits), typeof(Guid), typeof(TimeSpan), typeof(Uri),
        typeof(DateTime), typeof(List<int>), typeof(Dictionary<string, int>), typeof(int[]),
    ];

    [Fact]
    public void Scalar_texts_read_as_the_same_values()
    {
        var (real, compat) = Both.Deserializers();
        var diffs = new StringBuilder();
        foreach (Type t in Targets)
        {
            foreach (string text in Texts)
            {
                string a = Differ.Outcome(() => real.Deserialize(text, t));
                string b = Differ.Outcome(() => compat.Deserialize(text, t));
                // Intended difference: YamlDotNet turns a longer string into a char by dropping all but the first character.
                if (t == typeof(char) && text.Trim('\'', '"').Length != 1 && a.StartsWith("char:", StringComparison.Ordinal) && b == "EX YamlException")
                {
                    continue;
                }

                if (a != b)
                {
                    diffs.AppendLine($"{t.Name} <- [{text}]: real={a} compat={b}");
                }
            }
        }

        Assert.True(diffs.Length == 0, diffs.ToString());
    }
}
