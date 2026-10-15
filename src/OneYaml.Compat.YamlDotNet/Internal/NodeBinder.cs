using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using OneYaml.Compat.YamlDotNet.Core;
using OneYaml.Compat.YamlDotNet.Serialization;
using OneYaml.Documents;
using Oy = global::OneYaml;

namespace OneYaml.Compat.YamlDotNet.Internal;

// State of one Deserialize call.
internal sealed class BindContext
{
    public BindContext(Loaded source)
    {
        Source = source;
        S = source.State;
        Budget = new AliasBudget(S, Oy.YamlLimits.Default);
    }

    public Loaded Source { get; }

    public DocState S { get; }

    public AliasBudget Budget;

    // Anchored nodes already turned into objects, so an alias gives back the same instance, as YamlDotNet does.
    public Dictionary<int, object?> Anchored { get; } = [];

    public int Depth { get; set; }
}

// Reads a node of the document tree into a .NET value by YamlDotNet's rules.
[RequiresUnreferencedCode("Binds YAML to arbitrary types by reflection.")]
[RequiresDynamicCode("Binds YAML to arbitrary types by reflection.")]
internal sealed class NodeBinder
{
    private readonly Settings _settings;
    private readonly TypeModels _models;

    public NodeBinder(Settings settings)
    {
        _settings = settings;
        _models = new TypeModels(settings);
    }

    public object? Bind(BindContext c, int node, Type type)
    {
        if (++c.Depth > _settings.EffectiveMaxRecursion || !System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            Mark at = c.Source.MarkAt(c.S.Node(node).Start);
            throw new MaximumRecursionLevelReachedException(at, at, "Maximum level of recursion reached");
        }

        object? result = BindCore(c, node, type);
        c.Depth--;
        return result;
    }

    // Everything before the dispatch on the kind of type. It is a method of its own so that the frame of the method that
    // recurses stays small: a deeply nested document costs one such frame per level.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool Prepare(BindContext c, ref int node, ref Type type, out TagName tag, out TypeModel? model, out object? value)
    {
        model = null;
        value = null;
        tag = default;
        DocState s = c.S;
        if (s.Node(node).Kind == NodeKind.Alias)
        {
            int target = s.Node(node).Aux;
            if (c.Anchored.TryGetValue(target, out object? shared) && (shared is null ? !type.IsValueType : type.IsInstanceOfType(shared)))
            {
                value = shared;
                return true;
            }

            try
            {
                c.Budget.Charge(s, node, target);
            }
            catch (Oy.YamlException ex)
            {
                throw ErrorMap.Translate(ex, c.Source.Lines);
            }

            node = target;
        }

        ref readonly CstNode n = ref s.Node(node);
        ReadOnlySpan<byte> rawTag = s.RawTag(node);
        tag = rawTag.IsEmpty ? default : c.Source.TagOf(node);
        if (!tag.IsEmpty && _settings.TagToType.TryGetValue(tag.Value!, out Type? tagged) && (type == typeof(object) || type.IsAssignableFrom(tagged)))
        {
            type = tagged;
        }

        Type? nullable = Nullable.GetUnderlyingType(type);
        bool isNull = n.Kind == NodeKind.Scalar && tag.IsEmpty && (Oy.Parsing.ScalarStyle)n.Style == Oy.Parsing.ScalarStyle.Plain && IsNullScalar(s, node);
        if (isNull && nullable is null && !ReadsNullAsDefault(type, s, node))
        {
            isNull = false;
        }

        if (isNull || (n.Kind == NodeKind.Scalar && tag.Value == Tags.Null && IsNullScalar(s, node)))
        {
            value = nullable is not null || !type.IsValueType ? null : Activator.CreateInstance(type);
            return true;
        }

        if (nullable is not null)
        {
            type = nullable;
        }

        foreach (IYamlTypeConverter converter in _settings.Converters)
        {
            if (converter.Accepts(type))
            {
                value = RunConverter(c, converter, node, type);
                return true;
            }
        }

        model = _models.Get(type);
        if (model.AttributeConverter is { } attributed)
        {
            value = RunConverter(c, attributed, node, type);
            return true;
        }

        return false;
    }

    private object? BindCore(BindContext c, int node, Type type)
    {
        if (Prepare(c, ref node, ref type, out TagName tag, out TypeModel? model, out object? value))
        {
            return value;
        }

