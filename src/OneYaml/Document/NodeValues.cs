using System;
using System.Numerics;
using OneYaml.Schema;

namespace OneYaml.Documents;

/// <summary>
/// Reads a scalar node as a CLR value. The conversion is the binder's own: the node is replayed to
/// the same converters <see cref="YamlSerializer"/> uses, so both read a scalar to the same value or
/// the same error.
/// </summary>
internal static class NodeValues
{
    private static readonly StringConverter s_String = new();
    private static readonly BooleanConverter s_Boolean = new();
    private static readonly Int32Converter s_Int32 = new();
    private static readonly Int64Converter s_Int64 = new();
    private static readonly Int16Converter s_Int16 = new();
    private static readonly SByteConverter s_SByte = new();
    private static readonly ByteConverter s_Byte = new();
    private static readonly UInt16Converter s_UInt16 = new();
    private static readonly UInt32Converter s_UInt32 = new();
    private static readonly UInt64Converter s_UInt64 = new();
    private static readonly BigIntegerConverter s_Big = new();
    private static readonly DoubleConverter s_Double = new();
    private static readonly SingleConverter s_Single = new();
    private static readonly DecimalConverter s_Decimal = new();
    private static readonly CharConverter s_Char = new();
    private static readonly GuidConverter s_Guid = new();
    private static readonly DateTimeConverter s_DateTime = new();
    private static readonly DateTimeOffsetConverter s_DateTimeOffset = new();
    private static readonly DateOnlyConverter s_DateOnly = new();
    private static readonly TimeOnlyConverter s_TimeOnly = new();
    private static readonly TimeSpanConverter s_TimeSpan = new();

    /// <summary>Converts a scalar.</summary>
    /// <typeparam name="T">The wanted type: a string, boolean, integer, floating-point, big integer, character, date or time, GUID type (or the nullable form of a value type), or <see cref="object"/>.</typeparam>
    /// <param name="s">The document.</param>
    /// <param name="node">A node (aliases already followed).</param>
    /// <param name="options">Options carrying the limits.</param>
    /// <param name="value">The value.</param>
    /// <param name="error">Why the node cannot be that type. Built only when <paramref name="wantError"/> is set: an error costs a line and column lookup, and a caller that only asks "is it?" must not pay it per miss.</param>
    /// <param name="wantError">Whether the caller reads <paramref name="error"/>.</param>
    /// <returns>Whether the node holds a value of the type.</returns>
    /// <exception cref="YamlException">A limit is exceeded (<see cref="YamlErrorCategory.Limit"/>).</exception>
    public static bool TryGet<T>(DocState s, int node, YamlSerializerOptions options, out T value, out YamlException? error, bool wantError = true)
    {
        value = default!;
        error = null;
        ref readonly CstNode n = ref s.Node(node);
        if (n.Kind != NodeKind.Scalar)
        {
            error = wantError ? YamlException.Located(YamlErrorCategory.Binding, "The node is not a scalar.", s.Text, n.Start, ErrorPath.Find(s.Text, n.Start, atCollection: true)) : null;
            return false;
        }

        ScalarKind kind = s.KindOf(node, out bool valid);
        if (!valid)
        {
            error = wantError ? YamlException.Located(YamlErrorCategory.Binding, "The scalar's tag does not fit its text.", s.Text, n.Start, ErrorPath.Find(s.Text, n.Start, atCollection: false)) : null;
            return false;
        }

        if (kind == ScalarKind.Null)
        {
            // A null is a null for every type that can hold one.
            if (default(T) is null)
            {
                return true;
            }

            error = wantError ? YamlException.Located(YamlErrorCategory.Binding, "The scalar is null.", s.Text, n.Start, ErrorPath.Find(s.Text, n.Start, atCollection: false)) : null;
            return false;
        }

        // A try-read of a number or boolean from text of another kind is the common miss. Answer it from the
        // resolved kind instead of replaying the node into a converter that throws and is caught.
        if (!wantError && !KindMayFit(typeof(T), kind))
        {
            return false;
        }

        AnchorBook book = NodeReplay.Record(s, node, out int count);
        var reader = new Utf8YamlReader(s.Text, options, book, count);

        // A caller that never sees the error must not pay for a line, column and path lookup in it.
        reader.QuietErrors = !wantError;
        try
        {
            reader.BeginReplay();
            return TryLeaf(ref reader, options, out value, out error);
        }
        finally
        {
            reader.Dispose();
        }
    }

