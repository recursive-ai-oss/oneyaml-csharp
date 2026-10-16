using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text;
using OneYaml.Parsing;

namespace OneYaml;

internal static class BindingErrors
{
    public static YamlException Number(in Utf8YamlReader r, NumberStatus status, string type) =>
        r.CreateException(status == NumberStatus.OutOfRange
            ? $"The number{Space(r)}is outside the range of {type}."
            : $"The value{Space(r)}cannot be read as {type}.");

    public static YamlException Text(in Utf8YamlReader r, string type) => r.CreateException($"The value{Space(r)}cannot be read as {type}.");

    // " 'text' " between the words of a message, or one space when the current token has no text to show.
    private static string Space(in Utf8YamlReader r) => r.DescribeValue() is { Length: > 0 } v ? " " + v + " " : " ";
}

internal static class BuiltInConverters
{
    // Converters for types that need no generic instantiation, so they work without dynamic code.
    public static object? CreateLeaf(Type type)
    {
        if (type == typeof(string))
        {
            return new StringConverter();
        }

        if (type == typeof(bool))
        {
            return new BooleanConverter();
        }

        if (type == typeof(int))
        {
            return new Int32Converter();
        }

        if (type == typeof(long))
        {
            return new Int64Converter();
        }

        if (type == typeof(double))
        {
            return new DoubleConverter();
        }

        if (type == typeof(float))
        {
            return new SingleConverter();
        }

        if (type == typeof(decimal))
        {
            return new DecimalConverter();
        }

        if (type == typeof(short))
        {
            return new Int16Converter();
        }

        if (type == typeof(sbyte))
        {
            return new SByteConverter();
        }

        if (type == typeof(byte))
        {
            return new ByteConverter();
        }

        if (type == typeof(ushort))
        {
            return new UInt16Converter();
        }

        if (type == typeof(uint))
        {
            return new UInt32Converter();
        }

        if (type == typeof(ulong))
        {
            return new UInt64Converter();
        }

        if (type == typeof(BigInteger))
        {
            return new BigIntegerConverter();
        }

        if (type == typeof(Int128))
        {
            return new Int128Converter();
        }

        if (type == typeof(UInt128))
        {
            return new UInt128Converter();
        }

        if (type == typeof(Half))
        {
            return new HalfConverter();
        }

        if (type == typeof(nint))
        {
            return new NIntConverter();
        }

        if (type == typeof(nuint))
        {
            return new NUIntConverter();
        }

        if (type == typeof(char))
        {
            return new CharConverter();
        }

        if (type == typeof(Guid))
        {
            return new GuidConverter();
        }

        if (type == typeof(DateTime))
        {
            return new DateTimeConverter();
        }

        if (type == typeof(DateTimeOffset))
        {
            return new DateTimeOffsetConverter();
        }

        if (type == typeof(DateOnly))
        {
            return new DateOnlyConverter();
        }

        if (type == typeof(TimeOnly))
        {
            return new TimeOnlyConverter();
        }

        if (type == typeof(TimeSpan))
        {
            return new TimeSpanConverter();
        }

        if (type == typeof(Uri))
        {
            return new UriConverter();
        }

        if (type == typeof(byte[]))
        {
            return new ByteArrayConverter();
        }

        if (type == typeof(object))
        {
            return new GenericConverter();
        }

        return null;
    }

    public static object? Create(Type type, YamlSerializerOptions options) => CreateLeaf(type);
}

internal sealed class StringConverter : YamlConverter<string>
{
    public override void Write(Utf8YamlEmitter writer, string value, YamlSerializerOptions options) => writer.WriteStringValue(value);

    public override bool HandleNull => true;

    public override string? Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options)
    {
        if (reader.Type != YamlTokenType.Scalar)
        {
            throw reader.CreateException("Expected a scalar to read as a string.");
        }

        return reader.IsNull ? null : reader.GetSharedString();
    }
}

internal sealed class BooleanConverter : YamlConverter<bool>
{
    public override void Write(Utf8YamlEmitter writer, bool value, YamlSerializerOptions options) => writer.WriteBooleanValue(value);

    public override bool Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options) =>
        reader.TryReadBoolean(out bool v) == NumberStatus.Ok ? v : throw BindingErrors.Text(in reader, "a boolean");
}

internal sealed class Int32Converter : YamlConverter<int>
{
    public override void Write(Utf8YamlEmitter writer, int value, YamlSerializerOptions options) => writer.WriteNumberValue((long)value);