        switch (model!.Kind)
        {
            case TypeKind.Object:
                return BindObject(c, node, tag);
            case TypeKind.Scalar:
                return BindScalar(c, node, type, tag);
            case TypeKind.Enum:
                return BindEnum(c, node, model);
            case TypeKind.Convertible:
                return BindConvertible(c, node, model);
            case TypeKind.Array:
            case TypeKind.Collection:
                return BindSequence(c, node, model);
            case TypeKind.Dictionary:
                return BindDictionary(c, node, model);
            default:
                return BindPoco(c, node, model);
        }
    }

    // YamlDotNet reads a null scalar as the default value for most types, but types it reads with a plain parse (a GUID,
    // a time span, a URI) get the text "null" and fail or keep it.
    private static bool ReadsNullAsDefault(Type type, DocState s, int node) =>
        !(type == typeof(Guid) || type == typeof(TimeSpan) || type == typeof(Uri) || type == typeof(Version) || type == typeof(DateTimeOffset) || type == typeof(DateOnly) || type == typeof(TimeOnly))
        || s.DecodedScalar(node, out byte[]? rented).IsEmpty && ReleaseAndTrue(rented);

    private static bool ReleaseAndTrue(byte[]? rented)
    {
        DocState.ReleaseDecoded(rented);
        return true;
    }

    private static bool IsNullScalar(DocState s, int node)
    {
        ReadOnlySpan<byte> v = s.DecodedScalar(node, out byte[]? rented);
        try
        {
            return v.IsEmpty || v is [(byte)'~'] || v.SequenceEqual("null"u8) || v.SequenceEqual("Null"u8) || v.SequenceEqual("NULL"u8);
        }
        finally
        {
            DocState.ReleaseDecoded(rented);
        }
    }

    private static Core.YamlException Error(BindContext c, int node, string message, Exception? inner = null)
    {
        DocState s = c.S;
        return new Core.YamlException(c.Source.MarkAt(s.PropStart(node)), c.Source.MarkAt(s.Node(node).End), message, inner);
    }

    private object? RunConverter(BindContext c, IYamlTypeConverter converter, int node, Type type)
    {
        var parser = new Parser(c.Source, node);
        parser.MoveNext();
        object? value = converter.ReadYaml(parser, type, NestedReader(c, parser));
        if (value is not null && !type.IsInstanceOfType(value))
        {
            throw Error(c, node, $"The converter returned a {value.GetType()} where a {type} was expected.");
        }

        return value;
    }

    private ObjectDeserializer NestedReader(BindContext c, Parser parser) => nestedType =>
    {
        int at = parser.CurrentNode;
        if (at < 0)
        {
            throw new Core.YamlException(parser.Current?.Start ?? Mark.Empty, parser.Current?.End ?? Mark.Empty, "There is no value to read at this position.");
        }

        object? value = Bind(c, at, nestedType);
        parser.SkipNode();
        return value;
    };

    // Reads the node at the parser's position (used by Deserialize(IParser)).
    public object? BindAtParser(Parser parser, Type type)
    {
        var c = new BindContext(parser.Source);
        int at = parser.CurrentNode;
        if (at < 0)
        {
            Mark mark = parser.Current?.Start ?? Mark.Empty;
            throw new Core.YamlException(mark, mark, $"Expected a node, got '{parser.Current?.GetType().Name}'.");
        }

        object? value = Bind(c, at, type);
        parser.SkipNode();
        return value;
    }

