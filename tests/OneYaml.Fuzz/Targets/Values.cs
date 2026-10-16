using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text;

namespace OneYaml.Fuzzing;

/// <summary>Comparing and printing values read by the library.</summary>
internal static class Values
{
    private sealed class PairComparer : IEqualityComparer<(object, object)>
    {
        public bool Equals((object, object) x, (object, object) y) => ReferenceEquals(x.Item1, y.Item1) && ReferenceEquals(x.Item2, y.Item2);

        public int GetHashCode((object, object) p) => HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(p.Item1), System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(p.Item2));
    }

    /// <summary>
    /// Compares two generic-load results (null, bool, long, BigInteger, double, string, Dictionary, List) and
    /// describes the first difference. Shared nodes are compared once per pair, so an alias-heavy value costs
    /// its own size and not the size it expands to.
    /// </summary>
    public static string? Diff(object? expected, object? actual)
    {
        var seen = new HashSet<(object, object)>(new PairComparer());
        var work = new Stack<(object? E, object? A, string P)>();
        work.Push((expected, actual, "$"));
        long steps = 0;
        while (work.Count > 0)
        {
            (object? e, object? a, string p) = work.Pop();
            if (++steps > 4_000_000)
            {
                return null;
            }

            if (e is null || a is null)
            {
                if (e is not null || a is not null)
                {
                    return $"{p}: {Show(e)} vs {Show(a)}";
                }

                continue;
            }

            if (e.GetType() != a.GetType())
            {
                return $"{p}: type {e.GetType().Name} vs {a.GetType().Name}";
            }

            switch (e)
            {
                case double d:
                    if (BitConverter.DoubleToInt64Bits(d) != BitConverter.DoubleToInt64Bits((double)a) && !(double.IsNaN(d) && double.IsNaN((double)a)))
                    {
                        return $"{p}: {d:R} vs {(double)a:R}";
                    }

                    break;
                case IDictionary<string, object?> map:
                    {
                        if (!seen.Add((e, a)))
                        {
                            break;
                        }

                        var other = (IDictionary<string, object?>)a;
                        if (map.Count != other.Count)
                        {
                            return $"{p}: {map.Count} entries vs {other.Count}";
                        }

                        using IEnumerator<KeyValuePair<string, object?>> x = map.GetEnumerator();
                        using IEnumerator<KeyValuePair<string, object?>> y = other.GetEnumerator();
                        while (x.MoveNext() && y.MoveNext())
                        {
                            if (x.Current.Key != y.Current.Key)
                            {
                                return $"{p}: key '{x.Current.Key}' vs '{y.Current.Key}'";
                            }

                            work.Push((x.Current.Value, y.Current.Value, p + "/" + x.Current.Key));
                        }

                        break;
                    }

                case IList<object?> list:
                    {
                        if (!seen.Add((e, a)))
                        {
                            break;
                        }

                        var other = (IList<object?>)a;
                        if (list.Count != other.Count)
                        {
                            return $"{p}: {list.Count} items vs {other.Count}";
                        }

                        for (int i = 0; i < list.Count; i++)
                        {
                            work.Push((list[i], other[i], p + "/" + i.ToString(CultureInfo.InvariantCulture)));
                        }

                        break;
                    }

                default:
                    if (!e.Equals(a))
                    {
                        return $"{p}: {Show(e)} vs {Show(a)}";
                    }

                    break;
            }
        }

        return null;
    }

    private static string Show(object? v) => v is null ? "null" : v.GetType().Name + ":" + v;

    /// <summary>
    /// Prints a typed result: public properties and fields by name, collections by enumeration, the system types
    /// by their text. Two equal values print equally, so the printouts of the reflection and the generated path compare.
    /// </summary>
    public static string Render(object? value)
    {
        var sb = new StringBuilder();
        Write(sb, value, 0);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, object? v, int depth)
    {
        if (depth > 200)
        {
            sb.Append("<deep>");
            return;
        }

        // A default ImmutableArray is not enumerable; it is a value the binder can leave behind.
        if (v is not null && v.GetType() is { IsGenericType: true } gt && gt.GetGenericTypeDefinition() == typeof(System.Collections.Immutable.ImmutableArray<>)
            && (bool)gt.GetProperty("IsDefault")!.GetValue(v)!)
        {
            sb.Append("default-array");
            return;
        }

        switch (v)
        {
            case null:
                sb.Append("null");
                return;
            case string s:
                sb.Append('"').Append(s).Append('"');
                return;
            case double d:
                sb.Append(double.IsNaN(d) ? "NaN" : d.ToString("R", CultureInfo.InvariantCulture));
                return;
            case float f:
                sb.Append(float.IsNaN(f) ? "NaN" : f.ToString("R", CultureInfo.InvariantCulture));
                return;
            case BigInteger b:
                sb.Append(b.ToString(CultureInfo.InvariantCulture));
                return;
            case IDictionary map:
                sb.Append('{');
                foreach (DictionaryEntry entry in map)
                {
                    Write(sb, entry.Key, depth + 1);
                    sb.Append(':');
                    Write(sb, entry.Value, depth + 1);
                    sb.Append(',');
                }

                sb.Append('}');
                return;
            case IEnumerable seq:
                sb.Append('[');
                foreach (object? item in seq)
                {
                    Write(sb, item, depth + 1);
                    sb.Append(',');
                }

                sb.Append(']');
                return;
        }

        Type t = v.GetType();
        if (t.IsPrimitive || t.IsEnum || t.Namespace is "System" || (t.Namespace?.StartsWith("System.", StringComparison.Ordinal) ?? false) && !t.IsGenericType)
        {
            sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture));
            return;
        }

        sb.Append(t.Name).Append('(');
        foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            if (p.GetIndexParameters().Length == 0 && p.CanRead)
            {
                sb.Append(p.Name).Append('=');
                Write(sb, p.GetValue(v), depth + 1);
                sb.Append(';');
            }
        }

        foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.Instance).OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            sb.Append(f.Name).Append('=');
            Write(sb, f.GetValue(v), depth + 1);
            sb.Append(';');
        }

        sb.Append(')');
    }
}