    public override int Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options)
    {
        NumberStatus s = reader.TryReadInt64(out long v);
        if (s == NumberStatus.Ok)
        {
            if (v is >= int.MinValue and <= int.MaxValue)
            {
                return (int)v;
            }

            s = NumberStatus.OutOfRange;
        }

        throw BindingErrors.Number(in reader, s, "a 32-bit integer");
    }
}

internal sealed class Int64Converter : YamlConverter<long>
{
    public override void Write(Utf8YamlEmitter writer, long value, YamlSerializerOptions options) => writer.WriteNumberValue(value);

    public override long Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options)
    {
        NumberStatus s = reader.TryReadInt64(out long v);
        return s == NumberStatus.Ok ? v : throw BindingErrors.Number(in reader, s, "a 64-bit integer");
    }
}

internal sealed class Int16Converter : YamlConverter<short>
{
    public override void Write(Utf8YamlEmitter writer, short value, YamlSerializerOptions options) => writer.WriteNumberValue((long)value);

    public override short Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options)
    {
        NumberStatus s = reader.TryReadInt64(out long v);
        if (s == NumberStatus.Ok)
        {
            if (v is >= short.MinValue and <= short.MaxValue)
            {
                return (short)v;
            }

            s = NumberStatus.OutOfRange;
        }

        throw BindingErrors.Number(in reader, s, "a 16-bit integer");
    }
}

internal sealed class SByteConverter : YamlConverter<sbyte>
{
    public override void Write(Utf8YamlEmitter writer, sbyte value, YamlSerializerOptions options) => writer.WriteNumberValue((long)value);

    public override sbyte Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options)
    {
        NumberStatus s = reader.TryReadInt64(out long v);
        if (s == NumberStatus.Ok)
        {
            if (v is >= sbyte.MinValue and <= sbyte.MaxValue)
            {
                return (sbyte)v;
            }

            s = NumberStatus.OutOfRange;
        }

        throw BindingErrors.Number(in reader, s, "a signed byte");
    }
}

internal sealed class ByteConverter : YamlConverter<byte>
{
    public override void Write(Utf8YamlEmitter writer, byte value, YamlSerializerOptions options) => writer.WriteNumberValue((long)value);

    public override byte Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options)
    {
        NumberStatus s = reader.TryReadInt64(out long v);
        if (s == NumberStatus.Ok)
        {
            if (v is >= byte.MinValue and <= byte.MaxValue)
            {
                return (byte)v;
            }

            s = NumberStatus.OutOfRange;
        }

        throw BindingErrors.Number(in reader, s, "a byte");
    }
}

internal sealed class UInt16Converter : YamlConverter<ushort>
{
    public override void Write(Utf8YamlEmitter writer, ushort value, YamlSerializerOptions options) => writer.WriteNumberValue((long)value);

    public override ushort Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options)
    {
        NumberStatus s = reader.TryReadInt64(out long v);
        if (s == NumberStatus.Ok)
        {
            if (v is >= ushort.MinValue and <= ushort.MaxValue)
            {
                return (ushort)v;
            }

            s = NumberStatus.OutOfRange;
        }

        throw BindingErrors.Number(in reader, s, "an unsigned 16-bit integer");
    }
}

internal sealed class UInt32Converter : YamlConverter<uint>
{
    public override void Write(Utf8YamlEmitter writer, uint value, YamlSerializerOptions options) => writer.WriteNumberValue((long)value);

    public override uint Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options)
    {
        NumberStatus s = reader.TryReadInt64(out long v);
        if (s == NumberStatus.Ok)
        {
            if (v is >= uint.MinValue and <= uint.MaxValue)
            {
                return (uint)v;
            }

            s = NumberStatus.OutOfRange;
        }

        throw BindingErrors.Number(in reader, s, "an unsigned 32-bit integer");
    }
}

internal sealed class UInt64Converter : YamlConverter<ulong>
{
    public override void Write(Utf8YamlEmitter writer, ulong value, YamlSerializerOptions options) => writer.WriteNumberValue(value);

    public override ulong Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options)
    {
        NumberStatus s = reader.TryReadUInt64(out ulong v);
        return s == NumberStatus.Ok ? v : throw BindingErrors.Number(in reader, s, "an unsigned 64-bit integer");
    }
}