    private object? BindObject(BindContext c, int node, TagName tag)
    {
        switch (c.S.Node(node).Kind)
        {
            case NodeKind.Scalar:
                return BindUntypedScalar(c, node, tag);
            case NodeKind.Mapping:
                RequireCollectionTag(c, node, tag, Tags.Map);
                return BindDictionary(c, node, _models.Get(typeof(Dictionary<object, object>)));
            default:
                RequireCollectionTag(c, node, tag, Tags.Seq);
                return BindSequence(c, node, _models.Get(typeof(List<object>)));
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private object? BindUntypedScalar(BindContext c, int node, TagName tag)
    {
        DocState s = c.S;
        string text = s.ScalarString(node);
        if (!tag.IsEmpty)
        {
            if (tag.Value!.StartsWith(Tags.Prefix, StringComparison.Ordinal) && tag.Value is Tags.Str or Tags.Bool or Tags.Int or Tags.Float or Tags.Null)
            {
                try
                {
                    return ScalarRules.FromCoreTag(tag.Value, text);
                }
                catch (Exception ex) when (ex is FormatException or OverflowException)
                {
                    throw Error(c, node, "Exception during deserialization", ex);
                }
            }

            throw Error(c, node, $"Encountered an unresolved tag '{tag.Value}'");
        }

        bool plain = (Oy.Parsing.ScalarStyle)s.Node(node).Style == Oy.Parsing.ScalarStyle.Plain;
        return plain && _settings.UnquotedStringTypeDeserialization ? ScalarRules.Detect(text) : text;
    }

    private static void RequireCollectionTag(BindContext c, int node, TagName tag, string expected)
    {
        if (!tag.IsEmpty && tag.Value != expected)
        {
            throw UnresolvedTag(c, node, tag);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Core.YamlException UnresolvedTag(BindContext c, int node, TagName tag) =>
        Error(c, node, $"Encountered an unresolved tag '{tag.Value}'");

    private static object? BindScalar(BindContext c, int node, Type type, TagName tag)
    {
        DocState s = c.S;
        if (s.Node(node).Kind != NodeKind.Scalar)
        {
            throw Error(c, node, $"Expected a scalar for {type}, got a {(s.Node(node).Kind == NodeKind.Mapping ? "mapping" : "sequence")}.");
        }

        string text = s.ScalarString(node);
        if (type == typeof(string))
        {
            return text;
        }

        try
        {
            return ScalarRules.Convert(text, type);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or InvalidCastException or ArgumentException)
        {
            throw Error(c, node, "Exception during deserialization", ex);
        }
    }

    private static object? BindEnum(BindContext c, int node, TypeModel model)
    {
        DocState s = c.S;
        if (s.Node(node).Kind != NodeKind.Scalar)
        {
            throw Error(c, node, $"Expected a scalar for {model.Type}.");
        }

        string text = s.ScalarString(node);
        if (model.EnumByName!.TryGetValue(text, out object? known))
        {
            return known;
        }

        try
        {
            return Enum.Parse(model.Type, text, ignoreCase: true);
        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException)
        {
            throw Error(c, node, "Exception during deserialization", ex);
        }
    }

    private object? BindConvertible(BindContext c, int node, TypeModel model)
    {
        object? instance = Create(c, node, model.Type);
        var parser = new Parser(c.Source, node);
        parser.MoveNext();
        ((IYamlConvertible)instance!).Read(parser, model.Type, NestedReader(c, parser));
        return instance;
    }

    private object? Create(BindContext c, int node, Type type)
    {
        try
        {
            return _models.Create(type);
        }
        catch (Exception ex) when (ex is MissingMethodException or TargetInvocationException or MemberAccessException or NotSupportedException or ArgumentException)
        {
            throw Error(c, node, "Exception during deserialization", ex);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Core.YamlException ExpectedCollection(BindContext c, int node, string expected, string got)
    {
        Mark at = c.Source.MarkAt(c.S.PropStart(node));
        return new Core.YamlException(at, at, $"Expected '{expected}', got '{got}' (at {at}).");
    }

    private object? BindSequence(BindContext c, int node, TypeModel model)
    {
        DocState s = c.S;
        ref readonly CstNode n = ref s.Node(node);
        if (n.Kind != NodeKind.Sequence)
        {
            throw ExpectedCollection(c, node, "SequenceStart", n.Kind == NodeKind.Mapping ? "MappingStart" : "Scalar");
        }

        Type element = model.Element!;
        int count = n.Count;
        bool anchored = s.HasAnchor(node);
        if (model.Kind == TypeKind.Array)
        {
            var items = new List<object?>(count);
            int child = node + 1;
            for (int i = 0; i < count; i++)
            {
                items.Add(Bind(c, child, element));
                child += s.Node(child).Size;
            }

            Array array = Array.CreateInstance(element, items.Count);
            for (int i = 0; i < items.Count; i++)
            {
                array.SetValue(items[i], i);
            }

            if (anchored)
            {
                c.Anchored[node] = array;
            }

            return array;
        }

        Type concrete = model.Concrete ?? model.Type;
        object? list = Create(c, node, concrete);
        if (anchored)
        {
            c.Anchored[node] = list;
        }

        int pos = node + 1;
        for (int i = 0; i < count; i++)
        {
            object? item = Bind(c, pos, element);
            pos += s.Node(pos).Size;
            AddItem(list!, model, item);
        }

        return list;
    }

    // Out of line, with the other bookkeeping, so that the method that recurses keeps a small frame.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AddItem(object list, TypeModel model, object? item)
    {
        if (model.IsList)
        {
            ((IList)list).Add(item);
        }
        else
        {
            model.Add!.Invoke(list, [item]);
        }
    }

    private object? BindDictionary(BindContext c, int node, TypeModel model)
    {
        DocState s = c.S;
        ref readonly CstNode n = ref s.Node(node);
        if (n.Kind != NodeKind.Mapping)
        {
            throw ExpectedCollection(c, node, "MappingStart", n.Kind == NodeKind.Sequence ? "SequenceStart" : "Scalar");
        }

        Type concrete = model.Concrete ?? model.Type;
        object? dictionary = Create(c, node, concrete);
        if (s.HasAnchor(node))
        {
            c.Anchored[node] = dictionary;
        }

        int pairs = n.Count / 2;
        int pos = node + 1;
        for (int i = 0; i < pairs; i++)
        {
            int keyNode = pos;
            int valueNode = pos + s.Node(pos).Size;
            pos = valueNode + s.Node(valueNode).Size;
            object? key = Bind(c, keyNode, model.KeyType!);
            object? value = Bind(c, valueNode, model.ValueType!);
            PutEntry(c, dictionary!, model, keyNode, key, value);
        }

        return dictionary;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void PutEntry(BindContext c, object dictionary, TypeModel model, int keyNode, object? key, object? value)
    {
        try
        {
            if (key is null)
            {
                throw Error(c, keyNode, "A mapping key cannot be null.");
            }

            if (_settings.DuplicateKeyChecking && ContainsKey(dictionary, model, key))
            {
                throw Error(c, keyNode, $"Encountered duplicate key {key}");
            }

            if (model.IsDictionary)
            {
                ((IDictionary)dictionary)[key] = value;
            }
            else
            {
                model.Add!.Invoke(dictionary, [key, value]);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidCastException or TargetInvocationException)
        {
            throw Error(c, keyNode, "Exception during deserialization", ex);
        }
    }

    private static bool ContainsKey(object dictionary, TypeModel model, object key) =>
        dictionary is IDictionary d ? d.Contains(key) : false;

    private object? BindPoco(BindContext c, int node, TypeModel model)
    {
        DocState s = c.S;
        ref readonly CstNode n = ref s.Node(node);
        if (n.Kind != NodeKind.Mapping)
        {
            throw NotAMapping(c, node, model);
        }

        object? instance = Create(c, node, model.Concrete ?? model.Type);
        if (s.HasAnchor(node))
        {
            c.Anchored[node] = instance;
        }

        HashSet<string>? seen = _settings.DuplicateKeyChecking ? [] : null;
        int pairs = n.Count / 2;
        int pos = node + 1;
        for (int i = 0; i < pairs; i++)
        {
            int keyNode = pos;
            int valueNode = pos + s.Node(pos).Size;
            pos = valueNode + s.Node(valueNode).Size;
            string key = KeyText(c, keyNode);
            if (seen is not null && !seen.Add(key))
            {
                throw DuplicateKey(c, keyNode, key);
            }

            if (!model.ByKey.TryGetValue(key, out MemberModel? member) && !(model.ByKeyIgnoreCase?.TryGetValue(key, out member) ?? false))
            {
                if (_settings.IgnoreUnmatched)
                {
                    continue;
                }

                throw PropertyNotFound(c, keyNode, key, model);
            }

            SetMember(c, member, instance!, valueNode, Bind(c, valueNode, member.Type));
        }

        return instance;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Core.YamlException NotAMapping(BindContext c, int node, TypeModel model)
    {
        DocState s = c.S;
        if (s.Node(node).Kind == NodeKind.Sequence)
        {
            Mark at = c.Source.MarkAt(s.PropStart(node));
            return new Core.YamlException(at, at, $"No node deserializer was able to deserialize the node into type {model.Type.AssemblyQualifiedName}");
        }

        return Error(c, node, "Exception during deserialization", new InvalidCastException($"Unable to cast a scalar to {model.Type}."));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SetMember(BindContext c, MemberModel member, object instance, int valueNode, object? value)
    {
        try
        {
            member.Set(instance, value);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidCastException or TargetInvocationException or MemberAccessException)
        {
            throw Error(c, valueNode, "Exception during deserialization", ex);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Core.YamlException DuplicateKey(BindContext c, int keyNode, string key) =>
        Error(c, keyNode, $"Encountered duplicate key {key}");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Core.YamlException PropertyNotFound(BindContext c, int keyNode, string key, TypeModel model) =>
        Error(c, keyNode, $"Property '{key}' not found on type '{model.Type.FullName}'.");

    private static string KeyText(BindContext c, int keyNode)
    {
        DocState s = c.S;
        int target = s.Node(keyNode).Kind == NodeKind.Alias ? s.Node(keyNode).Aux : keyNode;
        if (s.Node(target).Kind != NodeKind.Scalar)
        {
            throw Error(c, keyNode, "A mapping key of an object must be a scalar.");
        }

        return s.ScalarString(target);
    }
}
