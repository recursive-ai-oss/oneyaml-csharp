using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text;

namespace OneYaml.Tests.Writing;

/// <summary>
/// A canonical text for a value: the same text means the same value. Collections list their items, dictionaries
/// their entries in order, other objects their public members by name; floating-point numbers are written by bits.
/// </summary>
internal static class Canon
{
    public static string Of(object? value)
    {
        var sb = new StringBuilder();
        Write(sb, value, 0);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, object? value, int depth)
    {
        if (depth > 60)
        {
            sb.Append("<deep>");
            return;
        }

        switch (value)
        {
            case null:
                sb.Append("null");
                return;
            case string s:
                sb.Append('"').Append(s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)).Append('"');
                return;
            case double d:
                sb.Append("d:").Append(BitConverter.DoubleToInt64Bits(double.IsNaN(d) ? double.NaN : d).ToString("X", CultureInfo.InvariantCulture));
                return;
            case float f:
                sb.Append("f:").Append(BitConverter.SingleToInt32Bits(float.IsNaN(f) ? float.NaN : f).ToString("X", CultureInfo.InvariantCulture));
                return;
            case byte[] bytes:
                sb.Append("bytes:").Append(Convert.ToHexString(bytes));
                return;
            case Uri uri:
                sb.Append("uri:").Append(uri.OriginalString);
                return;
            case Enum e:
                sb.Append(e.GetType().Name).Append(':').Append(Convert.ToInt64(e, CultureInfo.InvariantCulture));
                return;
            case DateTime dt:
                sb.Append("dt:").Append(dt.Ticks).Append('/').Append((int)dt.Kind);
                return;
            case IFormattable formattable when value.GetType().IsPrimitive || value is decimal or DateTimeOffset or Guid or TimeSpan or DateOnly or TimeOnly or BigInteger:
                sb.Append(value.GetType().Name).Append(':').Append(formattable.ToString(null, CultureInfo.InvariantCulture));
                return;
            case IDictionary dictionary:
                sb.Append(value.GetType().Name).Append('{');
                foreach (DictionaryEntry entry in dictionary)
                {
                    Write(sb, entry.Key, depth + 1);
                    sb.Append('=');
                    Write(sb, entry.Value, depth + 1);
                    sb.Append(',');
                }

                sb.Append('}');
                return;
            case IEnumerable sequence:
                sb.Append(value.GetType().Name).Append('[');
                foreach (object? item in sequence)
                {
                    Write(sb, item, depth + 1);
                    sb.Append(',');
                }

                sb.Append(']');
                return;
            default:
                break;
        }

        Type type = value.GetType();
        sb.Append(type.Name).Append('(');
        var members = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (PropertyInfo p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length == 0 && p.GetMethod is not null)
            {
                members[p.Name] = p.GetValue(value);
            }
        }

        foreach (FieldInfo f in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            members[f.Name] = f.GetValue(value);
        }

        foreach (KeyValuePair<string, object?> member in members)
        {
            sb.Append(member.Key).Append('=');
            Write(sb, member.Value, depth + 1);
            sb.Append(';');
        }

        sb.Append(')');
    }
}