internal sealed class BigIntegerConverter : YamlConverter<BigInteger>
{
    public override void Write(Utf8YamlEmitter writer, BigInteger value, YamlSerializerOptions options) => writer.WriteNumberValue(value);

    public override BigInteger Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options)
    {
        NumberStatus s = reader.TryReadBigInteger(out BigInteger v);
        return s == NumberStatus.Ok ? v : throw BindingErrors.Number(in reader, s, "an integer");
    }
}

internal sealed class Int128Converter : YamlConverter<Int128>
{
    public override void Write(Utf8YamlEmitter writer, Int128 value, YamlSerializerOptions options) => writer.WriteNumberValue((BigInteger)value);

    public override Int128 Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options)
    {
        NumberStatus s = reader.TryReadBigInteger(out BigInteger v);
        if (s == NumberStatus.Ok)
        {
            if (v >= (BigInteger)Int128.MinValue && v <= (BigInteger)Int128.MaxValue)
            {
                return (Int128)v;
            }

            s = NumberStatus.OutOfRange;
        }

        throw BindingErrors.Number(in reader, s, "a 128-bit integer");
    }
}

internal sealed class UInt128Converter : YamlConverter<UInt128>
{
    public override void Write(Utf8YamlEmitter writer, UInt128 value, YamlSerializerOptions options) => writer.WriteNumberValue((BigInteger)value);

    public override UInt128 Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options)
    {
        NumberStatus s = reader.TryReadBigInteger(out BigInteger v);
        if (s == NumberStatus.Ok)
        {
            if (v.Sign >= 0 && v <= (BigInteger)UInt128.MaxValue)
            {
                return (UInt128)v;
            }

            s = NumberStatus.OutOfRange;
        }

        throw BindingErrors.Number(in reader, s, "an unsigned 128-bit integer");
    }
}

internal sealed class NIntConverter : YamlConverter<nint>
{
    public override void Write(Utf8YamlEmitter writer, nint value, YamlSerializerOptions options) => writer.WriteNumberValue((long)value);

    public override nint Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options)
    {
        NumberStatus s = reader.TryReadInt64(out long v);
        if (s == NumberStatus.Ok)
        {
            // On a 32-bit process the native integer is narrower than a long.
            if (v >= nint.MinValue && v <= nint.MaxValue)
            {
                return (nint)v;
            }

            s = NumberStatus.OutOfRange;
        }

        throw BindingErrors.Number(in reader, s, "a native integer");
    }
}

internal sealed class NUIntConverter : YamlConverter<nuint>
{
    public override void Write(Utf8YamlEmitter writer, nuint value, YamlSerializerOptions options) => writer.WriteNumberValue((ulong)value);

    public override nuint Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options)
    {
        NumberStatus s = reader.TryReadUInt64(out ulong v);
        if (s == NumberStatus.Ok)
        {
            if (v <= nuint.MaxValue)
            {
                return (nuint)v;
            }

            s = NumberStatus.OutOfRange;
        }

        throw BindingErrors.Number(in reader, s, "an unsigned native integer");
    }
}

internal sealed class HalfConverter : YamlConverter<Half>
{
    public override void Write(Utf8YamlEmitter writer, Half value, YamlSerializerOptions options)
    {
        if (!Half.IsFinite(value))
        {
            writer.WriteNumberValue((double)value);
            return;
        }

        // The shortest text that reads back as the same Half ("0.1", not the float expansion of it), with a point or an
        // exponent mark so it stays a float: "1" would read back as an integer.
        Span<char> text = stackalloc char[32];
        value.TryFormat(text, out int written, default, CultureInfo.InvariantCulture);
        text = text.Slice(0, written);
        if (text.IndexOf('.') < 0)
        {
            int e = text.IndexOfAny('E', 'e');
            Span<char> fixedText = stackalloc char[40];
            if (e < 0)
            {
                text.CopyTo(fixedText);
                ".0".AsSpan().CopyTo(fixedText.Slice(written));
                writer.WritePlainNumberText(fixedText.Slice(0, written + 2));
                return;
            }

            text.Slice(0, e).CopyTo(fixedText);
            ".0".AsSpan().CopyTo(fixedText.Slice(e));
            text.Slice(e).CopyTo(fixedText.Slice(e + 2));
            writer.WritePlainNumberText(fixedText.Slice(0, written + 2));
            return;
        }

        writer.WritePlainNumberText(text);
    }

