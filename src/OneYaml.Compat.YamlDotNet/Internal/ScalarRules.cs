using System;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace OneYaml.Compat.YamlDotNet.Internal;

// How YamlDotNet reads the text of a scalar into a .NET value. These are its documented rules and they differ from the
// YAML 1.2 Core schema: booleans include yes/no/on/off/y/n, integers take 0x, 0b, a leading-zero octal, underscores and
// base-60 forms, and the target type decides the reading rather than the text.
internal static class ScalarRules
{
    public const int DefaultDigits = 4_300;

    public static bool IsNullText(string value) => value.Length == 0 || value is "~" or "null" or "Null" or "NULL";

    public static bool ParseBool(string value)
    {
        switch (value.ToUpperInvariant())
        {
            case "TRUE":
            case "YES":
            case "Y":
            case "ON":
                return true;
            case "FALSE":
            case "NO":
            case "N":
            case "OFF":
                return false;
            default:
                throw new FormatException($"String '{value}' was not recognized as a valid Boolean.");
        }
    }

    // Converting a long integer costs more than its length, so a literal past the library's digit limit is refused
    // before any work is done on it. It is an overflow, the failure YamlDotNet gives for a number that does not fit.
    private static void CheckIntegerLength(string text, int maxDigits)
    {
        if (text.Length > (long)maxDigits + 2)
        {
            throw new OverflowException("The integer has more digits than the configured limit.");
        }
    }

    public static BigInteger ParseInteger(string text, int maxDigits = DefaultDigits)
    {
        CheckIntegerLength(text, maxDigits);
        if (text.Length > 0 && text.Length < 19 && text[0] != '0' && text[0] != '+' && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long quick) && !(text.Length > 1 && text[0] == '-' && text[1] == '0'))
        {
            return quick;
        }

        string v = text.Replace("_", string.Empty, StringComparison.Ordinal);
        if (v.Length == 0)
        {
            throw new FormatException("The input string '' was not in a correct format.");
        }

        bool negative = false;
        int i = 0;
        if (v[0] == '-')
        {
            negative = true;
            i = 1;
        }
        else if (v[0] == '+')
        {
            i = 1;
        }

        string rest = v[i..];
        BigInteger magnitude;
        if (rest.StartsWith("0x", StringComparison.Ordinal))
        {
            magnitude = ParseRadix(rest[2..], 16, text);
        }
        else if (rest.StartsWith("0b", StringComparison.Ordinal))
        {
            magnitude = ParseRadix(rest[2..], 2, text);
        }
        else if (rest.Length > 1 && rest[0] == '0')
        {
            magnitude = ParseRadix(rest[1..], 8, text);
        }
        else if (rest.Contains(':', StringComparison.Ordinal))
        {
            magnitude = BigInteger.Zero;
            foreach (string part in rest.Split(':'))
            {
                magnitude = (magnitude * 60) + ParseRadix(part, 10, text);
            }
        }
        else
        {
            magnitude = ParseRadix(rest, 10, text);
        }

