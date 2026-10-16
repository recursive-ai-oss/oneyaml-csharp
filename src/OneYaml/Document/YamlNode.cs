using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Text;
using OneYaml.Documents;
using OneYaml.Parsing;
using OneYaml.Schema;

namespace OneYaml;

/// <summary>
/// A lightweight view of one node of a <see cref="YamlDocument"/>. Reading follows aliases.
/// A view stays valid across edits, the way a <c>JsonNode</c> or an <c>XElement</c> does: edits made through it
/// or elsewhere in the document move it along with its node, so
/// <c>var spec = doc.Root["spec"]; spec["a"] = 1; spec["b"] = 2;</c> works. Replacing a node's value
/// keeps the view of the node itself; views of what was inside the old value, and of nodes that were removed,
/// throw <see cref="InvalidOperationException"/> when used. A node made from a value (a string, a number,
/// <see langword="null"/>…) is detached and can only be assigned into a document. Using a view after
/// its document was disposed throws <see cref="ObjectDisposedException"/>.
/// </summary>
/// <remarks>
/// <para>Reading through aliases is charged to the alias budget of the root the view was reached from, so a
/// long-lived reader should take <see cref="YamlDocument.Root"/> again for each operation.</para>
/// <para>A view reached through an alias is a read-only snapshot: it is valid only until the next edit, and then
/// throws <see cref="InvalidOperationException"/>; take it again from the root.</para>
/// <para>Two views are equal when they name the same node of the same document.</para>
/// </remarks>
public sealed class YamlNode : IEquatable<YamlNode>
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly YamlDocument? _doc;
    private readonly bool _viaAlias;
    private readonly object? _value;

    // Where the node is in the document's node array, as of the edit epoch the view last saw. A view that is
    // behind follows the edits made since (see EditEpoch).
    private int _index;
    private EditEpoch? _epoch;

    internal YamlNode(YamlDocument doc, int index, EditEpoch epoch, bool viaAlias)
    {
        _doc = doc;
        _index = index;
        _epoch = epoch;
        _viaAlias = viaAlias;
        _value = null;
    }

    private YamlNode(object? detached)
    {
        _doc = null;
        _index = -1;
        _epoch = null;
        _viaAlias = false;
        _value = detached;
    }

    /// <summary>Makes a detached node from a value, to assign into a document.</summary>
    /// <param name="value">null, a string, a boolean, a number, a dictionary or sequence of these, or a value of any other type the serializer can write (written as <see cref="YamlSerializer"/> writes it, with reflection).</param>
    /// <returns>The detached node.</returns>
    /// <exception cref="ArgumentException">The value contains a type that has no YAML form, or one outside the plain value model while reflection is turned off (the default in trimmed and ahead-of-time applications); use the overload that takes a <see cref="YamlTypeInfo{T}"/> there.</exception>
    public static YamlNode Create(object? value) => new(value is null || value is YamlNode ? value : Typed(value));

    /// <summary>Makes a detached node from a value of any type <paramref name="typeInfo"/> describes, written as <see cref="YamlSerializer"/> writes it.</summary>
    /// <typeparam name="T">The type of the value.</typeparam>
    /// <param name="value">The value.</param>
    /// <param name="typeInfo">Metadata for <typeparamref name="T"/>, usually from a generated <see cref="YamlSerializerContext"/>.</param>
    /// <returns>The detached node.</returns>
    /// <exception cref="ArgumentException">The value has no YAML form.</exception>
    public static YamlNode Create<T>(T value, YamlTypeInfo<T> typeInfo) => new(TypedValues.Of(value, typeInfo, YamlLimits.Default));

    /// <summary>Makes a detached node from a value written by the reflection-based serializer.</summary>
    /// <typeparam name="T">The type of the value.</typeparam>
    /// <param name="value">The value.</param>
    /// <param name="options">Options for writing the value; <see langword="null"/> means <see cref="YamlSerializerOptions.Default"/>.</param>
    /// <returns>The detached node.</returns>
    /// <exception cref="ArgumentException">The value has no YAML form.</exception>
    [RequiresUnreferencedCode("Reflection-based serialization reads members that trimming can remove. Pass a YamlTypeInfo from a YamlSerializerContext instead.")]
    [RequiresDynamicCode("Reflection-based serialization makes generic types at run time. Pass a YamlTypeInfo from a YamlSerializerContext instead.")]
    public static YamlNode Create<T>(T value, YamlSerializerOptions? options) => new(TypedValues.Of(value, options, YamlLimits.Default));

    // A plain value stays as it is; anything else is written by the serializer now, so the node holds its tree.
    private static object Typed(object value)
    {
        if (value is string or bool or char or Enum or Guid or BigInteger or double or float or Half or decimal
            or sbyte or byte or short or ushort or int or uint or long or ulong or nint or nuint or IDictionary
            or IEnumerable)
        {
            // Collections are checked when they are assigned, together with what they hold.
            return value;
        }

        return TypedValues.OfObject(value, YamlLimits.Default);
    }

    /// <summary>Makes a detached string node.</summary>
    /// <param name="value">The string, or <see langword="null"/> for a null node.</param>
    public static implicit operator YamlNode(string? value) => new(value);

    /// <summary>Makes a detached boolean node.</summary>
    /// <param name="value">The value.</param>
    public static implicit operator YamlNode(bool value) => new(value);

    /// <summary>Makes a detached integer node.</summary>
    /// <param name="value">The value.</param>
    public static implicit operator YamlNode(int value) => new(value);

    /// <summary>Makes a detached integer node.</summary>
    /// <param name="value">The value.</param>
    public static implicit operator YamlNode(long value) => new(value);

    /// <summary>Makes a detached integer node.</summary>
    /// <param name="value">The value.</param>
    public static implicit operator YamlNode(uint value) => new(value);

    /// <summary>Makes a detached integer node.</summary>
    /// <param name="value">The value.</param>
    public static implicit operator YamlNode(ulong value) => new(value);

    /// <summary>Makes a detached floating-point node.</summary>
    /// <param name="value">The value.</param>
    public static implicit operator YamlNode(double value) => new(value);

    /// <summary>Makes a detached floating-point node.</summary>
    /// <param name="value">The value.</param>
    public static implicit operator YamlNode(decimal value) => new(value);

    /// <summary>Whether the node comes from a value and is not part of a document.</summary>
    public bool IsDetached => _doc is null;

    private DocState State()
    {
        YamlDocument? doc = _doc;
        if (doc is null)
        {
            throw new InvalidOperationException("A detached node has no position in a document.");
        }

        // Disposal is reported first, as itself, whatever edits came before.
        DocState s = doc.State;
        if (_epoch!.Number != doc.EditCount)
        {
            Follow(doc);
        }

        return s;
    }

    // The document was edited since this view last looked: find the node again by following each edit's effect
    // on positions. A node an edit removed has no place any more.
    private void Follow(YamlDocument doc)
    {
        if (_viaAlias)
        {
            throw new InvalidOperationException("A node reached through an alias is valid only until the document is edited; take it again from the root.");
        }

        int index = _index;
        EditEpoch e = _epoch!;
        while (e.Number != doc.EditCount)
        {
            e = e.Next!;
            index = e.Map(index);
            if (index < 0)
            {
                throw new InvalidOperationException("This node is no longer part of the document: it was removed, or a value that contained it was replaced.");
            }
        }

        _index = index;
        _epoch = e;
    }

    // Scalars up to this many bytes read through an alias at the price of reading them directly.
    private const int FreeAliasReadBytes = 256;

    // The node whose content reads see: aliases followed. Following costs nothing by itself; the reads
    // that do work in proportion to what the alias stands for charge the document's walk budget.
    private int Followed(DocState s, out bool via)
    {
        via = _viaAlias;
        if (s.Node(_index).Kind != NodeKind.Alias)
        {
            return _index;
        }

        via = true;
        return s.Node(_index).Aux;
    }

    // Charges the text of a node read through an alias.
    private void ChargeText(DocState s, int node, bool via, int length)
    {
        if (via && length > FreeAliasReadBytes)
        {
            _doc!.Walk.ChargeRead(s, node, 1, length);
        }
    }

    /// <summary>The kind of node. An alias is reported as <see cref="YamlNodeKind.Alias"/>; use <see cref="Target"/> to see what it names.</summary>
    public YamlNodeKind Kind
    {
        get
        {
            if (_doc is null)
            {
                return _value switch
                {
                    ValueTree t => t[0].Kind switch
                    {
                        VKind.Mapping => YamlNodeKind.Mapping,
                        VKind.Sequence => YamlNodeKind.Sequence,
                        _ => YamlNodeKind.Scalar,
                    },
                    null => YamlNodeKind.Scalar,
                    string => YamlNodeKind.Scalar,
                    IDictionary => YamlNodeKind.Mapping,
                    IEnumerable => YamlNodeKind.Sequence,
                    _ => YamlNodeKind.Scalar,
                };
            }

            return (YamlNodeKind)(int)State().Node(_index).Kind;
        }
    }

    /// <summary>For an alias, the node it names; any other node is returned as it is.</summary>
    public YamlNode Target
    {
        get
        {
            DocState s = State();
            int t = Followed(s, out bool via);
            return new YamlNode(_doc!, t, _epoch!, via);
        }
    }

    /// <summary>How the node is written in the source.</summary>
    public YamlNodeStyle Style
    {
        get
        {
            ref readonly CstNode n = ref State().Node(_index);
            return n.Kind switch
            {
                NodeKind.Scalar => (YamlNodeStyle)n.Style,
                NodeKind.Alias => YamlNodeStyle.Block,
                _ => n.IsFlow ? YamlNodeStyle.Flow : YamlNodeStyle.Block,
            };
        }
    }

    /// <summary>The tag resolved against the document's <c>%TAG</c> directives, or <see langword="null"/> when the node has none.</summary>
    /// <remarks>
    /// <c>!!str</c> reads as <c>tag:yaml.org,2002:str</c> and a local tag such as <c>!Sub</c> as <c>!Sub</c>. This is the form
    /// <see cref="Utf8YamlReader.Tag"/> returns and <see cref="Utf8YamlEmitter.WriteTag(string)"/> takes, and writes back as the same tag, including a local tag
    /// such as <c>!!foo</c> (from <c>!%21foo</c>). Use
    /// <see cref="RawTag"/> for the text as it appears in the source.
    /// </remarks>
    public string? Tag
    {
        get
        {
            DocState s = State();
            return s.TryTagParts(_index, out _, out _, out int ps, out int pl, out int ss, out int sl)
                ? YamlReader.BuildTag(s.Text, ps, pl, ss, sl)
                : null;
        }
    }

    /// <summary>The tag exactly as written in the source, such as <c>!!str</c>, <c>!e!widget</c> or <c>!&lt;tag:example.com,2000:x&gt;</c>, or <see langword="null"/>.</summary>
    public string? RawTag
    {
        get
        {
            DocState s = State();
            ReadOnlySpan<byte> tag = s.RawTag(_index);
            return tag.IsEmpty ? null : Encoding.UTF8.GetString(tag);
        }
    }

    /// <summary>The anchor name without the <c>&amp;</c>, or <see langword="null"/>.</summary>
    public string? Anchor
    {
        get
        {
            DocState s = State();
            ReadOnlySpan<byte> name = s.AnchorName(_index);
            return name.IsEmpty ? null : Encoding.UTF8.GetString(name);
        }
    }

    /// <summary>Where the node's content starts in the source, after any anchor or tag.</summary>
    public YamlPosition Position
    {
        get
        {
            DocState s = State();
            return s.PositionOf(s.Node(_index).Start);
        }
    }

    /// <summary>Whether the node is a scalar that reads as null (empty, <c>~</c>, <c>null</c>). Aliases are followed.</summary>
    public bool IsNull
    {
        get
        {
            if (_doc is null)
            {
                return _value is null || (_value is ValueTree tree && tree[0].Kind == VKind.Scalar && tree[0].Scalar == ScalarKind.Null);
            }

            DocState s = State();
            int t = Followed(s, out _);
            return s.Node(t).Kind == NodeKind.Scalar && s.KindOf(t, out bool valid) == ScalarKind.Null && valid;
        }
    }

    /// <summary>The number of items of a sequence or entries of a mapping; 0 for a scalar. Aliases are followed.</summary>
    public int Count
    {
        get
        {
            DocState s = State();
            ref readonly CstNode n = ref s.Node(Followed(s, out _));
            return n.Kind switch
            {
                NodeKind.Mapping => n.Count / 2,
                NodeKind.Sequence => n.Count,
                _ => 0,
            };
        }
    }

    // ---- Reading values ----

    /// <summary>Reads a scalar as a CLR value.</summary>
    /// <typeparam name="T">A string, boolean, integer, floating-point, big integer, character, date or time, or GUID type, or <see cref="object"/>.</typeparam>
    /// <returns>The value.</returns>
    /// <exception cref="YamlException">The node is not a scalar of that type (<see cref="YamlErrorCategory.Binding"/>).</exception>
    public T GetValue<T>()
    {
        DocState s = State();
        if (!NodeValues.IsLeafType(typeof(T)))
        {
            return BindAny<T>();
        }

        int t = Followed(s, out bool via);
        ChargeText(s, t, via, s.Node(t).End - s.Node(t).Start);
        if (NodeValues.TryGet(s, t, _doc!.BindOptions, out T value, out YamlException? error))
        {
            return value;
        }

        _ = YamlException.Tag(error!, _doc.SourceName);
        throw error!;
    }

    /// <summary>Reads the node as a type using generated metadata. This is the form for trimmed and ahead-of-time programs; it reads whatever <see cref="Set{T}(T, YamlTypeInfo{T})"/> can write.</summary>
    /// <typeparam name="T">The type to read.</typeparam>
    /// <param name="typeInfo">The metadata for <typeparamref name="T"/>.</param>
    /// <returns>The value.</returns>
    /// <exception cref="YamlException">The node does not fit the type, or aliases expand beyond the limits.</exception>
    public T GetValue<T>(YamlTypeInfo<T> typeInfo) => Deserialize(typeInfo)!;

    // A type that is not a plain scalar (an enum, a class, a collection) is read by the binder, as Set<T> writes it.
    // This needs reflection unless the caller passes a YamlTypeInfo, so it is open only while the switch is on.
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Reflection-based reads happen only while YamlSerializer.IsReflectionEnabledByDefault is true. That switch is false when trimming or compiling ahead of time, the trimmer folds the check, and this method then throws NotSupportedException naming the overload that takes a YamlTypeInfo.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Same switch as above: with it off, this method throws before any reflection runs.")]
    private T BindAny<T>()
    {
        if (!YamlSerializer.IsReflectionEnabledByDefault)
        {
            throw new NotSupportedException($"{typeof(T).Name} is not a plain scalar type, and reflection is turned off in this application. Use the overload that takes a YamlTypeInfo, or turn the OneYaml.YamlSerializer.IsReflectionEnabledByDefault switch on.");
        }

        return Deserialize<T>(_doc!.BindOptions)!;
    }

    // The same read as BindAny, for a caller that only asks whether it fits: a miss makes no error to throw and catch.
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Same switch as BindAny: with reflection off this method returns before any reflection runs.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Same switch as BindAny.")]
    private bool TryBindAny<T>(out T value)
    {
        if (!YamlSerializer.IsReflectionEnabledByDefault)
        {
            throw new NotSupportedException($"{typeof(T).Name} is not a plain scalar type, and reflection is turned off in this application. Use the overload that takes a YamlTypeInfo, or turn the OneYaml.YamlSerializer.IsReflectionEnabledByDefault switch on.");
        }

        try
        {
            value = NodeReplay.Bind(State(), _index, _doc!.BindOptions.GetReflectiveTypeInfo<T>(), quiet: true)!;
            return true;
        }
        catch (YamlException e) when (e.Category == YamlErrorCategory.Binding)
        {
            value = default!;
            return false;
        }
    }

    /// <summary>Reads a scalar as a CLR value, if it has that type.</summary>
    /// <typeparam name="T">A string, boolean, integer, floating-point, big integer, character, date or time, or GUID type, or <see cref="object"/>.</typeparam>
    /// <param name="value">The value, or the default.</param>
    /// <returns><see langword="true"/> if the node is a scalar of that type.</returns>
    public bool TryGetValue<T>(out T value)
    {
        DocState s = State();
        if (!NodeValues.IsLeafType(typeof(T)))
        {
            return TryBindAny(out value);
        }

        int t = Followed(s, out bool via);
        ChargeText(s, t, via, s.Node(t).End - s.Node(t).Start);
        return NodeValues.TryGet(s, t, _doc!.BindOptions, out value, out _, wantError: false);
    }

    /// <summary>Binds the node to a type with the reflection binder, as <see cref="YamlSerializer"/> would bind the same text.</summary>
    /// <typeparam name="T">The type to read.</typeparam>
    /// <param name="options">Options; <see langword="null"/> means the document's own limits and <see cref="YamlDocumentOptions.SourceName"/> with the defaults of <see cref="YamlSerializerOptions.Default"/>. Whatever limits the options carry, no limit is looser than the one the document was opened with.</param>
    /// <returns>The value.</returns>
    /// <exception cref="YamlException">The node does not fit the type, or aliases expand beyond the limits.</exception>
    /// <exception cref="InvalidOperationException">The node was removed, or is detached.</exception>
    [RequiresUnreferencedCode("Reflection-based binding reads members and constructors that trimming can remove. Pass a YamlTypeInfo from a YamlSerializerContext instead.")]
    [RequiresDynamicCode("Reflection-based binding makes generic types and compiles accessors at run time. Pass a YamlTypeInfo from a YamlSerializerContext instead.")]
    public T? Deserialize<T>(YamlSerializerOptions? options = null)
    {
        DocState s = State();
        try
        {
            return NodeReplay.Bind(s, _index, (options is null ? _doc!.BindOptions : _doc!.StricterOptions(options)).GetReflectiveTypeInfo<T>());
        }
        catch (YamlException e) when (YamlException.Tag(e, _doc!.SourceName))
        {
            throw;
        }
    }

    /// <summary>Binds the node to a type using generated metadata.</summary>
    /// <typeparam name="T">The type to read.</typeparam>
    /// <param name="typeInfo">The metadata for <typeparamref name="T"/>.</param>
    /// <returns>The value.</returns>
    /// <exception cref="YamlException">The node does not fit the type, or aliases expand beyond the limits.</exception>
    /// <exception cref="InvalidOperationException">The node was removed, or is detached.</exception>
    public T? Deserialize<T>(YamlTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        DocState s = State();
        try
        {
            return NodeReplay.Bind(s, _index, typeInfo);
        }
        catch (YamlException e) when (YamlException.Tag(e, _doc!.SourceName))
        {
            throw;
        }
    }

    // ---- Children ----

    /// <summary>Tells whether a mapping has a key with this text.</summary>
    /// <param name="key">The key's text.</param>
    /// <returns><see langword="true"/> if there is such a key.</returns>
    /// <exception cref="YamlException">Two keys have this text (<see cref="YamlErrorCategory.RefusedAmbiguous"/>).</exception>
    public bool ContainsKey(string key) => TryGetValue(key, out _);

    /// <summary>Gets the value of a mapping key.</summary>
    /// <param name="key">The key's text.</param>
    /// <param name="value">The value's node.</param>
    /// <returns><see langword="true"/> if the key is there.</returns>
    /// <exception cref="YamlException">Two keys have this text (<see cref="YamlErrorCategory.RefusedAmbiguous"/>).</exception>
    public bool TryGetValue(string key, [NotNullWhen(true)] out YamlNode? value)
    {
        ArgumentNullException.ThrowIfNull(key);
        DocState s = State();
        int t = Followed(s, out bool via);
        value = null!;
        if (s.Node(t).Kind != NodeKind.Mapping)
        {
            return false;
        }

        int child = PointerOps.FindChild(s, t, StrictUtf8Bytes(key), out _);
        if (child < 0)
        {
            return false;
        }

        value = new YamlNode(_doc!, child, _epoch!, via);
        return true;
    }

    /// <summary>Gets an item of a sequence.</summary>
    /// <param name="index">The item's position.</param>
    /// <param name="value">The item's node.</param>
    /// <returns><see langword="true"/> if the node is a sequence with an item at that position.</returns>
    public bool TryGetValue(int index, [NotNullWhen(true)] out YamlNode? value)
    {
        DocState s = State();
        int t = Followed(s, out bool via);
        value = null;
        if (s.Node(t).Kind != NodeKind.Sequence || (uint)index >= (uint)s.Node(t).Count)
        {
            return false;
        }

        value = new YamlNode(_doc!, s.ChildAt(t, index), _epoch!, via);
        return true;
    }

    private static byte[] StrictUtf8Bytes(string text)
    {
        try
        {
            return StrictUtf8.GetBytes(text);
        }
        catch (EncoderFallbackException)
        {
            throw new ArgumentException("The key contains an unpaired surrogate.", nameof(text));
        }
    }

    /// <summary>Gets or sets the value of a mapping key. Setting an existing key replaces its value; a new key is added at the end.</summary>
    /// <param name="key">The key's text.</param>
    /// <exception cref="KeyNotFoundException">Getting: the node is a mapping with no such key. Use <see cref="TryGetValue(string, out YamlNode)"/> to look without throwing.</exception>
    /// <exception cref="YamlException">Setting: the write goes through an alias (<see cref="YamlErrorCategory.RefusedAlias"/>). The document is unchanged.</exception>
    /// <exception cref="InvalidOperationException">The node is not a mapping, was removed, or is detached.</exception>
    /// <exception cref="ObjectDisposedException">The document was disposed.</exception>
    /// <example>
    /// <code language="csharp">
    /// using var doc = YamlDocument.Parse("spec:\n  replicas: 2  # scaled by CI\n");
    /// YamlNode spec = doc.Root["spec"];
    /// Console.WriteLine(spec["replicas"].GetValue&lt;int&gt;()); // 2
    /// spec["replicas"] = 3;
    /// spec["paused"] = false;
    /// Console.Write(doc);
    /// // spec:
    /// //   replicas: 3  # scaled by CI
    /// //   paused: false
    /// </code>
    /// </example>
    [AllowNull]
    public YamlNode this[string key]
    {
        get
        {
            if (TryGetValue(key, out YamlNode? v))
            {
                return v;
            }

            DocState s = State();
            ref readonly CstNode n = ref s.Node(Followed(s, out _));
            YamlPosition at = s.PositionOf(s.Node(_index).Start);
            if (n.Kind != NodeKind.Mapping)
            {
                string kind = n.Kind == NodeKind.Sequence ? "a sequence" : "a scalar";
                throw new InvalidOperationException($"Cannot read the key '{key}': the node at line {at.Line}, column {at.Column} is {kind}, not a mapping.");
            }

            throw new KeyNotFoundException($"The mapping at line {at.Line}, column {at.Column} has no key '{key}'.");
        }

        set
        {
            ArgumentNullException.ThrowIfNull(key);
            ValueTree v = ValueOf(value);
            DocState s = State();
            _doc!.BeginEdit(s);
            if (IsEmptyRoot(s))
            {
                // The root of a stream with no content becomes the mapping on its first write.
                _doc!.Swap(Editor.Replace(s, _doc.Limits, _index, ValueTree.Wrap(StrictUtf8Bytes(key), v)));
                return;
            }

            int container = WriteContainer(s, NodeKind.Mapping);
            _doc!.Swap(PointerOps.WriteIn(s, _doc.Limits, container, StrictUtf8Bytes(key), v, PointerOps.Mode.Set));
        }
    }

    /// <summary>Gets or sets an item of a sequence. Setting an index equal to the count appends.</summary>
    /// <param name="index">The item's position.</param>
    /// <exception cref="ArgumentOutOfRangeException">The index is outside the sequence (for setting, more than the count).</exception>
    /// <exception cref="YamlException">Setting: the write goes through an alias (<see cref="YamlErrorCategory.RefusedAlias"/>). The document is unchanged.</exception>
    /// <exception cref="InvalidOperationException">The node is not a sequence, was removed, or is detached.</exception>
    /// <example>
    /// <code language="csharp">
    /// using var doc = YamlDocument.Parse("ports: [80, 443]\n");
    /// YamlNode ports = doc.Root["ports"];
    /// ports[0] = 8080;
    /// ports[ports.Count] = 8443; // the count appends
    /// Console.Write(doc);
    /// // ports: [8080, 443, 8443]
    /// </code>
    /// </example>
    [AllowNull]
    public YamlNode this[int index]
    {
        get
        {
            DocState s = State();
            int t = Followed(s, out bool via);
            ref readonly CstNode n = ref s.Node(t);
            if (n.Kind != NodeKind.Sequence)
            {
                YamlPosition at = s.PositionOf(s.Node(_index).Start);
                throw new InvalidOperationException($"Cannot read item {index}: the node at line {at.Line}, column {at.Column} is {(n.Kind == NodeKind.Mapping ? "a mapping" : "a scalar")}, not a sequence.");
            }

            if ((uint)index >= (uint)n.Count)
            {
                YamlPosition at = s.PositionOf(s.Node(_index).Start);
                throw new ArgumentOutOfRangeException(nameof(index), index, $"Cannot read item {index}: the sequence at line {at.Line}, column {at.Column} has {n.Count} {(n.Count == 1 ? "item" : "items")}.");
            }

            return new YamlNode(_doc!, s.ChildAt(t, index), _epoch!, via);
        }

        set
        {
            ValueTree v = ValueOf(value);
            DocState s = State();
            _doc!.BeginEdit(s);
            if (index == 0 && IsEmptyRoot(s))
            {
                _doc!.Swap(Editor.Replace(s, _doc.Limits, _index, ValueTree.Wrap(null, v)));
                return;
            }

            int container = WriteContainer(s, NodeKind.Sequence);
            if (index < 0 || index > s.Node(container).Count)
            {
                YamlPosition at = s.PositionOf(s.Node(_index).Start);
                throw new ArgumentOutOfRangeException(nameof(index), index, $"Cannot write item {index}: the sequence at line {at.Line}, column {at.Column} has {s.Node(container).Count} {(s.Node(container).Count == 1 ? "item" : "items")}, so only an index up to the count is allowed.");
            }

            _doc!.Swap(PointerOps.WriteIn(s, _doc.Limits, container, Encoding.ASCII.GetBytes(index.ToString(System.Globalization.CultureInfo.InvariantCulture)), v, PointerOps.Mode.Set));
        }
    }

    // A document root with no text at all: nothing is written yet, so a write may give it a kind.
    private bool IsEmptyRoot(DocState s) => !_viaAlias && s.IsEmptyRoot(_index);

    // The collection an indexer writes into; writing through an alias is refused.
    private int WriteContainer(DocState s, NodeKind expected)
    {
        if (_viaAlias || s.Node(_index).Kind == NodeKind.Alias)
        {
            throw YamlException.At(YamlErrorCategory.RefusedAlias, "The edit would write through an alias and change every place that shares the anchored node.", s.Text, s.Node(_index).Start);
        }

        if (s.Node(_index).Kind != expected)
        {
            YamlPosition at = s.PositionOf(s.Node(_index).Start);
            throw new InvalidOperationException($"The node at line {at.Line}, column {at.Column} is not {(expected == NodeKind.Mapping ? "a mapping" : "a sequence")}.");
        }

        return _index;
    }

    /// <summary>Replaces this node's value, keeping its style where it can hold the new value.</summary>
    /// <param name="value">null, a string, a boolean, a number, a <see cref="YamlNode"/>, a dictionary or sequence of these, or a value of any other type the serializer can write.</param>
    /// <exception cref="ArgumentException">The value contains a type that has no YAML form, or one outside the plain value model while reflection is turned off (the default in trimmed and ahead-of-time applications); use the overload that takes a <see cref="YamlTypeInfo{T}"/> there.</exception>
    /// <exception cref="YamlException">The write goes through an alias, or the node holds an anchor that aliases still use inside it. The document is unchanged.</exception>
    /// <exception cref="InvalidOperationException">The node was removed, or is detached.</exception>
    /// <exception cref="ObjectDisposedException">The document was disposed.</exception>
    public void Set(object? value)
    {
        YamlLimits limits = _doc?.Limits ?? YamlLimits.Default;
        Replace(ValueTree.FromObject(value, limits.MaxDepth, limits));
    }

    /// <summary>Replaces this node's value with a value of any type <paramref name="typeInfo"/> describes, written as <see cref="YamlSerializer"/> writes it.</summary>
    /// <typeparam name="T">The type of the value.</typeparam>
    /// <param name="value">The value.</param>
    /// <param name="typeInfo">Metadata for <typeparamref name="T"/>, usually from a generated <see cref="YamlSerializerContext"/>.</param>
    /// <exception cref="ArgumentException">The value has no YAML form.</exception>
    /// <exception cref="YamlException">The write goes through an alias, or the node holds an anchor that aliases still use inside it. The document is unchanged.</exception>
    /// <exception cref="InvalidOperationException">The node was removed, or is detached.</exception>
    /// <exception cref="ObjectDisposedException">The document was disposed.</exception>
    public void Set<T>(T value, YamlTypeInfo<T> typeInfo) => Replace(TypedValues.Of(value, typeInfo, _doc?.Limits ?? YamlLimits.Default));

    /// <summary>Replaces this node's value with a value written by the reflection-based serializer.</summary>
    /// <typeparam name="T">The type of the value.</typeparam>
    /// <param name="value">The value.</param>
    /// <param name="options">Options for writing the value; <see langword="null"/> means <see cref="YamlSerializerOptions.Default"/>.</param>
    /// <exception cref="ArgumentException">The value has no YAML form.</exception>
    /// <exception cref="YamlException">The write goes through an alias, or the node holds an anchor that aliases still use inside it. The document is unchanged.</exception>
    /// <exception cref="InvalidOperationException">The node was removed, or is detached.</exception>
    /// <exception cref="ObjectDisposedException">The document was disposed.</exception>
    [RequiresUnreferencedCode("Reflection-based serialization reads members that trimming can remove. Pass a YamlTypeInfo from a YamlSerializerContext instead.")]
    [RequiresDynamicCode("Reflection-based serialization makes generic types at run time. Pass a YamlTypeInfo from a YamlSerializerContext instead.")]
    public void Set<T>(T value, YamlSerializerOptions? options) => Replace(TypedValues.Of(value, options, _doc?.Limits ?? YamlLimits.Default));

    private void Replace(ValueTree v)
    {
        DocState s = State();
        _doc!.BeginEdit(s);
        if (_viaAlias)
        {
            throw YamlException.At(YamlErrorCategory.RefusedAlias, "The edit would write through an alias and change every place that shares the anchored node.", s.Text, s.Node(_index).Start);
        }

        _doc!.Swap(Editor.Replace(s, _doc.Limits, _index, v));
    }

    /// <summary>Removes this node from its mapping or sequence. Removing the view of a mapping key removes the entry.</summary>
    /// <exception cref="YamlException">The node is a root, the write goes through an alias, or it holds an anchor that aliases still use. The document is unchanged.</exception>
    /// <exception cref="InvalidOperationException">The node was removed, or is detached.</exception>
    /// <exception cref="ObjectDisposedException">The document was disposed.</exception>
    public void Remove()
    {
        DocState s = State();
        _doc!.BeginEdit(s);
        if (_viaAlias)
        {
            throw YamlException.At(YamlErrorCategory.RefusedAlias, "The edit would write through an alias and change every place that shares the anchored node.", s.Text, s.Node(_index).Start);
        }

        // The key of an entry takes the entry with it: the edit works on the value, which names the entry.
        ref readonly CstNode n = ref s.Node(_index);
        int target = (n.Flags & NodeFlags.IsKey) != 0 ? _index + n.Size : _index;
        _doc!.Swap(Editor.Remove(s, _doc.Limits, target));
    }

    private ValueTree ValueOf(YamlNode? value)
    {
        ValueTree t = new();
        if (value is null)
        {
            return ValueTree.FromObject(null, (_doc?.Limits ?? YamlLimits.Default).MaxDepth);
        }

        value.AppendTo(t, _doc?.Limits ?? YamlLimits.Default);
        return t;
    }

    internal void AppendTo(ValueTree tree, YamlLimits limits)
    {
        if (_doc is null)
        {
            tree.AppendTree(_value as ValueTree ?? ValueTree.FromObject(_value, limits.MaxDepth, limits));
            return;
        }

        DocState s = State();
        var budget = new AliasBudget(s, _doc.Limits);
        tree.AppendFrom(s, _index, ref budget);
    }

    internal void AppendTo(ValueTree tree) => AppendTo(tree, _doc?.Limits ?? YamlLimits.Default);

    /// <summary>The items of a sequence, or the values of a mapping. Aliases are followed.</summary>
    public ItemEnumerable Items => new(this);

    /// <summary>The entries of a mapping. Aliases are followed.</summary>
    public EntryEnumerable Entries => new(this);

    /// <summary>The source text of the node, from its first property to its end. A detached node shows its value.</summary>
    /// <returns>The text.</returns>
    public override string ToString()
    {
        if (_doc is null)
        {
            // A detached node has no source text, so show the YAML its value would be written as.
            YamlLimits limits = YamlLimits.Default;
            ValueTree tree = _value as ValueTree ?? ValueTree.FromObject(_value, limits.MaxDepth, limits);
            if (tree[0].Kind == VKind.Scalar)
            {
                return Encoding.UTF8.GetString(tree[0].Text ?? []);
            }

            using var buffer = new ByteBuffer(256);
            ValueWriter.WriteBlock(buffer, tree, 0, 0, false, new Layout("\n", 2, 2));
            return Encoding.UTF8.GetString(buffer.WrittenSpan).TrimEnd('\n');
        }

        DocState s = State();
        int start = s.PropStart(_index);
        ChargeText(s, _index, _viaAlias, s.Node(_index).End - start);
        return Encoding.UTF8.GetString(s.Text.Slice(start, s.Node(_index).End - start));
    }

    /// <inheritdoc />
    public bool Equals(YamlNode? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (_doc is null || other._doc is null)
        {
            return _doc is null && other._doc is null && Equals(_value, other._value);
        }

        // Both views are brought up to date first, so equal views name one node whichever edits they have seen.
        return ReferenceEquals(_doc, other._doc) && _viaAlias == other._viaAlias && CurrentIndex() is var mine && mine >= 0 && mine == other.CurrentIndex();
    }

    // The node's place now, or -1 when the view cannot be brought up to date.
    private int CurrentIndex()
    {
        try
        {
            State();
            return _index;
        }
        catch (InvalidOperationException)
        {
            // Removed, stale through an alias, or disposed (a kind of InvalidOperationException).
            return -1;
        }
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is YamlNode other && Equals(other);

    /// <inheritdoc />
    /// <remarks>
    /// Views of one document share a hash code, because a node's place in the document changes with edits and a
    /// hash must not. A hash set of many views of one document is therefore slow; key a dictionary by something
    /// else (a pointer string, say).
    /// </remarks>
    public override int GetHashCode() => _doc is null ? _value?.GetHashCode() ?? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_doc);

    /// <summary>Tells whether two views name the same node of the same document.</summary>
    /// <param name="left">A view.</param>
    /// <param name="right">A view.</param>
    /// <returns><see langword="true"/> if they are equal.</returns>
    public static bool operator ==(YamlNode? left, YamlNode? right) => left is null ? right is null : left.Equals(right);

    /// <summary>Tells whether two views differ.</summary>
    /// <param name="left">A view.</param>
    /// <param name="right">A view.</param>
    /// <returns><see langword="true"/> if they are not equal.</returns>
    public static bool operator !=(YamlNode? left, YamlNode? right) => !(left == right);

    /// <summary>The nodes of a collection. The enumerator is a struct; each node it returns is a new view.</summary>
    public readonly struct ItemEnumerable : IEnumerable<YamlNode>
    {
        private readonly YamlNode _node;

        internal ItemEnumerable(YamlNode node)
        {
            _node = node;
        }

        /// <summary>Gets the enumerator.</summary>
        /// <returns>An enumerator.</returns>
        public ItemEnumerator GetEnumerator() => new(_node);

        IEnumerator<YamlNode> IEnumerable<YamlNode>.GetEnumerator() => GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// Enumerates the items of a sequence or the values of a mapping. Edits made while it runs (through the
    /// items it returns, say) are followed: it carries on with the item after the one it returned last.
    /// </summary>
    public struct ItemEnumerator : IEnumerator<YamlNode>
    {
        private readonly YamlNode _node;
        private readonly bool _via;
        private readonly int _step;
        private EditEpoch? _seen;
        private int _next;
        private int _position;
        private int _remaining;

        internal ItemEnumerator(YamlNode node)
        {
            _node = node;
            Current = default!;
            _seen = null;
            _position = 0;
            if (node is null)
            {
                // default(ItemEnumerable): nothing to enumerate.
                _via = false;
                _step = 1;
                _next = 0;
                _remaining = 0;
                return;
            }

            DocState s = node.State();
            int t = node.Followed(s, out _via);
            ref readonly CstNode n = ref s.Node(t);
            _step = n.Kind == NodeKind.Mapping ? 2 : 1;
            _remaining = n.Kind is NodeKind.Mapping or NodeKind.Sequence ? n.Count : 0;
            if (_via && _remaining > 0)
            {
                node._doc!.Walk.ChargeRead(s, t, _remaining, 0);
            }

            _seen = node._epoch;
            _next = t + 1;
            if (_step == 2 && _remaining > 0)
            {
                _next = t + 1 + s.Node(t + 1).Size;
            }
        }

        /// <inheritdoc />
        public YamlNode Current { get; private set; }

        object IEnumerator.Current => Current;

        /// <inheritdoc />
        public bool MoveNext()
        {
            if (_node is null)
            {
                return false;
            }

            DocState s = _node.State();
            if (!ReferenceEquals(_seen, _node._epoch))
            {
                Relocate(s);
            }

            if (_remaining <= 0)
            {
                return false;
            }

            Current = new YamlNode(_node._doc!, _next, _node._epoch!, _via);
            _remaining -= _step;
            _position += _step;
            if (_remaining > 0)
            {
                int after = _next + s.Node(_next).Size;
                _next = _step == 2 ? after + s.Node(after).Size : after;
            }

            return true;
        }

        // The container changed since the last step: find the next child again by how many were returned.
        private void Relocate(DocState s)
        {
            int t = _node.Followed(s, out _);
            ref readonly CstNode n = ref s.Node(t);
            _seen = _node._epoch;
            if (n.Kind is not (NodeKind.Mapping or NodeKind.Sequence) || _position >= n.Count)
            {
                _remaining = 0;
                return;
            }

            _remaining = n.Count - _position;
            int c = t + 1;
            for (int i = 0; i < _position; i++)
            {
                c += s.Node(c).Size;
            }

            _next = _step == 2 ? c + s.Node(c).Size : c;
        }

        /// <inheritdoc />
        public void Reset() => throw new NotSupportedException();

        /// <inheritdoc />
        public void Dispose()
        {
        }
    }

    /// <summary>The entries of a mapping. The enumerator is a struct; each key and value it returns is a new view.</summary>
    public readonly struct EntryEnumerable : IEnumerable<KeyValuePair<YamlNode, YamlNode>>
    {
        private readonly YamlNode _node;

        internal EntryEnumerable(YamlNode node)
        {
            _node = node;
        }

        /// <summary>Gets the enumerator.</summary>
        /// <returns>An enumerator.</returns>
        public EntryEnumerator GetEnumerator() => new(_node);

        IEnumerator<KeyValuePair<YamlNode, YamlNode>> IEnumerable<KeyValuePair<YamlNode, YamlNode>>.GetEnumerator() => GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>Enumerates the entries of a mapping. Edits made while it runs are followed, as for <see cref="ItemEnumerator"/>.</summary>
    public struct EntryEnumerator : IEnumerator<KeyValuePair<YamlNode, YamlNode>>
    {
        private readonly YamlNode _node;
        private readonly bool _via;
        private EditEpoch? _seen;
        private int _next;
        private int _position;
        private int _remaining;

        internal EntryEnumerator(YamlNode node)
        {
            _node = node;
            Current = default;
            _seen = null;
            _position = 0;
            if (node is null)
            {
                // default(EntryEnumerable): nothing to enumerate.
                _via = false;
                _next = 0;
                _remaining = 0;
                return;
            }

            DocState s = node.State();
            int t = node.Followed(s, out _via);
            ref readonly CstNode n = ref s.Node(t);
            _remaining = n.Kind == NodeKind.Mapping ? n.Count : 0;
            if (_via && _remaining > 0)
            {
                node._doc!.Walk.ChargeRead(s, t, _remaining, 0);
            }

            _seen = node._epoch;
            _next = t + 1;
        }

        /// <inheritdoc />
        public KeyValuePair<YamlNode, YamlNode> Current { get; private set; }

        object IEnumerator.Current => Current;

        /// <inheritdoc />
        public bool MoveNext()
        {
            if (_node is null)
            {
                return false;
            }

            DocState s = _node.State();
            if (!ReferenceEquals(_seen, _node._epoch))
            {
                Relocate(s);
            }

            if (_remaining <= 0)
            {
                return false;
            }

            int value = _next + s.Node(_next).Size;
            Current = new KeyValuePair<YamlNode, YamlNode>(
                new YamlNode(_node._doc!, _next, _node._epoch!, _via),
                new YamlNode(_node._doc!, value, _node._epoch!, _via));
            _remaining -= 2;
            _position += 2;
            _next = value + s.Node(value).Size;
            return true;
        }

        private void Relocate(DocState s)
        {
            int t = _node.Followed(s, out _);
            ref readonly CstNode n = ref s.Node(t);
            _seen = _node._epoch;
            if (n.Kind != NodeKind.Mapping || _position >= n.Count)
            {
                _remaining = 0;
                return;
            }

            _remaining = n.Count - _position;
            int c = t + 1;
            for (int i = 0; i < _position; i++)
            {
                c += s.Node(c).Size;
            }

            _next = c;
        }

        /// <inheritdoc />
        public void Reset() => throw new NotSupportedException();

        /// <inheritdoc />
        public void Dispose()
        {
        }
    }
}
