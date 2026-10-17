using System.Globalization;

namespace OneYaml.Tests.Schema;

/// <summary>
/// Deterministic number-like scalars: plain ints and doubles of every shape the resolver sees, the edges around
/// 2^53 and the 64-bit range, subnormals and overflow, and mutated spellings (stray signs, dots, exponents,
/// underscores, base prefixes) that must be refused or handled by the exact path.
/// </summary>
internal static class NumberScalarGenerator
{
    private static readonly string[] s_fixed =
    [
        "0", "-0", "+0", "0.0", "-0.0", "+0.0", "0e0", "-0e5", ".5", "-.5", "+.5e3", "5.", "5.e3", "1e5", "1E5", "1e+5", "1e-5",
        "4.9e-324", "2.2250738585072014e-308", "2.2250738585072011e-308", "1.7976931348623157e308", "1.7976931348623159e308", "1e308", "1e309", "1e400", "-1e400",
        "9007199254740992", "9007199254740993", "9007199254740992.0", "9007199254740993.0", "9223372036854775807", "9223372036854775808",
        "-9223372036854775808", "-9223372036854775809", "18446744073709551615", "18446744073709551616", "0.1", "0.3", "1.1", "123456789012345678",
        "0.000001", "1e22", "1e23", "1e-22", "1e-23", "123e-25", "0e100", "00000000000000000000001", "-0000000000000000000", "0x1F", "0o17", "0b1",
        ".inf", "-.inf", ".nan", ".NaN", "1_000", "1,5", "1.2.3", "e5", ".e5", "-", "+", ".", "1e", "1e+", "--1", "1 2", "١٢٣",
    ];

    private const string Noise = "0123456789.eE+-_xo fnNi";

    public static string Next(Random rnd)
    {
        switch (rnd.Next(10))
        {
            case 0:
                return s_fixed[rnd.Next(s_fixed.Length)];
            case 1:
                return Sign(rnd) + Digits(rnd, rnd.Next(1, 22));
            case 2:
                return rnd.NextInt64(long.MinValue, long.MaxValue).ToString(CultureInfo.InvariantCulture);
            case 3:
                return BitConverter.Int64BitsToDouble(rnd.NextInt64()).ToString("R", CultureInfo.InvariantCulture);
            case 4:
                return (rnd.NextDouble() * Math.Pow(10, rnd.Next(-30, 31))).ToString("R", CultureInfo.InvariantCulture);
            case 5:
                return Sign(rnd) + Digits(rnd, rnd.Next(0, 12)) + "." + Digits(rnd, rnd.Next(0, 12)) + Exponent(rnd);
            case 6:
                return Sign(rnd) + Digits(rnd, rnd.Next(1, 20)) + Exponent(rnd, force: true);
            case 7:
                return rnd.Next(-1_000_000, 1_000_000).ToString(CultureInfo.InvariantCulture) + "." + Digits(rnd, rnd.Next(1, 4));
            case 8:
                return Mutate(rnd, Sign(rnd) + Digits(rnd, rnd.Next(1, 10)) + "." + Digits(rnd, rnd.Next(1, 6)) + Exponent(rnd));
            default:
                return Mutate(rnd, s_fixed[rnd.Next(s_fixed.Length)]);
        }
    }

    private static string Sign(Random rnd) => rnd.Next(4) switch { 0 => "-", 1 => "+", _ => string.Empty };

    private static string Digits(Random rnd, int count)
    {
        Span<char> c = count <= 64 ? stackalloc char[count] : new char[count];
        for (int i = 0; i < c.Length; i++)
        {
            c[i] = (char)('0' + rnd.Next(10));
        }

        return new string(c);
    }

    private static string Exponent(Random rnd, bool force = false)
    {
        if (!force && rnd.Next(3) != 0)
        {
            return string.Empty;
        }

        int v = rnd.Next(8) == 0 ? rnd.Next(0, 100000) : rnd.Next(0, 40);
        return (rnd.Next(2) == 0 ? "e" : "E") + Sign(rnd) + v.ToString(CultureInfo.InvariantCulture);
    }

    private static string Mutate(Random rnd, string s)
    {
        int n = rnd.Next(1, 3);
        for (int k = 0; k < n; k++)
        {
            int at = rnd.Next(s.Length + 1);
            switch (rnd.Next(3))
            {
                case 0:
                    s = s.Insert(at, Noise[rnd.Next(Noise.Length)].ToString());
                    break;
                case 1 when s.Length > 0:
                    s = s.Remove(Math.Min(at, s.Length - 1), 1);
                    break;
                case 2 when s.Length > 0:
                    int p = Math.Min(at, s.Length - 1);
                    s = s.Remove(p, 1).Insert(p, Noise[rnd.Next(Noise.Length)].ToString());
                    break;
            }
        }

        return s;
    }
}
