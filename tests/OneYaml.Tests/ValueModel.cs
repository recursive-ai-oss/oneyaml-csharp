using System.Numerics;
using System.Text.Json;

namespace OneYaml.Tests;

/// <summary>
/// Compares a generically loaded value with a kit value (JSON with the kit's escape forms). It walks
/// with an explicit stack because kit values can be a hundred thousand levels deep.
/// </summary>
internal static class ValueModel
{
    internal static readonly JsonDocumentOptions Deep = new() { MaxDepth = int.MaxValue / 2 };

    internal sealed class Report
    {
        /// <summary>Whether the expected value held a tagged node whose tag the generic model cannot keep.</summary>
        public bool SawTag { get; set; }

        /// <summary>Whether a mapping with a non-string key was compared by the key's source text.</summary>
        public bool SawNonStringKey { get; set; }
    }

    /// <summary>The first difference, or null when the values are equal.</summary>
    internal static string? Diff(object? actual, JsonElement expected, Report report)
    {
        var stack = new Stack<(object? Actual, JsonElement Expected, string Path)>();
        stack.Push((actual, expected, "$"));
        while (stack.Count > 0)
        {
            (object? a, JsonElement e, string path) = stack.Pop();
            string? problem = Step(a, e, path, stack, report);
            if (problem is not null)
            {
                return problem;
            }
        }

        return null;
    }

    private static string Child(string path, string key) => path.Length > 160 ? path : path + "/" + key;

    private static string? Step(object? a, JsonElement e, string path, Stack<(object? Actual, JsonElement Expected, string Path)> stack, Report report)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Null:
                return a is null ? null : $"{path}: expected null, got {Describe(a)}";
            case JsonValueKind.True:
            case JsonValueKind.False:
                return a is bool b && b == (e.ValueKind == JsonValueKind.True) ? null : $"{path}: expected {e.ValueKind}, got {Describe(a)}";
            case JsonValueKind.String:
                return a is string s && s == e.GetString() ? null : $"{path}: expected string '{Short(e.GetString())}', got {Describe(a)}";
            case JsonValueKind.Number:
                return Number(a, e, path);
            case JsonValueKind.Array:
                {
                    if (a is not List<object?> list)
                    {
                        return $"{path}: expected a sequence, got {Describe(a)}";
                    }

                    if (list.Count != e.GetArrayLength())
                    {
                        return $"{path}: expected {e.GetArrayLength()} items, got {list.Count}";
                    }

                    int i = 0;
                    foreach (JsonElement item in e.EnumerateArray())
                    {
                        stack.Push((list[i], item, Child(path, i.ToString(System.Globalization.CultureInfo.InvariantCulture))));
                        i++;
                    }

                    return null;
                }

            case JsonValueKind.Object:
                return ObjectStep(a, e, path, stack, report);
            default:
                return $"{path}: unexpected expected kind {e.ValueKind}";
        }
    }

    private static string? ObjectStep(object? a, JsonElement e, string path, Stack<(object? Actual, JsonElement Expected, string Path)> stack, Report report)
    {
        JsonProperty? first = null;
        int count = 0;
        foreach (JsonProperty p in e.EnumerateObject())
        {
            first ??= p;
            count++;
        }

        if (count == 1 && first!.Value.Name.StartsWith('$'))
        {
            JsonProperty only = first.Value;
            switch (only.Name)
            {
                case "$float":
                    {
                        string kind = only.Value.GetString()!;
                        bool ok = a is double d && (kind switch
                        {
                            "nan" => double.IsNaN(d),
                            "inf" => double.IsPositiveInfinity(d),
                            _ => double.IsNegativeInfinity(d),
                        });
                        return ok ? null : $"{path}: expected {kind}, got {Describe(a)}";
                    }

                case "$tagged":
                    report.SawTag = true;
                    stack.Push((a, only.Value.GetProperty("value"), path));
                    return null;
                case "$map":
                    return MapStep(a, only.Value, path, stack, report);
                default:
                    return $"{path}: unknown escape {only.Name}";
            }
        }

        if (a is not Dictionary<string, object?> dict)
        {
            return $"{path}: expected a mapping, got {Describe(a)}";
        }

        if (dict.Count != count)
        {
            return $"{path}: expected {count} keys, got {dict.Count}";
        }

        foreach (JsonProperty p in e.EnumerateObject())
        {
            if (!dict.TryGetValue(p.Name, out object? v))
            {
                return $"{path}: missing key '{Short(p.Name)}'";
            }

            stack.Push((v, p.Value, Child(path, p.Name)));
        }

        return null;
    }

    private static string? MapStep(object? a, JsonElement pairs, string path, Stack<(object? Actual, JsonElement Expected, string Path)> stack, Report report)
    {
        if (a is not Dictionary<string, object?> dict)
        {
            return $"{path}: expected a mapping, got {Describe(a)}";
        }

        if (dict.Count != pairs.GetArrayLength())
        {
            return $"{path}: expected {pairs.GetArrayLength()} keys, got {dict.Count}";
        }

        foreach (JsonElement pair in pairs.EnumerateArray())
        {
            JsonElement key = pair[0];
            string? text = key.ValueKind switch
            {
                JsonValueKind.String => key.GetString(),
                _ => null,
            };
            if (text is null)
            {
                // A non-string key is a mapping key's source text in the generic model; only the count and
                // the values of the keys that are strings can be compared exactly.
                report.SawNonStringKey = true;
                continue;
            }

            if (!dict.TryGetValue(text, out object? v))
            {
                return $"{path}: missing key '{Short(text)}'";
            }

            stack.Push((v, pair[1], Child(path, text)));
        }

        return null;
    }

    private static string? Number(object? a, JsonElement e, string path)
    {
        string raw = e.GetRawText();
        bool isFloat = raw.AsSpan().IndexOfAny('.', 'e', 'E') >= 0;
        if (isFloat)
        {
            return a is double d && d == double.Parse(raw, System.Globalization.CultureInfo.InvariantCulture)
                ? null
                : $"{path}: expected float {raw}, got {Describe(a)}";
        }

        BigInteger want = BigInteger.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
        return a switch
        {
            long l when l == want => null,
            BigInteger b when b == want => null,
            _ => $"{path}: expected integer {raw}, got {Describe(a)}",
        };
    }

    private static string Describe(object? a) => a switch
    {
        null => "null",
        string s => $"string '{Short(s)}'",
        Dictionary<string, object?> d => $"mapping of {d.Count}",
        List<object?> l => $"sequence of {l.Count}",
        _ => $"{a.GetType().Name} {a}",
    };

    private static string Short(string? s) => s is null ? "null" : s.Length > 60 ? s[..60] + "..." : s;

    /// <summary>Splits a file of concatenated JSON values, as the suite's in.json files are.</summary>
    internal static List<JsonDocument> ParseValues(byte[] json)
    {
        var docs = new List<JsonDocument>();
        int offset = 0;
        while (true)
        {
            while (offset < json.Length && json[offset] is (byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t')
            {
                offset++;
            }

            if (offset >= json.Length)
            {
                return docs;
            }

            var reader = new Utf8JsonReader(json.AsSpan(offset), new JsonReaderOptions { MaxDepth = int.MaxValue / 2 });
            reader.Read();
            docs.Add(JsonDocument.ParseValue(ref reader));
            offset += (int)reader.BytesConsumed;
        }
    }
}