    /// <summary>Whether <see cref="TryGet{T}"/> reads the type itself: the scalar types it lists, their nullable forms and <see cref="object"/>.</summary>
    public static bool IsLeafType(Type type)
    {
        Type u = Nullable.GetUnderlyingType(type) ?? type;
        return u == typeof(object) || u == typeof(string) || u == typeof(bool) || u == typeof(int) || u == typeof(long)
            || u == typeof(short) || u == typeof(sbyte) || u == typeof(byte) || u == typeof(ushort) || u == typeof(uint)
            || u == typeof(ulong) || u == typeof(BigInteger) || u == typeof(double) || u == typeof(float)
            || u == typeof(decimal) || u == typeof(char) || u == typeof(Guid) || u == typeof(DateTime)
            || u == typeof(DateTimeOffset) || u == typeof(DateOnly) || u == typeof(TimeOnly) || u == typeof(TimeSpan);
    }

    private static bool KindMayFit(Type type, ScalarKind kind)
    {
        Type u = Nullable.GetUnderlyingType(type) ?? type;
        if (u == typeof(bool))
        {
            return kind == ScalarKind.Bool;
        }

        if (u == typeof(int) || u == typeof(long) || u == typeof(short) || u == typeof(sbyte) || u == typeof(byte)
            || u == typeof(ushort) || u == typeof(uint) || u == typeof(ulong) || u == typeof(BigInteger)
            || u == typeof(double) || u == typeof(float) || u == typeof(decimal))
        {
            return kind is ScalarKind.Int or ScalarKind.Float;
        }

        return true;
    }

    private static bool TryLeaf<T>(ref Utf8YamlReader r, YamlSerializerOptions o, out T value, out YamlException? error)
    {
        value = default!;
        error = null;
        Type type = typeof(T);
        Type u = Nullable.GetUnderlyingType(type) ?? type;
        try
        {
            object? boxed;
            if (u == typeof(object))
            {
                boxed = r.ReadGenericScalar();
            }
            else if (!TryRead(ref r, o, u, out boxed))
            {
                error = r.CreateException("The scalar is not of the requested type.");
                return false;
            }

            value = (T)boxed!;
            return true;
        }
        catch (YamlException ex) when (ex.Category == YamlErrorCategory.Binding)
        {
            error = ex;
            return false;
        }
    }

    private static bool TryRead(ref Utf8YamlReader r, YamlSerializerOptions o, Type u, out object? v)
    {
        v = null;
        if (u == typeof(string))
        {
            v = s_String.Read(ref r, u, o);
        }
        else if (u == typeof(bool))
        {
            v = s_Boolean.Read(ref r, u, o);
        }
        else if (u == typeof(int))
        {
            v = s_Int32.Read(ref r, u, o);
        }
        else if (u == typeof(long))
        {
            v = s_Int64.Read(ref r, u, o);
        }
        else if (u == typeof(short))
        {
            v = s_Int16.Read(ref r, u, o);
        }
        else if (u == typeof(sbyte))
        {
            v = s_SByte.Read(ref r, u, o);
        }
        else if (u == typeof(byte))
        {
            v = s_Byte.Read(ref r, u, o);
        }
        else if (u == typeof(ushort))
        {
            v = s_UInt16.Read(ref r, u, o);
        }
        else if (u == typeof(uint))
        {
            v = s_UInt32.Read(ref r, u, o);
        }
        else if (u == typeof(ulong))
        {
            v = s_UInt64.Read(ref r, u, o);
        }
        else if (u == typeof(BigInteger))
        {
            v = s_Big.Read(ref r, u, o);
        }
        else if (u == typeof(double))
        {
            v = s_Double.Read(ref r, u, o);
        }
        else if (u == typeof(float))
        {
            v = s_Single.Read(ref r, u, o);
        }
        else if (u == typeof(decimal))
        {
            v = s_Decimal.Read(ref r, u, o);
        }
        else if (u == typeof(char))
        {
            v = s_Char.Read(ref r, u, o);
        }
        else if (u == typeof(Guid))
        {
            v = s_Guid.Read(ref r, u, o);
        }
        else if (u == typeof(DateTime))
        {
            v = s_DateTime.Read(ref r, u, o);
        }
        else if (u == typeof(DateTimeOffset))
        {
            v = s_DateTimeOffset.Read(ref r, u, o);
        }
        else if (u == typeof(DateOnly))
        {
            v = s_DateOnly.Read(ref r, u, o);
        }
        else if (u == typeof(TimeOnly))
        {
            v = s_TimeOnly.Read(ref r, u, o);
        }
        else if (u == typeof(TimeSpan))
        {
            v = s_TimeSpan.Read(ref r, u, o);
        }
        else
        {
            return false;
        }

        return true;
    }
}
