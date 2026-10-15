using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;
using OneYaml.Parsing;

namespace OneYaml;

internal static class BindingErrors
{
    public static YamlException Number(in Utf8YamlReader r, NumberStatus status, string type) =>
        r.CreateException(status == NumberStatus.OutOfRange
            ? $"The number is outside the range of {type}."
            : $"The value cannot be read as {type}.");

    public static YamlException Text(in Utf8YamlReader r, string type) => r.CreateException($"The value cannot be read as {type}.");
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
        return s == NumberStatus.Ok ? (float)v : throw BindingErrors.Number(in reader, s, "a number");
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

// Enums read by name (exact, then through the naming policy, then ignoring case) or by number.
internal sealed class EnumConverter<TEnum> : YamlConverter<TEnum>
    where TEnum : struct, Enum
{
    private readonly Dictionary<string, TEnum> _exact = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TEnum> _ignoreCase = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<TEnum, string> _written = [];

    public EnumConverter(YamlSerializerOptions options)
    {
        foreach (TEnum value in Enum.GetValues<TEnum>())
        {
            string name = value.ToString();
            _exact.TryAdd(name, value);
            _ignoreCase.TryAdd(name, value);
            string writtenName = name;
            if (options.PropertyNamingPolicy is { } policy)
            {
                string converted = policy.ConvertName(name);
                _exact.TryAdd(converted, value);
                _ignoreCase.TryAdd(converted, value);
                writtenName = converted;
            }

            _written.TryAdd(value, writtenName);
        }
    }

    // A declared value is written by its name, through the naming policy. A combination of flags is written
    // with the plain names, which the reader parses whatever the policy; a number outside the names stays a number.
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
            writer.WriteStringValue(text);
        }
    }

    public override TEnum Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options)
    {
        reader.RequireScalar();
        NumberStatus s = reader.TryReadInt64(out long number);
        if (s == NumberStatus.Ok)
        {
            return (TEnum)Enum.ToObject(typeof(TEnum), number);
        }

        string text = reader.GetString();
        if (_exact.TryGetValue(text, out TEnum v) || _ignoreCase.TryGetValue(text, out v))
        {
            return v;
        }

        // Flag combinations such as "Read, Write".
        if (text.Contains(',', StringComparison.Ordinal) && Enum.TryParse(text, ignoreCase: true, out v))
        {
            return v;
        }

        throw reader.CreateException($"'{text}' is not a value of {typeof(TEnum).Name}.");
    }
}
