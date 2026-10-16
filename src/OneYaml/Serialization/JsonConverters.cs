using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OneYaml;

// Members of the System.Text.Json document types (JsonElement, JsonNode and its subclasses) are read as an untyped
// value and turned into the JSON type, and written the other way. Both directions walk with explicit stacks, so the
// depth of the data costs heap, never call depth.
internal static class JsonTrees
{
    // The nesting System.Text.Json itself reads by default; the types are not meant for more.
    internal const int MaxJsonDepth = 64;

    private struct BuildFrame
    {
        public Dictionary<string, object?>.Enumerator Map;
        public List<object?>.Enumerator List;
        public JsonObject? Object;
        public JsonArray? Array;
        public string? Key;
    }

    // From the value of an untyped load to a node, or null for a YAML null.
    public static JsonNode? ToNode(object? tree, in Utf8YamlReader reader)
    {
        if (tree is not (Dictionary<string, object?> or List<object?>))
        {
            return Leaf(tree, in reader);
        }

        var stack = new List<BuildFrame>();
        JsonNode? result = null;
        Push(stack, tree, in reader);
        while (stack.Count > 0)
        {
            BuildFrame top = stack[^1];
            object? child;
            string? key = null;
            if (top.Object is not null)
            {
                if (!top.Map.MoveNext())
                {
                    result = Finish(stack, top.Object);
                    continue;
                }

                key = top.Map.Current.Key;
                child = top.Map.Current.Value;
            }
            else
            {
                if (!top.List.MoveNext())
                {
                    result = Finish(stack, top.Array!);
                    continue;
                }

                child = top.List.Current;
            }

            // The enumerators are structs held in the list: write the moved copy back.
            stack[^1] = top;
            if (child is Dictionary<string, object?> or List<object?>)
            {
                Push(stack, child, in reader);
                stack[^2] = WithKey(stack[^2], key);
            }
            else
            {
                Attach(top, key, Leaf(child, in reader));
            }
        }

        return result;
    }

    private static BuildFrame WithKey(BuildFrame frame, string? key)
    {
        frame.Key = key;
        return frame;
    }

    private static void Push(List<BuildFrame> stack, object? tree, in Utf8YamlReader reader)
    {
        if (stack.Count >= MaxJsonDepth)
        {
            throw reader.CreateException($"The value is nested deeper than {MaxJsonDepth} levels, which the System.Text.Json types hold.");
        }

        if (tree is Dictionary<string, object?> map)
        {
            stack.Add(new BuildFrame { Map = map.GetEnumerator(), Object = new JsonObject() });
        }
        else
        {
            stack.Add(new BuildFrame { List = ((List<object?>)tree!).GetEnumerator(), Array = new JsonArray() });
        }
    }

    // A finished collection leaves the stack and goes into the one below it, or is the result.
    private static JsonNode? Finish(List<BuildFrame> stack, JsonNode done)
    {
        stack.RemoveAt(stack.Count - 1);
        if (stack.Count == 0)
        {
            return done;
        }

        BuildFrame parent = stack[^1];
        Attach(parent, parent.Key, done);
        return null;
    }

    private static void Attach(BuildFrame parent, string? key, JsonNode? value)
    {
        if (parent.Object is not null)
        {
            parent.Object[key!] = value;
        }
        else
        {
            parent.Array!.Add(value);
        }
    }

    private static JsonNode? Leaf(object? value, in Utf8YamlReader reader)
    {
        switch (value)
        {
            case null:
                return null;
            case string s:
                return JsonValue.Create(s);
            case bool b:
                return JsonValue.Create(b);
            case long l:
                return JsonValue.Create(l);
            case double d:
                if (!double.IsFinite(d))
                {
                    throw reader.CreateException("A JSON number cannot be infinite or not a number.");
                }

                return JsonValue.Create(d);
            case BigInteger big:
                return JsonNode.Parse(big.ToString(CultureInfo.InvariantCulture));
            default:
                throw reader.CreateException($"A value of type {value.GetType().Name} has no JSON form.");
        }
    }