        return negative ? -magnitude : magnitude;
    }

    private static BigInteger ParseRadix(string digits, int radix, string whole)
    {
        if (digits.Length == 0)
        {
            throw new FormatException($"The input string '{whole}' was not in a correct format.");
        }

        BigInteger result = BigInteger.Zero;
        foreach (char c in digits)
        {
            int d = c is >= '0' and <= '9' ? c - '0' : c is >= 'a' and <= 'f' ? c - 'a' + 10 : c is >= 'A' and <= 'F' ? c - 'A' + 10 : 99;
            if (d >= radix)
            {
                throw new FormatException($"The input string '{whole}' was not in a correct format.");
            }

            result = (result * radix) + d;
        }

        return result;
    }

    public static double ParseDouble(string text)
    {
        switch (text)
        {
            case ".inf" or ".Inf" or ".INF" or "+.inf" or "+.Inf" or "+.INF":
                return double.PositiveInfinity;
            case "-.inf" or "-.Inf" or "-.INF":
                return double.NegativeInfinity;
            case ".nan" or ".NaN" or ".NAN":
                return double.NaN;
        }

        // Only digits, a sign, a point and an exponent: the symbols .NET also accepts (Infinity, NaN) are not YAML floats.
        foreach (char c in text)
        {
            if (!(c is >= '0' and <= '9' or '.' or '+' or '-' or 'e' or 'E' or '_' or ' '))
            {
                throw new FormatException($"The input string '{text}' was not in a correct format.");
            }
        }

        return double.Parse(text.Contains('_', StringComparison.Ordinal) ? text.Replace("_", string.Empty, StringComparison.Ordinal) : text, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    // A half is rounded once from the text; going through a double first can round twice and land one step off.
    // A number too large for a half is an overflow rather than a silent infinity, which would change the value.
    private static Half ParseHalf(string text)
    {
        double asDouble = ParseDouble(text);
        if (double.IsNaN(asDouble) || double.IsInfinity(asDouble))
        {
            return (Half)asDouble;
        }

        Half result = Half.Parse(text.Contains('_', StringComparison.Ordinal) ? text.Replace("_", string.Empty, StringComparison.Ordinal) : text, NumberStyles.Float, CultureInfo.InvariantCulture);
        return Half.IsInfinity(result) ? throw new OverflowException("Value was either too large or too small for a Half.") : result;
    }

    // Reads the text as a value of a scalar type. Throws FormatException or OverflowException when it does not fit.
    public static object? Convert(string text, Type type, int maxDigits = DefaultDigits)
    {
        switch (Type.GetTypeCode(type))
        {
            case TypeCode.String:
                return text;
            case TypeCode.Boolean:
                return ParseBool(text);
            case TypeCode.Int32:
                return (int)ParseInteger(text, maxDigits);
            case TypeCode.Int64:
                return (long)ParseInteger(text, maxDigits);
            case TypeCode.Int16:
                return (short)ParseInteger(text, maxDigits);
            case TypeCode.SByte:
                return (sbyte)ParseInteger(text, maxDigits);
            case TypeCode.Byte:
                return (byte)ParseInteger(text, maxDigits);
            case TypeCode.UInt16:
                return (ushort)ParseInteger(text, maxDigits);
            case TypeCode.UInt32:
                return (uint)ParseInteger(text, maxDigits);
            case TypeCode.UInt64:
                return (ulong)ParseInteger(text, maxDigits);
            case TypeCode.Double:
                return ParseDouble(text);
            case TypeCode.Single:
                return (float)ParseDouble(text);
            case TypeCode.Decimal:
                return decimal.Parse(text.Replace("_", string.Empty, StringComparison.Ordinal), NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite | NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
            case TypeCode.Char:
                return text.Length == 1 ? text[0] : throw new FormatException("String must be exactly one character long.");
            case TypeCode.DateTime:
                return DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        }

        if (type == typeof(Guid))
        {
            return Guid.Parse(text);
        }

        if (type == typeof(DateTimeOffset))
        {
            return DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        }

        if (type == typeof(TimeSpan))
        {
            return TimeSpan.Parse(text, CultureInfo.InvariantCulture);
        }

        if (type == typeof(Uri))
        {
            return new Uri(text, UriKind.RelativeOrAbsolute);
        }

        if (type == typeof(Version))
        {
            return Version.Parse(text);
        }

        if (type == typeof(DateOnly))
        {
            return DateOnly.Parse(text, CultureInfo.InvariantCulture);
        }

        if (type == typeof(TimeOnly))
        {
            return TimeOnly.Parse(text, CultureInfo.InvariantCulture);
        }

        if (type == typeof(BigInteger))
        {
            return ParseInteger(text, maxDigits);
        }

        if (type == typeof(Int128))
        {
            return (Int128)ParseInteger(text, maxDigits);
        }

        if (type == typeof(UInt128))
        {
            return (UInt128)ParseInteger(text, maxDigits);
        }

        if (type == typeof(Half))
        {
            return ParseHalf(text);
        }

        if (type == typeof(byte[]))
        {
            return System.Convert.FromBase64String(text);
        }

        throw new InvalidCastException($"A scalar cannot be read as {type}.");
    }

    // The type a scalar takes when it is read with no target type (or as object) and carries an explicit core tag.
    public static object? FromCoreTag(string tag, string text, int maxDigits = DefaultDigits)
    {
        switch (tag)
        {
            case Tags.Str:
                return text;
            case Tags.Bool:
                return ParseBool(text);
            case Tags.Int:
                {
                    BigInteger v = ParseInteger(text, maxDigits);
                    return v >= int.MinValue && v <= int.MaxValue ? (int)v : v >= long.MinValue && v <= long.MaxValue ? (long)v : (object)v;
                }

            case Tags.Float:
                return ParseDouble(text);
            case Tags.Null:
                return IsNullText(text) ? null : throw new FormatException($"The text '{text}' is not a null value.");
            default:
                return text;
        }
    }

    // With unquoted-string type deserialization on, a plain scalar read as object takes the type its text suggests: the
    // smallest integer type that holds it, a boolean for true/false, a floating-point number, else the string. Floating
    // point is a double here; YamlDotNet picks a single first, which loses digits (123456789.123 becomes 123456790).
    public static object? Detect(string text, int maxDigits = DefaultDigits)
    {
        if (IsNullText(text))
        {
            return null;
        }

        switch (text)
        {
            case "true" or "True" or "TRUE":
                return true;
            case "false" or "False" or "FALSE":
                return false;
            case ".inf" or ".Inf" or ".INF" or "+.inf" or "+.Inf" or "+.INF":
                return double.PositiveInfinity;
            case "-.inf" or "-.Inf" or "-.INF":
                return double.NegativeInfinity;
            case ".nan" or ".NaN" or ".NAN":
                return double.NaN;
        }

        if (IsDecimalInteger(text) || (text.StartsWith("0x", StringComparison.Ordinal) && text.Length > 2 && IsHex(text.AsSpan(2))))
        {
            if (text.Length > (long)maxDigits + 2)
            {
                // Too long to read as a number; it stays text rather than failing a load that asked for no type.
                return text;
            }

            BigInteger v = text.StartsWith("0x", StringComparison.Ordinal) ? ParseInteger(text, maxDigits) : BigInteger.Parse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            if (v >= byte.MinValue && v <= byte.MaxValue)
            {
                return (byte)v;
            }

            if (v >= short.MinValue && v <= short.MaxValue)
            {
                return (short)v;
            }

            if (v >= int.MinValue && v <= int.MaxValue)
            {
                return (int)v;
            }

            if (v >= long.MinValue && v <= long.MaxValue)
            {
                return (long)v;
            }

            if (v >= 0 && v <= ulong.MaxValue)
            {
                return (ulong)v;
            }

            return (double)v;
        }

        if (IsDecimalFloat(text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
        {
            return d;
        }

        return text;
    }

    private static bool IsHex(ReadOnlySpan<char> digits)
    {
        foreach (char c in digits)
        {
            if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsDecimalInteger(string text)
    {
        int i = text.Length > 0 && text[0] is '-' or '+' ? 1 : 0;
        if (i >= text.Length)
        {
            return false;
        }

        for (; i < text.Length; i++)
        {
            if (text[i] is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsDecimalFloat(string text)
    {
        int i = text.Length > 0 && text[0] is '-' or '+' ? 1 : 0;
        int digits = 0;
        for (; i < text.Length && text[i] is >= '0' and <= '9'; i++)
        {
            digits++;
        }

        if (i < text.Length && text[i] == '.')
        {
            for (i++; i < text.Length && text[i] is >= '0' and <= '9'; i++)
            {
                digits++;
            }
        }

        if (digits == 0)
        {
            return false;
        }

        if (i < text.Length && text[i] is 'e' or 'E')
        {
            i++;
            if (i < text.Length && text[i] is '-' or '+')
            {
                i++;
            }

            int exp = 0;
            for (; i < text.Length && text[i] is >= '0' and <= '9'; i++)
            {
                exp++;
            }

            if (exp == 0)
            {
                return false;
            }
        }

        return i == text.Length;
    }

    public static string FormatNumber(object number) => System.Convert.ToString(number, CultureInfo.InvariantCulture) ?? string.Empty;

    public static bool IsPlainSyntax(string text, bool flow)
    {
        if (text.Length == 0 || text[0] == ' ' || text[^1] == ' ' || text[^1] == ':')
        {
            return false;
        }

        char first = text[0];
        if (",[]{}#&*!|>'\"%@`".Contains(first, StringComparison.Ordinal))
        {
            return false;
        }

        if ((first is '-' or '?' or ':') && (text.Length == 1 || text[1] == ' '))
        {
            return false;
        }

        if (text.StartsWith("---", StringComparison.Ordinal) || text.StartsWith("...", StringComparison.Ordinal))
        {
            return false;
        }

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c < ' ' || c == '\u007f' || c == '\u0085' || c == '\u2028' || c == '\u2029' || c == '\uFEFF')
            {
                return false;
            }

            if (c == ':' && (flow || i + 1 == text.Length || text[i + 1] == ' '))
            {
                return false;
            }

            if (c == '#' && i > 0 && text[i - 1] == ' ')
            {
                return false;
            }

            if (flow && c is ',' or '[' or ']' or '{' or '}')
            {
                return false;
            }
        }

        return true;
    }

    public static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);
}
