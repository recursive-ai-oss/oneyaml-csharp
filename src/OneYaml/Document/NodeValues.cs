using System;
using System.Numerics;
using OneYaml.Schema;

namespace OneYaml.Documents;

/// <summary>Reads a scalar node as a CLR value under the Core schema.</summary>
internal static class NodeValues
{
    /// <summary>Converts a scalar.</summary>
    /// <typeparam name="T">The wanted type: a string, boolean, integer, floating-point or big integer type (or the nullable form of the common ones), or <see cref="object"/>.</typeparam>
    /// <param name="s">The document.</param>
    /// <param name="node">A scalar node (aliases already followed).</param>
    /// <param name="value">The value.</param>
    /// <param name="problem">Why the scalar cannot be that type.</param>
    /// <returns>Whether the scalar holds a value of the type.</returns>
    public static bool TryGet<T>(DocState s, int node, out T value, out string? problem)
    {
        value = default!;
        problem = null;
        if (s.Node(node).Kind != NodeKind.Scalar)
        {
            problem = "The node is not a scalar.";
            return false;
        }

        ScalarKind kind = s.KindOf(node, out bool valid);
        if (!valid)
        {
            problem = "The scalar's tag does not fit its text.";
            return false;
        }

        if (typeof(T) == typeof(string))
        {
            if (kind == ScalarKind.Null && !s.HasTag(node) && (ScalarStyleOf(s, node) == Parsing.ScalarStyle.Plain))
            {
                value = default!;
                return true;
            }

            value = (T)(object)s.ScalarString(node);
            return true;
        }

        if (typeof(T) == typeof(object))
        {
            return TryObject(s, node, kind, out value, out problem);
        }

        if (kind == ScalarKind.Null)
        {
            if (default(T) is null || typeof(T) == typeof(int?) || typeof(T) == typeof(long?) || typeof(T) == typeof(double?) || typeof(T) == typeof(bool?))
            {
                value = default!;
                return true;
            }

            problem = "The scalar is null.";
            return false;
        }

        ReadOnlySpan<byte> text = s.DecodedScalar(node, out byte[]? rented);
        try
        {
            return TryTyped(text, kind, out value, out problem);
        }
        finally
        {
            DocState.ReleaseDecoded(rented);
        }
    }

    private static Parsing.ScalarStyle ScalarStyleOf(DocState s, int node) => (Parsing.ScalarStyle)s.Node(node).Style;

    private static bool TryObject<T>(DocState s, int node, ScalarKind kind, out T value, out string? problem)
    {
        problem = null;
        value = default!;
        if (kind == ScalarKind.String)
        {
            value = (T)(object)s.ScalarString(node);
            return true;
        }

        ReadOnlySpan<byte> text = s.DecodedScalar(node, out byte[]? rented);
        try
        {
            switch (kind)
            {
                case ScalarKind.Null:
                    return true;
                case ScalarKind.Bool:
                    if (CoreSchema.TryParseBool(text, out bool b))
                    {
                        value = (T)(object)b;
                        return true;
                    }

                    problem = "The boolean cannot be read.";
                    return false;
                case ScalarKind.Int:
                    if (CoreSchema.TryParseInt64(text, out long l))
                    {
                        value = (T)(object)l;
                        return true;
                    }

                    if (CoreSchema.TryParseBigInteger(text, out BigInteger big))
                    {
                        value = (T)(object)big;
                        return true;
                    }

                    problem = "The integer cannot be read.";
                    return false;
                default:
                    if (CoreSchema.TryParseDouble(text, out double d))
                    {
                        value = (T)(object)d;
                        return true;
                    }

                    problem = "The number cannot be read.";
                    return false;
            }
        }
        finally
        {
            DocState.ReleaseDecoded(rented);
        }
    }

