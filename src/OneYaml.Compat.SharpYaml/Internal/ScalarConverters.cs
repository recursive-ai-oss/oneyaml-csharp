using System;
using System.Globalization;
using Oy = global::OneYaml;

namespace OneYaml.Compat.SharpYaml;

// SharpYaml reads a null into a number, a boolean or another value type as the default value, and it reads a quoted
// "5" or "true" into a number or a boolean. The engine follows the Core schema, where a quoted scalar is a string
// and a null is not a number, so these converters add the two leniencies on top of the engine's own converter.
internal delegate bool TextParser<T>(string text, out T value);

internal sealed class LenientScalarConverter<T> : Oy.YamlConverter<T>
    where T : struct
{
    private readonly Oy.YamlConverter<T> _inner;
    private readonly TextParser<T>? _parse;
    private readonly bool _integer;

    public LenientScalarConverter(Oy.YamlConverter<T> inner, TextParser<T>? parse, bool integer)
    {
        _inner = inner;
        _parse = parse;
        _integer = integer;
    }

    public override bool HandleNull => true;

    public override T Read(ref Oy.Utf8YamlReader reader, Type typeToConvert, Oy.YamlSerializerOptions options)
    {
        if (reader.IsNull)
        {
            return default;
        }

        try
        {
            return _inner.Read(ref reader, typeToConvert, options);
        }
        catch (Oy.YamlException) when (_parse is not null && reader.TokenType == Oy.YamlTokenKind.Scalar)
        {
            string text = reader.GetString();
            if (LooksLikeNumberOrBool(text) && _parse(text, out T value) && IsFinite(value))
            {
                return value;
            }

            throw;
        }
    }

    public override void Write(Oy.Utf8YamlEmitter writer, T value, Oy.YamlSerializerOptions options) => _inner.Write(writer, value, options);

    // A finite literal that overflows the type (1e400, or 1e39 as a float) parses to infinity; that would change the
    // value, so it stays an error. This also keeps the fallback to quoted scalars in practice: a plain scalar the engine
    // refused while the text has the shape of a number is one that overflows, and an overflowing integer fails to parse.
    private static bool IsFinite(T value) => value switch
    {
        double d => double.IsFinite(d),
        float f => float.IsFinite(f),
        Half h => Half.IsFinite(h),
        _ => true,
    };

    // The shape check keeps the fallback to what a quoted number or boolean looks like: nothing the engine refused for
    // a better reason (a hexadecimal float, a word such as "Infinity") gets in through the parser's own leniency.
    private bool LooksLikeNumberOrBool(string text)
    {
        if (typeof(T) == typeof(bool))
        {
            return text is "true" or "True" or "TRUE" or "false" or "False" or "FALSE";
        }

        int i = 0;
        if (i < text.Length && text[i] is '+' or '-')
        {
            i++;
        }

        int digits = 0;
        while (i < text.Length && char.IsAsciiDigit(text[i]))
        {
            i++;
            digits++;
        }

        if (_integer)
        {
            return digits > 0 && i == text.Length;
        }

        if (i < text.Length && text[i] == '.')
        {
            i++;
            while (i < text.Length && char.IsAsciiDigit(text[i]))
            {
                i++;
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
            if (i < text.Length && text[i] is '+' or '-')
            {
                i++;
            }

            int exponent = 0;
            while (i < text.Length && char.IsAsciiDigit(text[i]))
            {
                i++;
                exponent++;
            }

            if (exponent == 0)
            {
                return false;
            }
        }

        return i == text.Length;
    }

    internal static TextParser<T>? ParserFor(Oy.YamlTypeInfo<T> inner)
    {
        const NumberStyles Int = NumberStyles.AllowLeadingSign;
        const NumberStyles Float = NumberStyles.Float;
        CultureInfo inv = CultureInfo.InvariantCulture;
        object? parser = null;
        if (typeof(T) == typeof(int))
        {
            parser = new TextParser<int>((string s, out int v) => int.TryParse(s, Int, inv, out v));
        }
        else if (typeof(T) == typeof(long))
        {
            parser = new TextParser<long>((string s, out long v) => long.TryParse(s, Int, inv, out v));
        }
        else if (typeof(T) == typeof(short))
        {
            parser = new TextParser<short>((string s, out short v) => short.TryParse(s, Int, inv, out v));
        }
        else if (typeof(T) == typeof(sbyte))
        {
            parser = new TextParser<sbyte>((string s, out sbyte v) => sbyte.TryParse(s, Int, inv, out v));
        }
        else if (typeof(T) == typeof(byte))
        {
            parser = new TextParser<byte>((string s, out byte v) => byte.TryParse(s, Int, inv, out v));
        }
        else if (typeof(T) == typeof(ushort))
        {
            parser = new TextParser<ushort>((string s, out ushort v) => ushort.TryParse(s, Int, inv, out v));
        }
        else if (typeof(T) == typeof(uint))
        {
            parser = new TextParser<uint>((string s, out uint v) => uint.TryParse(s, Int, inv, out v));
        }
        else if (typeof(T) == typeof(ulong))
        {
            parser = new TextParser<ulong>((string s, out ulong v) => ulong.TryParse(s, Int, inv, out v));
        }
        else if (typeof(T) == typeof(double))
        {
            parser = new TextParser<double>((string s, out double v) => double.TryParse(s, Float, inv, out v));
        }
        else if (typeof(T) == typeof(float))
        {
            parser = new TextParser<float>((string s, out float v) => float.TryParse(s, Float, inv, out v));
        }
        else if (typeof(T) == typeof(decimal))
        {
            parser = new TextParser<decimal>((string s, out decimal v) => decimal.TryParse(s, Float, inv, out v));
        }
        else if (typeof(T) == typeof(bool))
        {
            parser = new TextParser<bool>((string s, out bool v) => bool.TryParse(s, out v));
        }
        else if (typeof(T) == typeof(Int128))
        {
            parser = new TextParser<Int128>((string s, out Int128 v) => Int128.TryParse(s, Int, inv, out v));
        }
        else if (typeof(T) == typeof(UInt128))
        {
            parser = new TextParser<UInt128>((string s, out UInt128 v) => UInt128.TryParse(s, Int, inv, out v));
        }
        else if (typeof(T) == typeof(Half))
        {
            // The engine rounds a decimal text to Half exactly; Half.TryParse does not on every runtime.
            var info = (Oy.YamlTypeInfo<Half>)(object)inner;
            parser = new TextParser<Half>((string s, out Half v) =>
            {
                try
                {
                    v = Oy.YamlSerializer.Deserialize(s, info);
                    return true;
                }
                catch (Oy.YamlException)
                {
                    v = default;
                    return false;
                }
            });
        }

        return (TextParser<T>?)parser;
    }

    internal static bool IsInteger() =>
        typeof(T) == typeof(int) || typeof(T) == typeof(long) || typeof(T) == typeof(short) || typeof(T) == typeof(sbyte)
        || typeof(T) == typeof(byte) || typeof(T) == typeof(ushort) || typeof(T) == typeof(uint) || typeof(T) == typeof(ulong)
        || typeof(T) == typeof(Int128) || typeof(T) == typeof(UInt128);
}