    public static JsonElement ToElement(object? tree, in Utf8YamlReader reader)
    {
        JsonNode? node = ToNode(tree, in reader);
        using JsonDocument document = JsonDocument.Parse(node is null ? "null" : node.ToJsonString());
        return document.RootElement.Clone();
    }

    // ---- JSON types to an untyped value ----

    private struct WalkFrame
    {
        public JsonElement.ObjectEnumerator Object;
        public JsonElement.ArrayEnumerator Array;
        public Dictionary<string, object?>? Map;
        public List<object?>? List;
        public string? Key;
    }

    public static object? FromElement(JsonElement element)
    {
        if (element.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
        {
            return ScalarOf(element);
        }

        var stack = new List<WalkFrame>();
        stack.Add(Open(element));
        object? result = null;
        while (stack.Count > 0)
        {
            WalkFrame top = stack[^1];
            JsonElement child;
            string? key = null;
            if (top.Map is not null)
            {
                if (!top.Object.MoveNext())
                {
                    result = Close(stack, top.Map);
                    continue;
                }

                key = top.Object.Current.Name;
                child = top.Object.Current.Value;
            }
            else
            {
                if (!top.Array.MoveNext())
                {
                    result = Close(stack, top.List!);
                    continue;
                }

                child = top.Array.Current;
            }

            stack[^1] = top;
            if (child.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                stack[^1] = WithWalkKey(top, key);
                stack.Add(Open(child));
            }
            else
            {
                Add(top, key, ScalarOf(child));
            }
        }

        return result;
    }

    private static WalkFrame WithWalkKey(WalkFrame frame, string? key)
    {
        frame.Key = key;
        return frame;
    }

    private static WalkFrame Open(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object
            ? new WalkFrame { Object = element.EnumerateObject(), Map = new Dictionary<string, object?>(StringComparer.Ordinal) }
            : new WalkFrame { Array = element.EnumerateArray(), List = [] };

    private static object? Close(List<WalkFrame> stack, object done)
    {
        stack.RemoveAt(stack.Count - 1);
        if (stack.Count == 0)
        {
            return done;
        }

        WalkFrame parent = stack[^1];
        Add(parent, parent.Key, done);
        return null;
    }

    private static void Add(WalkFrame parent, string? key, object? value)
    {
        if (parent.Map is not null)
        {
            parent.Map[key!] = value;
        }
        else
        {
            parent.List!.Add(value);
        }
    }

    private static object? ScalarOf(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.String:
                return e.GetString();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Number:
                {
                    if (e.TryGetInt64(out long l))
                    {
                        return l;
                    }

                    string text = e.GetRawText();
                    if (text.AsSpan().IndexOfAny('.', 'e', 'E') < 0 && BigInteger.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out BigInteger big))
                    {
                        return big;
                    }

                    return e.GetDouble();
                }

            default:
                // Null, and the Undefined of a default element, which holds no value.
                return null;
        }
    }

    public static object? FromNode(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        // Nodes are walked through their JSON form: one reader of JSON text, bounded by what a node can hold.
        using JsonDocument document = JsonDocument.Parse(node.ToJsonString(), new JsonDocumentOptions { MaxDepth = int.MaxValue });
        return FromElement(document.RootElement);
    }
}

internal sealed class JsonElementConverter : YamlConverter<JsonElement>
{
    public override bool HandleNull => true;

    public override JsonElement Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options) =>
        JsonTrees.ToElement(GenericLoader.Load(ref reader), in reader);

    public override void Write(Utf8YamlEmitter writer, JsonElement value, YamlSerializerOptions options) =>
        GenericWriter.Write(writer, JsonTrees.FromElement(value)!, options);
}

internal sealed class JsonNodeConverter<T> : YamlConverter<T>
    where T : JsonNode
{
    public override bool HandleNull => true;

    public override T? Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options)
    {
        JsonNode? node = JsonTrees.ToNode(GenericLoader.Load(ref reader), in reader);
        if (node is null || node is T)
        {
            return (T?)node;
        }

        throw reader.CreateException($"The value cannot be read as {typeof(T).Name}; it is {node.GetType().Name}.");
    }

    public override void Write(Utf8YamlEmitter writer, T value, YamlSerializerOptions options) =>
        GenericWriter.Write(writer, JsonTrees.FromNode(value)!, options);
}