    public override Half Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options)
    {
        NumberStatus s = reader.TryReadHalf(out Half h);
        if (s == NumberStatus.Ok)
        {
            return h;
        }

        throw BindingErrors.Number(in reader, s, "a number");
    }
}

internal sealed class DoubleConverter : YamlConverter<double>
{
    public override void Write(Utf8YamlEmitter writer, double value, YamlSerializerOptions options) => writer.WriteNumberValue(value);

    public override double Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options)
    {
        NumberStatus s = reader.TryReadDouble(out double v);
        return s == NumberStatus.Ok ? v : throw BindingErrors.Number(in reader, s, "a number");
    }
}

internal sealed class SingleConverter : YamlConverter<float>
{
    public override void Write(Utf8YamlEmitter writer, float value, YamlSerializerOptions options) => writer.WriteNumberValue(value);

    public override float Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options)
    {
        NumberStatus s = reader.TryReadDouble(out double v);
        if (s == NumberStatus.Ok)
        {
            float f = (float)v;
            if (!float.IsInfinity(f) || double.IsInfinity(v))
            {
                return f;
            }

            s = NumberStatus.OutOfRange;
        }

        throw BindingErrors.Number(in reader, s, "a number");
    }
}

internal sealed class DecimalConverter : YamlConverter<decimal>
{
    public override void Write(Utf8YamlEmitter writer, decimal value, YamlSerializerOptions options) => writer.WriteNumberValue(value);

    public override decimal Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options)
    {
        NumberStatus s = reader.TryReadDecimal(out decimal v);
        return s == NumberStatus.Ok ? v : throw BindingErrors.Number(in reader, s, "a decimal");
    }
}

internal sealed class CharConverter : YamlConverter<char>
{
    public override void Write(Utf8YamlEmitter writer, char value, YamlSerializerOptions options) => writer.WriteStringValue(new ReadOnlySpan<char>(in value));

    public override char Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options)
    {
        reader.RequireScalar();
        string s = reader.GetString();
        return s.Length == 1 ? s[0] : throw BindingErrors.Text(in reader, "a single character");
    }
}

// Converts a scalar's text with a parser; any scalar style is accepted.
internal abstract class TextConverter<T> : YamlConverter<T>
{
    protected abstract bool TryParse(string text, out T value);

    // Formats the value in its round-trip form; the text is at most 64 characters.
    protected abstract bool TryFormat(T value, Span<char> destination, out int written);

    public override void Write(Utf8YamlEmitter writer, T value, YamlSerializerOptions options)
    {
        Span<char> buffer = stackalloc char[64];
        if (!TryFormat(value, buffer, out int written))
        {
            throw Utf8YamlEmitter.Misuse($"The value cannot be written as {Describe}.");
        }

        writer.WriteStringValue(buffer.Slice(0, written));
    }

    protected abstract string Describe { get; }

    public override T Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options)
    {
        reader.RequireScalar();
        return TryParse(reader.GetString(), out T v) ? v : throw BindingErrors.Text(in reader, Describe);
    }
}

internal sealed class GuidConverter : TextConverter<Guid>
{
    protected override bool TryFormat(Guid value, Span<char> destination, out int written) => value.TryFormat(destination, out written, "D");

    protected override string Describe => "a GUID";

    protected override bool TryParse(string text, out Guid value) => Guid.TryParse(text, out value);
}

internal sealed class DateTimeConverter : TextConverter<DateTime>
{
    protected override bool TryFormat(DateTime value, Span<char> destination, out int written) => value.TryFormat(destination, out written, "O", CultureInfo.InvariantCulture);

    protected override string Describe => "a date and time";

    protected override bool TryParse(string text, out DateTime value) =>
        DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind | DateTimeStyles.AllowWhiteSpaces, out value);
}

internal sealed class DateTimeOffsetConverter : TextConverter<DateTimeOffset>
{
    protected override bool TryFormat(DateTimeOffset value, Span<char> destination, out int written) => value.TryFormat(destination, out written, "O", CultureInfo.InvariantCulture);

    protected override string Describe => "a date and time with an offset";

    // YAML timestamps without an offset are UTC.
    protected override bool TryParse(string text, out DateTimeOffset value) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out value);
}

internal sealed class DateOnlyConverter : TextConverter<DateOnly>
{
    protected override bool TryFormat(DateOnly value, Span<char> destination, out int written) => value.TryFormat(destination, out written, "O", CultureInfo.InvariantCulture);

    protected override string Describe => "a date";