    private static bool TryTyped<T>(ReadOnlySpan<byte> text, ScalarKind kind, out T value, out string? problem)
    {
        value = default!;
        problem = null;
        if (typeof(T) == typeof(bool) || typeof(T) == typeof(bool?))
        {
            if (kind == ScalarKind.Bool && CoreSchema.TryParseBool(text, out bool b))
            {
                value = (T)(object)b;
                return true;
            }

            problem = "The scalar is not a boolean.";
            return false;
        }

        if (kind == ScalarKind.Int)
        {
            if (typeof(T) == typeof(BigInteger) || typeof(T) == typeof(BigInteger?))
            {
                if (CoreSchema.TryParseBigInteger(text, out BigInteger big))
                {
                    value = (T)(object)big;
                    return true;
                }

                problem = "The integer cannot be read.";
                return false;
            }

            if (!CoreSchema.TryParseInt64(text, out long l))
            {
                // Beyond 64 bits: only unsigned 64-bit, floating-point and decimal can still hold it.
                if (!CoreSchema.TryParseBigInteger(text, out BigInteger huge))
                {
                    problem = "The integer cannot be read.";
                    return false;
                }

                if (typeof(T) == typeof(ulong) && huge >= 0 && huge <= ulong.MaxValue)
                {
                    value = (T)(object)(ulong)huge;
                    return true;
                }

                if (typeof(T) == typeof(double))
                {
                    value = (T)(object)(double)huge;
                    return true;
                }

                if (typeof(T) == typeof(decimal) && huge >= (BigInteger)decimal.MinValue && huge <= (BigInteger)decimal.MaxValue)
                {
                    value = (T)(object)(decimal)huge;
                    return true;
                }

                problem = "The integer is too large for the type.";
                return false;
            }

            return FromInt64(l, out value, out problem);
        }

        if (kind == ScalarKind.Float)
        {
            if (!CoreSchema.TryParseDouble(text, out double d))
            {
                problem = "The number cannot be read.";
                return false;
            }

            if (typeof(T) == typeof(double) || typeof(T) == typeof(double?))
            {
                value = (T)(object)d;
                return true;
            }

            if (typeof(T) == typeof(float))
            {
                value = (T)(object)(float)d;
                return true;
            }

            if (typeof(T) == typeof(decimal) && Math.Abs(d) < 7.9e28 && !double.IsNaN(d))
            {
                value = (T)(object)(decimal)d;
                return true;
            }
        }

        problem = "The scalar is not of the requested type.";
        return false;
    }

    private static bool FromInt64<T>(long l, out T value, out string? problem)
    {
        value = default!;
        problem = null;
        bool ok = true;
        if (typeof(T) == typeof(long) || typeof(T) == typeof(long?))
        {
            value = (T)(object)l;
        }
        else if (typeof(T) == typeof(int) || typeof(T) == typeof(int?))
        {
            ok = l is >= int.MinValue and <= int.MaxValue;
            if (ok)
            {
                value = (T)(object)(int)l;
            }
        }
        else if (typeof(T) == typeof(short))
        {
            ok = l is >= short.MinValue and <= short.MaxValue;
            if (ok)
            {
                value = (T)(object)(short)l;
            }
        }
        else if (typeof(T) == typeof(sbyte))
        {
            ok = l is >= sbyte.MinValue and <= sbyte.MaxValue;
            if (ok)
            {
                value = (T)(object)(sbyte)l;
            }
        }
        else if (typeof(T) == typeof(byte))
        {
            ok = l is >= 0 and <= byte.MaxValue;
            if (ok)
            {
                value = (T)(object)(byte)l;
            }
        }
        else if (typeof(T) == typeof(ushort))
        {
            ok = l is >= 0 and <= ushort.MaxValue;
            if (ok)
            {
                value = (T)(object)(ushort)l;
            }
        }
        else if (typeof(T) == typeof(uint))
        {
            ok = l is >= 0 and <= uint.MaxValue;
            if (ok)
            {
                value = (T)(object)(uint)l;
            }
        }
        else if (typeof(T) == typeof(ulong))
        {
            ok = l >= 0;
            if (ok)
            {
                value = (T)(object)(ulong)l;
            }
        }
        else if (typeof(T) == typeof(double) || typeof(T) == typeof(double?))
        {
            value = (T)(object)(double)l;
        }
        else if (typeof(T) == typeof(float))
        {
            value = (T)(object)(float)l;
        }
        else if (typeof(T) == typeof(decimal))
        {
            value = (T)(object)(decimal)l;
        }
        else
        {
            problem = "The scalar is not of the requested type.";
            return false;
        }

        if (!ok)
        {
            problem = "The integer does not fit the type.";
        }

        return ok;
    }
}
