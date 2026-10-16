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
// value and turned into the JSON type (a node directly, an element through JSON text), and written the other way. Every walk uses an explicit
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

    private struct NodeBuildFrame
    {
        public Dictionary<string, object?>.Enumerator Map;
        public List<object?>.Enumerator List;
        public JsonObject? Object;
        public JsonArray? Array;
        public string? Key;
    }

    // From the value of an untyped load (numbers as JsonNumber) to a node, or null for a YAML null. A single value and
    // a small tree are built directly: going through JSON text and a parse costs several times the memory for them.
    public static JsonNode? ToNode(object? tree, in Utf8YamlReader reader)
    {
        if (tree is not (Dictionary<string, object?> or List<object?>))
        {
            return Leaf(tree, in reader);
        }

        // A larger tree goes through JSON text and one parse: the parse makes the nodes only when they are touched, which costs
        // far less than building each one, while the writer and the buffer cost a fixed amount a small tree should not pay.
        if (!IsSmall(tree))
        {
            ArrayBufferWriter<byte> json = ToJson(tree, in reader);
            return JsonNode.Parse(json.WrittenSpan, documentOptions: s_documentOptions);
        }

        var stack = new List<NodeBuildFrame>();
        JsonNode? result = null;
        PushNode(stack, tree, in reader);
        while (stack.Count > 0)
        {
            NodeBuildFrame top = stack[^1];
            object? child;
            string? key = null;
            if (top.Object is not null)
            {
                if (!top.Map.MoveNext())
                {
                    result = FinishNode(stack, top.Object);
                    continue;
                }

                key = top.Map.Current.Key;
                child = top.Map.Current.Value;
            }
            else
            {
                if (!top.List.MoveNext())
                {
                    result = FinishNode(stack, top.Array!);
                    continue;
                }

                child = top.List.Current;
            }

            // The enumerators are structs held in the list: write the moved copy back.
            stack[^1] = top;
            if (child is Dictionary<string, object?> or List<object?>)
            {
                top.Key = key;
                stack[^1] = top;
                PushNode(stack, child, in reader);
            }
            else
            {
                AttachNode(top, key, Leaf(child, in reader));
            }
        }

        return result;
    }

    // Whether a tree holds no more than SmallTree values, counting a shared value each time it is used. The count stops
    // at the budget, and every level of nesting costs at least one value, so the recursion is at most SmallTree deep.
    private static bool IsSmall(object? tree)
    {
        int budget = SmallTree;
        return Fits(tree, ref budget);
    }

    private static bool Fits(object? tree, ref int budget)
    {
        if (tree is Dictionary<string, object?> map)
        {
            foreach (object? child in map.Values)
            {
                if (--budget < 0 || !Fits(child, ref budget))
                {
                    return false;
                }
            }
        }
        else if (tree is List<object?> list)
        {
            foreach (object? child in list)
            {
                if (--budget < 0 || !Fits(child, ref budget))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private const int SmallTree = 8;

    private static void PushNode(List<NodeBuildFrame> stack, object? tree, in Utf8YamlReader reader)
    {
        if (stack.Count >= MaxJsonDepth)
        {
            throw reader.CreateException($"The value is nested deeper than {MaxJsonDepth} levels, which the System.Text.Json types hold.");
        }

        if (tree is Dictionary<string, object?> map)
        {
            stack.Add(new NodeBuildFrame { Map = map.GetEnumerator(), Object = new JsonObject() });
        }
        else
        {
            stack.Add(new NodeBuildFrame { List = ((List<object?>)tree!).GetEnumerator(), Array = new JsonArray() });
        }
    }

    // A finished collection leaves the stack and goes into the one below it, or is the result.
    private static JsonNode? FinishNode(List<NodeBuildFrame> stack, JsonNode done)
    {
        stack.RemoveAt(stack.Count - 1);
        if (stack.Count == 0)
        {
            return done;
        }

        NodeBuildFrame parent = stack[^1];
        AttachNode(parent, parent.Key, done);
        return null;
    }

    private static void AttachNode(NodeBuildFrame parent, string? key, JsonNode? value)
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
            case JsonNumber number:
                return Number(number.Text);
            default:
                throw reader.CreateException($"A value of type {value.GetType().Name} has no JSON form.");
        }
    }

    // A number keeps the digits it was written with, so it is parsed from its own text: the node then reads as one
    // System.Text.Json parsed (GetValue<int>, GetValue<double> and GetValue<decimal> all work), and 1.0, -0 and 1e400 print as written.
    private static JsonNode? Number(string text) => JsonNode.Parse(text);

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

        // The element type may hold a name twice, which YAML cannot: the names written so far are compared by scanning
        // the object for the first few and by a set after that.
        public int Written;
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
        NameSet?[]? sets = null;
        OpenElement(stack, writer, element);
        try
        {
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

                    string name = top.Object.Current.Name;
                    if (RepeatsName(top, stack.Count - 1, name, ref sets))
                    {
                        throw new YamlException($"The object has the property name '{name}' twice, which a YAML mapping cannot hold.") { Category = YamlErrorCategory.Binding };
                    }

                    writer.WritePropertyName(name);
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
        finally
        {
            if (sets is not null)
            {
                foreach (NameSet? set in sets)
                {
                    set?.Release();
                }
            }
        }
    }

    private const int ScanLimit = 16;

    private static bool RepeatsName(ElementFrame frame, int depth, string name, ref NameSet?[]? sets)
    {
        int written = frame.Written++;
        if (written > ScanLimit)
        {
            return !sets![depth]!.Add(name);
        }

        // A copy of the enumerator, rewound, walks the names already written.
        JsonElement.ObjectEnumerator earlier = frame.Object;
        earlier.Reset();
        if (written < ScanLimit)
        {
            for (int index = 0; index < written && earlier.MoveNext(); index++)
            {
                if (earlier.Current.NameEquals(name))
                {
                    return true;
                }
            }

            return false;
        }

        // A large object: its names go into a set that the objects at this depth after it reuse.
        // GetPropertyCount needs a newer runtime than the oldest target: count by walking the object once.
        JsonElement.ObjectEnumerator counted = earlier;
        int count = 0;
        while (counted.MoveNext())
        {
            count++;
        }

        if (sets is null || sets.Length <= depth)
        {
            Array.Resize(ref sets, depth + 1);
        }

        NameSet names = sets[depth] ??= new NameSet();
        names.Reset(count);
        for (int index = 0; index < written && earlier.MoveNext(); index++)
        {
            names.Add(earlier.Current.Name);
        }

        return !names.Add(name);
    }

    // The names of one large object, in storage from the pool: a set that grows on the heap costs more than the object it checks.
    private sealed class NameSet
    {
        private string?[] _names = [];
        private int[] _table = [];
        private int _count;
        private int _size;

        public void Reset(int capacity)
        {
            Release();
            _names = ArrayPool<string?>.Shared.Rent(Math.Max(capacity, 1));
            _size = 16;
            while (_size < capacity * 2)
            {
                _size <<= 1;
            }

            _table = ArrayPool<int>.Shared.Rent(_size);
            Array.Clear(_table, 0, _size);
            _count = 0;
        }

        // Whether the name was new. The table holds the position of a name in _names plus one; 0 is a free slot.
        public bool Add(string name)
        {
            int mask = _size - 1;
            int slot = name.GetHashCode() & mask;
            while (_table[slot] != 0)
            {
                if (string.Equals(_names[_table[slot] - 1], name, StringComparison.Ordinal))
                {
                    return false;
                }

                slot = (slot + 1) & mask;
            }

            _names[_count++] = name;
            _table[slot] = _count;
            return true;
        }

        public void Release()
        {
            if (_names.Length != 0)
            {
                ArrayPool<string?>.Shared.Return(_names, clearArray: true);
                _names = [];
            }

            if (_table.Length != 0)
            {
                ArrayPool<int>.Shared.Return(_table);
                _table = [];
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
