using System.Collections;
using System.Globalization;
using System.Text;

namespace OneYaml.Compat.Tests;

// Runs the same input through the real library and the compat package and compares what comes out.
internal static class Differ
{
    internal static readonly string[] Types = [];

    // A canonical, comparable description of a value: type names for scalars, structure for collections, member values for objects.
    internal static string Dump(object? value, int depth = 0)
    {
        if (depth > 200)
        {
            return "<deep>";
        }

        switch (value)
        {
            case null:
                return "null";
            case string s:
                return "str:" + s.Replace("\n", "\\n", StringComparison.Ordinal);
            case char c:
                return "char:" + c;
            case bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal:
                return value.GetType().Name + ":" + Convert.ToString(value, CultureInfo.InvariantCulture);
            case DateTime dt:
                return "DateTime:" + dt.ToString("o", CultureInfo.InvariantCulture);
            case DateTimeOffset dto:
                return "DateTimeOffset:" + dto.ToString("o", CultureInfo.InvariantCulture);
            case Enum e:
                return e.GetType().Name + ":" + e;
            case IDictionary d:
                {
                    var sb = new StringBuilder("{");
                    foreach (DictionaryEntry entry in d)
                    {
                        sb.Append(Dump(entry.Key, depth + 1)).Append('=').Append(Dump(entry.Value, depth + 1)).Append(';');
                    }

                    return sb.Append('}').ToString();
                }

            case IEnumerable seq:
                {
                    var sb = new StringBuilder("[");
                    foreach (object? item in seq)
                    {
                        sb.Append(Dump(item, depth + 1)).Append(',');
                    }

                    return sb.Append(']').ToString();
                }

            default:
                {
                    Type t = value.GetType();
                    if (t.IsPrimitive || t.Namespace == "System")
                    {
                        return t.Name + ":" + Convert.ToString(value, CultureInfo.InvariantCulture);
                    }

                    var sb = new StringBuilder(t.Name + "(");
                    foreach (var f in t.GetFields().Where(f => !f.IsStatic))
                    {
                        sb.Append(f.Name).Append('=').Append(Dump(f.GetValue(value), depth + 1)).Append(';');
                    }

                    foreach (var p in t.GetProperties().Where(p => p.GetIndexParameters().Length == 0 && p.GetMethod is not null))
                    {
                        sb.Append(p.Name).Append('=').Append(Dump(p.GetValue(value), depth + 1)).Append(';');
                    }

                    return sb.Append(')').ToString();
                }
        }
    }

    // The outcome of a call: its dumped value, or the kind of failure.
    internal static string Outcome(Func<object?> call)
    {
        try
        {
            return Dump(call());
        }
        catch (Exception e)
        {
            return "EX " + Kind(e);
        }
    }

    // The failure class: both libraries wrap what goes wrong in their YamlException, so the class is the only common ground.
    internal static string Kind(Exception e) => e.GetType().Name switch
    {
        "YamlException" or "SemanticErrorException" or "SyntaxErrorException" or "AnchorNotFoundException" or "ForwardAnchorNotSupportedException" => "YamlException",
        "MaximumRecursionLevelReachedException" => "Recursion",
        var other => other,
    };
}