    protected override bool TryParse(string text, out DateOnly value) => DateOnly.TryParse(text, CultureInfo.InvariantCulture, out value);
}

internal sealed class TimeOnlyConverter : TextConverter<TimeOnly>
{
    protected override bool TryFormat(TimeOnly value, Span<char> destination, out int written) => value.TryFormat(destination, out written, "O", CultureInfo.InvariantCulture);

    protected override string Describe => "a time of day";

    protected override bool TryParse(string text, out TimeOnly value) => TimeOnly.TryParse(text, CultureInfo.InvariantCulture, out value);
}

internal sealed class TimeSpanConverter : TextConverter<TimeSpan>
{
    protected override bool TryFormat(TimeSpan value, Span<char> destination, out int written) => value.TryFormat(destination, out written, "c", CultureInfo.InvariantCulture);

    protected override string Describe => "a time span";

    protected override bool TryParse(string text, out TimeSpan value) => TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out value);
}

internal sealed class UriConverter : YamlConverter<Uri>
{
    public override void Write(Utf8YamlEmitter writer, Uri value, YamlSerializerOptions options) => writer.WriteStringValue(value.OriginalString);

    public override Uri? Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options)
    {
        reader.RequireScalar();
        return Uri.TryCreate(reader.GetString(), UriKind.RelativeOrAbsolute, out Uri? uri) ? uri : throw BindingErrors.Text(in reader, "a URI");
    }
}

// Binary data is Base64 text (the binary tag's form); a sequence of byte values is accepted too.
internal sealed class ByteArrayConverter : YamlConverter<byte[]>
{
    public override void Write(Utf8YamlEmitter writer, byte[] value, YamlSerializerOptions options) => writer.WriteBinaryValue(value);

    public override byte[]? Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options)
    {
        if (reader.Type == YamlTokenType.Scalar)
        {
            try
            {
                return Convert.FromBase64String(reader.GetString());
            }
            catch (FormatException)
            {
                throw BindingErrors.Text(in reader, "Base64 binary data");
            }
        }

        if (reader.Type != YamlTokenType.SequenceStart)
        {
            throw reader.CreateException("Expected Base64 text or a sequence of bytes.");
        }

        var bytes = new List<byte>();
        var inner = new ByteConverter();
        while (reader.Read() && reader.Type != YamlTokenType.SequenceEnd)
        {
            bytes.Add(inner.ReadValue(ref reader, options));
        }

        return bytes.ToArray();
    }
}

internal sealed class NullableConverter<T> : YamlConverter<T?>
    where T : struct
{
    private readonly YamlConverter<T> _inner;

    public NullableConverter(YamlConverter<T> inner) => _inner = inner;

    public override bool HandleNull => true;

    public override void Write(Utf8YamlEmitter writer, T? value, YamlSerializerOptions options)
    {
        if (value is { } inner)
        {
            _inner.WriteValue(writer, inner, options);
        }
        else
        {
            writer.WriteNullValue();
        }
    }

    public override T? Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options) =>
        reader.IsNull ? null : _inner.ReadValue(ref reader, options);
}

