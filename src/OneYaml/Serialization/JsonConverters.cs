using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OneYaml;

// The text of a JSON number, as the System.Text.Json members read it from YAML. Keeping the text, not a double, is what
// lets 1.0, 1e3, -0 and a long decimal go through unchanged.
internal sealed class JsonNumber(string text)
{
    public string Text { get; } = text;
}

// Members of the System.Text.Json document types (JsonElement, JsonNode and its subclasses) are read as an untyped
// value, turned into JSON text and parsed into the JSON type, and written the other way. Every walk uses an explicit
// stack, so the depth of the data costs heap, never call depth. Numbers travel as their text in both directions.
internal static class JsonTrees
{
    // The nesting System.Text.Json itself reads by default; the types are not meant for more. Writing stops at the same depth,
    // so what is written can be read back.
    internal const int MaxJsonDepth = 64;

    private static readonly JsonDocumentOptions s_documentOptions = new() { MaxDepth = MaxJsonDepth };

    private struct BuildFrame
    {
        public Dictionary<string, object?>.Enumerator Map;
        public List<object?>.Enumerator List;
        public bool IsMap;
    }

    // From the value of an untyped load (numbers as JsonNumber) to a node, or null for a YAML null.
    public static JsonNode? ToNode(object? tree, in Utf8YamlReader reader)
    {
        ArrayBufferWriter<byte> json = ToJson(tree, in reader);
        return JsonNode.Parse(json.WrittenSpan, documentOptions: s_documentOptions);
    }

    public static JsonElement ToElement(object? tree, in Utf8YamlReader reader)
    {
        ArrayBufferWriter<byte> json = ToJson(tree, in reader);
        using JsonDocument document = JsonDocument.Parse(json.WrittenMemory, s_documentOptions);
        return document.RootElement.Clone();
    }

    private static ArrayBufferWriter<byte> ToJson(object? tree, in Utf8YamlReader reader)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { SkipValidation = false, MaxDepth = MaxJsonDepth + 1 });
        if (tree is not (Dictionary<string, object?> or List<object?>))
        {
            WriteLeaf(writer, tree, in reader);
            writer.Flush();
            return buffer;
        }

        var stack = new List<BuildFrame>();
        Push(stack, writer, tree, in reader);
        while (stack.Count > 0)
        {
            BuildFrame top = stack[^1];
            object? child;
            if (top.IsMap)
            {
                if (!top.Map.MoveNext())
                {
                    writer.WriteEndObject();
                    stack.RemoveAt(stack.Count - 1);
                    continue;
                }

                writer.WritePropertyName(top.Map.Current.Key);
                child = top.Map.Current.Value;
            }
            else
            {
                if (!top.List.MoveNext())
                {
                    writer.WriteEndArray();
                    stack.RemoveAt(stack.Count - 1);
                    continue;
                }

                child = top.List.Current;
            }

            // The enumerators are structs held in the list: write the moved copy back.
            stack[^1] = top;
            if (child is Dictionary<string, object?> or List<object?>)
            {
                Push(stack, writer, child, in reader);
            }
            else
            {
                WriteLeaf(writer, child, in reader);
            }
        }

        writer.Flush();
        return buffer;
    }

    private static void Push(List<BuildFrame> stack, Utf8JsonWriter writer, object? tree, in Utf8YamlReader reader)
    {
        if (stack.Count >= MaxJsonDepth)
        {
            throw reader.CreateException($"The value is nested deeper than {MaxJsonDepth} levels, which the System.Text.Json types hold.");
        }

        if (tree is Dictionary<string, object?> map)
        {
            writer.WriteStartObject();
            stack.Add(new BuildFrame { Map = map.GetEnumerator(), IsMap = true });
        }
        else
        {
            writer.WriteStartArray();
            stack.Add(new BuildFrame { List = ((List<object?>)tree!).GetEnumerator() });
        }
    }

    private static void WriteLeaf(Utf8JsonWriter writer, object? value, in Utf8YamlReader reader)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                return;
            case string s:
                writer.WriteStringValue(s);
                return;
            case bool b:
                writer.WriteBooleanValue(b);
                return;
            case JsonNumber number:
                writer.WriteRawValue(number.Text, skipInputValidation: false);
                return;
            default:
                throw reader.CreateException($"A value of type {value.GetType().Name} has no JSON form.");
        }
    }

    // ---- JSON types to YAML ----

    private sealed class ElementFrame
    {
        public JsonElement.ObjectEnumerator Object;
        public JsonElement.ArrayEnumerator Array;
        public bool IsObject;
    }

    private sealed class NodeFrame
    {
        public IEnumerator<KeyValuePair<string, JsonNode?>>? Object;
        public JsonArray? Array;
        public int Index;
    }

    private static YamlException TooDeep() =>
        new($"The value is nested deeper than {MaxJsonDepth} levels, which the System.Text.Json types hold.") { Category = YamlErrorCategory.Limit };

    public static void WriteElement(Utf8YamlEmitter writer, JsonElement element)
    {
        if (element.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
        {
            WriteElementLeaf(writer, element);
            return;
        }

        var stack = new List<ElementFrame>();
        OpenElement(stack, writer, element);
        while (stack.Count > 0)
        {
            ElementFrame top = stack[^1];
            JsonElement child;
            if (top.IsObject)
            {
                if (!top.Object.MoveNext())
                {
                    writer.WriteEndMapping();
                    stack.RemoveAt(stack.Count - 1);
                    continue;
                }

                writer.WritePropertyName(top.Object.Current.Name);
                child = top.Object.Current.Value;
            }
            else
            {
                if (!top.Array.MoveNext())
                {
                    writer.WriteEndSequence();
                    stack.RemoveAt(stack.Count - 1);
                    continue;
                }

                child = top.Array.Current;
            }

            if (child.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                OpenElement(stack, writer, child);
            }
            else
            {
                WriteElementLeaf(writer, child);
            }
        }
    }

    private static void OpenElement(List<ElementFrame> stack, Utf8YamlEmitter writer, JsonElement element)
    {
        if (stack.Count >= MaxJsonDepth)
        {
            throw TooDeep();
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartMapping();
            stack.Add(new ElementFrame { Object = element.EnumerateObject(), IsObject = true });
        }
        else
        {
            writer.WriteStartSequence();
            stack.Add(new ElementFrame { Array = element.EnumerateArray() });
        }
    }

    private static void WriteElementLeaf(Utf8YamlEmitter writer, JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.String:
                writer.WriteStringValue(e.GetString());
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Number:
                writer.WriteJsonNumber(e.GetRawText());
                break;
            default:
                // Null, and the Undefined of a default element, which holds no value.
                writer.WriteNullValue();
                break;
        }
    }

    public static void WriteNode(Utf8YamlEmitter writer, JsonNode? node)
    {
        if (node is not (JsonObject or JsonArray))
        {
            WriteNodeLeaf(writer, node);
            return;
        }

        var stack = new List<NodeFrame>();
        OpenNode(stack, writer, node);
        try
        {
            while (stack.Count > 0)
            {
                NodeFrame top = stack[^1];
                JsonNode? child;
                if (top.Object is not null)
                {
                    if (!top.Object.MoveNext())
                    {
                        top.Object.Dispose();
                        writer.WriteEndMapping();
                        stack.RemoveAt(stack.Count - 1);
                        continue;
                    }

                    writer.WritePropertyName(top.Object.Current.Key);
                    child = top.Object.Current.Value;
                }
                else
                {
                    if (top.Index >= top.Array!.Count)
                    {
                        writer.WriteEndSequence();
                        stack.RemoveAt(stack.Count - 1);
                        continue;
                    }

                    child = top.Array[top.Index++];
                }

                if (child is JsonObject or JsonArray)
                {
                    OpenNode(stack, writer, child);
                }
                else
                {
                    WriteNodeLeaf(writer, child);
                }
            }
        }
        finally
        {
            foreach (NodeFrame open in stack)
            {
                open.Object?.Dispose();
            }
        }
    }

    private static void OpenNode(List<NodeFrame> stack, Utf8YamlEmitter writer, JsonNode node)
    {
        if (stack.Count >= MaxJsonDepth)
        {
            throw TooDeep();
        }

        if (node is JsonObject obj)
        {
            writer.WriteStartMapping();
            stack.Add(new NodeFrame { Object = ((IEnumerable<KeyValuePair<string, JsonNode?>>)obj).GetEnumerator() });
        }
        else
        {
            writer.WriteStartSequence();
            stack.Add(new NodeFrame { Array = (JsonArray)node });
        }
    }

    private static void WriteNodeLeaf(Utf8YamlEmitter writer, JsonNode? node)
    {
        if (node is null)
        {
            writer.WriteNullValue();
            return;
        }

        JsonValue value = node.AsValue();
        switch (value.GetValueKind())
        {
            case JsonValueKind.String:
                if (value.TryGetValue(out string? text))
                {
                    writer.WriteStringValue(text);
                }
                else
                {
                    using JsonDocument document = JsonDocument.Parse(value.ToJsonString());
                    writer.WriteStringValue(document.RootElement.GetString());
                }

                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Number:
                writer.WriteJsonNumber(NumberText(value));
                break;
            default:
                writer.WriteNullValue();
                break;
        }
    }

    // A number that System.Text.Json itself cannot write (a not-a-number or infinite double held as a CLR value) has no JSON form.
    private static string NumberText(JsonValue value)
    {
        try
        {
            return value.ToJsonString();
        }
        catch (ArgumentException)
        {
            throw new YamlException("The number has no JSON form: it is infinite or not a number.") { Category = YamlErrorCategory.Binding };
        }
    }
}

internal sealed class JsonElementConverter : YamlConverter<JsonElement>
{
    public override bool HandleNull => true;

    public override JsonElement Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options) =>
        JsonTrees.ToElement(GenericLoader.Load(ref reader, numberText: true), in reader);

    public override void Write(Utf8YamlEmitter writer, JsonElement value, YamlSerializerOptions options) =>
        JsonTrees.WriteElement(writer, value);
}

internal sealed class JsonNodeConverter<T> : YamlConverter<T>
    where T : JsonNode
{
    public override bool HandleNull => true;

    public override T? Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options)
    {
        JsonNode? node = JsonTrees.ToNode(GenericLoader.Load(ref reader, numberText: true), in reader);
        if (node is null || node is T)
        {
            return (T?)node;
        }

        throw reader.CreateException($"The value cannot be read as {typeof(T).Name}; it is {node.GetType().Name}.");
    }

    public override void Write(Utf8YamlEmitter writer, T value, YamlSerializerOptions options) =>
        JsonTrees.WriteNode(writer, value);
}