// Enums are written by their declared names, or by the names the enum member attributes or the enum naming
// policy give them. They read by any of those names (exact, then ignoring case) or by number.
internal sealed class EnumConverter<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] TEnum> : YamlConverter<TEnum>
    where TEnum : struct, Enum
{
    private readonly Dictionary<string, TEnum> _exact = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TEnum> _ignoreCase = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<TEnum, string> _written = [];
    private readonly Dictionary<string, string> _writtenByName = new(StringComparer.Ordinal);
    private readonly bool _renames;
    private readonly TypeCode _underlying = Type.GetTypeCode(Enum.GetUnderlyingType(typeof(TEnum)));

    public EnumConverter(YamlSerializerOptions options)
    {
        foreach (FieldInfo field in typeof(TEnum).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var value = (TEnum)field.GetValue(null)!;
            string name = field.Name;
            _exact.TryAdd(name, value);
            _ignoreCase.TryAdd(name, value);
            string writtenName = name;
            if (MemberNameOf(field) is { } attributed)
            {
                writtenName = attributed;
            }
            else if (options.EnumNamingPolicy is { } policy)
            {
                writtenName = policy.ConvertName(name);
            }

            if (writtenName != name)
            {
                _renames = true;
                _exact.TryAdd(writtenName, value);
                _ignoreCase.TryAdd(writtenName, value);
            }

            _written.TryAdd(value, writtenName);
            _writtenByName.TryAdd(name, writtenName);
        }
    }

    // The name an attribute gives a member. The attribute is found by its name so that the .NET 8 build
    // honours the System.Text.Json one too, which that runtime does not define.
    private static string? MemberNameOf(FieldInfo field)
    {
        string? name = null;
        foreach (CustomAttributeData data in field.GetCustomAttributesData())
        {
            string? full = data.AttributeType.FullName;
            if (full == "OneYaml.YamlEnumMemberNameAttribute"
                || (name is null && full == "System.Text.Json.Serialization.JsonStringEnumMemberNameAttribute"))
            {
                if (data.ConstructorArguments.Count == 1 && data.ConstructorArguments[0].Value is string given)
                {
                    name = given;
                    if (full == "OneYaml.YamlEnumMemberNameAttribute")
                    {
                        break;
                    }
                }
            }
        }

        return name;
    }

    // A declared value is written by its name. A combination of flags is written as the names of its
    // parts; a number outside the names stays a number.
    public override void Write(Utf8YamlEmitter writer, TEnum value, YamlSerializerOptions options)
    {
        if (_written.TryGetValue(value, out string? name))
        {
            writer.WriteStringValue(name);
            return;
        }

        string text = value.ToString();
        if (text.Length > 0 && (char.IsAsciiDigit(text[0]) || text[0] == '-'))
        {
            writer.WritePlainNumberText(text);
        }
        else
        {
            writer.WriteStringValue(_renames ? RenameParts(text) : text);
        }
    }

    private string RenameParts(string combination)
    {
        string[] parts = combination.Split(", ");
        for (int i = 0; i < parts.Length; i++)
        {
            if (_writtenByName.TryGetValue(parts[i], out string? renamed))
            {
                parts[i] = renamed;
            }
        }

        return string.Join(", ", parts);
    }

    // Turns a list of written names back into declared names, which Enum.TryParse understands.
    private string DeclaredParts(string combination)
    {
        string[] parts = combination.Split(',');
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i].Trim();
            parts[i] = _ignoreCase.TryGetValue(part, out TEnum single) ? single.ToString() : part;
        }

        return string.Join(", ", parts);
    }

    private bool InRange(long number) => _underlying switch
    {
        TypeCode.SByte => number is >= sbyte.MinValue and <= sbyte.MaxValue,
        TypeCode.Byte => number is >= byte.MinValue and <= byte.MaxValue,
        TypeCode.Int16 => number is >= short.MinValue and <= short.MaxValue,
        TypeCode.UInt16 => number is >= ushort.MinValue and <= ushort.MaxValue,
        TypeCode.Int32 => number is >= int.MinValue and <= int.MaxValue,
        TypeCode.UInt32 => number is >= uint.MinValue and <= uint.MaxValue,
        TypeCode.UInt64 => number >= 0,
        _ => true,
    };

    private static YamlException OutOfRange(in Utf8YamlReader reader) =>
        reader.CreateException($"The number {reader.DescribeValue()} is outside the range of {typeof(TEnum).Name}.");

    public override TEnum Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options)
    {
        reader.RequireScalar();
        NumberStatus s = reader.TryReadInt64(out long number);
        if (s == NumberStatus.Ok)
        {
            // Enum.ToObject cuts a number down to the underlying type, so check the range first.
            return InRange(number) ? (TEnum)Enum.ToObject(typeof(TEnum), number) : throw OutOfRange(in reader);
        }

        if (s == NumberStatus.OutOfRange)
        {
            if (_underlying == TypeCode.UInt64 && reader.TryReadUInt64(out ulong wide) == NumberStatus.Ok)
            {
                return (TEnum)Enum.ToObject(typeof(TEnum), wide);
            }

            throw OutOfRange(in reader);
        }

        string text = reader.GetString();
        if (_exact.TryGetValue(text, out TEnum v) || _ignoreCase.TryGetValue(text, out v))
        {
            return v;
        }

        // Flag combinations such as "Read, Write".
        if (text.Contains(',', StringComparison.Ordinal) && Enum.TryParse(_renames ? DeclaredParts(text) : text, ignoreCase: true, out v))
        {
            return v;
        }

        throw reader.CreateException($"'{YamlException.Excerpt(text, YamlException.MessageExcerpt)}' is not a value of {typeof(TEnum).Name}.");
    }
}
