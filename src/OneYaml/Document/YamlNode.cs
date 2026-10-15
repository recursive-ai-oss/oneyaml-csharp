using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Text;
using OneYaml.Documents;
using OneYaml.Schema;

namespace OneYaml;

/// <summary>
/// A lightweight view of one node of a <see cref="YamlDocument"/>. Reading follows aliases.
/// Any edit of the document invalidates every view taken before it: using one afterwards throws
/// <see cref="InvalidOperationException"/>. A node made from a value (a string, a number,
/// <see langword="null"/>…) is detached and can only be assigned into a document.
/// </summary>
public sealed class YamlNode : IEquatable<YamlNode>
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly YamlDocument? _doc;
    private readonly int _index;
    private readonly int _version;
    private readonly bool _viaAlias;
    private readonly object? _value;

    internal YamlNode(YamlDocument doc, int index, int version, bool viaAlias)
    {
        _doc = doc;
        _index = index;
        _version = version;
        _viaAlias = viaAlias;
        _value = null;
    }

    private YamlNode(object? detached)
    {
        _doc = null;
        _index = -1;
        _version = 0;
        _viaAlias = false;
        _value = detached;
    }

    /// <summary>Makes a detached node from a value, to assign into a document.</summary>
    /// <param name="value">null, a string, a boolean, a number, or a dictionary or sequence of these.</param>
    /// <returns>The detached node.</returns>
    public static YamlNode Create(object? value) => new(value);

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
        if (_doc is null)
        {
            throw new InvalidOperationException("A detached node has no position in a document.");
        }

        if (_doc.Version != _version)
        {
            throw new InvalidOperationException("The document was edited after this node was taken; take the node again.");
        }

        return _doc.State;
    }

    // The node whose content reads see: aliases followed, with the budget charged.
    private int Followed(DocState s, out bool via)
    {
        via = _viaAlias;
        if (s.Node(_index).Kind != NodeKind.Alias)
        {
            return _index;
        }

        // One budget for the whole walk from a root, so a traversal that follows aliases without end hits the limit.
        _doc!.Walk.Charge(s, _index, s.Node(_index).Aux);
        via = true;
        return s.Node(_index).Aux;
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
            return new YamlNode(_doc!, t, _version, via);
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

    /// <summary>The tag as written, such as <c>!!str</c> or <c>!local</c>, or <see langword="null"/>.</summary>
    public string? Tag
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
                return _value is null;
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
        int t = Followed(s, out _);
        if (NodeValues.TryGet(s, t, _doc!.BindOptions, out T value, out YamlException? error))
        {
            return value;
        }

        throw error!;
    }

    /// <summary>Reads a scalar as a CLR value, if it has that type.</summary>
    /// <typeparam name="T">A string, boolean, integer, floating-point, big integer, character, date or time, or GUID type, or <see cref="object"/>.</typeparam>
    /// <param name="value">The value, or the default.</param>
    /// <returns><see langword="true"/> if the node is a scalar of that type.</returns>
    public bool TryGetValue<T>(out T value)
    {
        DocState s = State();
        int t = Followed(s, out _);
        return NodeValues.TryGet(s, t, _doc!.BindOptions, out value, out _);
    }

    /// <summary>Binds the node to a type with the reflection binder, as <see cref="YamlSerializer"/> would bind the same text.</summary>
    /// <typeparam name="T">The type to read.</typeparam>
    /// <param name="options">Options; <see langword="null"/> means <see cref="YamlSerializerOptions.Default"/>.</param>
    /// <returns>The value.</returns>
    /// <exception cref="YamlException">The node does not fit the type, or aliases expand beyond the limits.</exception>
    /// <exception cref="InvalidOperationException">The view is stale or detached.</exception>
    [RequiresUnreferencedCode("Reflection-based binding reads members and constructors that trimming can remove. Pass a YamlTypeInfo from a YamlSerializerContext instead.")]
    [RequiresDynamicCode("Reflection-based binding makes generic types and compiles accessors at run time. Pass a YamlTypeInfo from a YamlSerializerContext instead.")]
    public T? Deserialize<T>(YamlSerializerOptions? options = null) => NodeReplay.Bind(State(), _index, (options ?? YamlSerializerOptions.Default).GetTypeInfo<T>());

    /// <summary>Binds the node to a type using generated metadata.</summary>
    /// <typeparam name="T">The type to read.</typeparam>
    /// <param name="typeInfo">The metadata for <typeparamref name="T"/>.</param>
    /// <returns>The value.</returns>
    /// <exception cref="YamlException">The node does not fit the type, or aliases expand beyond the limits.</exception>
    /// <exception cref="InvalidOperationException">The view is stale or detached.</exception>
    public T? Deserialize<T>(YamlTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        return NodeReplay.Bind(State(), _index, typeInfo);
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

        value = new YamlNode(_doc!, child, _version, via);
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
    /// <exception cref="YamlException">Getting: the node is not a mapping or has no such key (<see cref="YamlErrorCategory.PatchPath"/>). Setting: that, or the write goes through an alias. The document is unchanged.</exception>
    /// <exception cref="InvalidOperationException">The view is stale or detached.</exception>
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
            throw YamlException.At(YamlErrorCategory.PatchPath, "The mapping has no such key.", s.Text, s.Node(_index).Start);
        }

        set
        {
            ArgumentNullException.ThrowIfNull(key);
            ValueTree v = ValueOf(value);
            DocState s = State();
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
    /// <exception cref="YamlException">The node is not a sequence or the index is outside it (<see cref="YamlErrorCategory.PatchPath"/>), or the write goes through an alias. The document is unchanged.</exception>
    /// <exception cref="InvalidOperationException">The view is stale or detached.</exception>
    [AllowNull]
    public YamlNode this[int index]
    {
        get
        {
            DocState s = State();
            int t = Followed(s, out bool via);
            if (s.Node(t).Kind != NodeKind.Sequence || (uint)index >= (uint)s.Node(t).Count)
            {
                throw YamlException.At(YamlErrorCategory.PatchPath, "The node has no such item.", s.Text, s.Node(_index).Start);
            }

            return new YamlNode(_doc!, s.ChildAt(t, index), _version, via);
        }

        set
        {
            ValueTree v = ValueOf(value);
            DocState s = State();
            if (index == 0 && IsEmptyRoot(s))
            {
                _doc!.Swap(Editor.Replace(s, _doc.Limits, _index, ValueTree.Wrap(null, v)));
                return;
            }

            int container = WriteContainer(s, NodeKind.Sequence);
            if (index < 0)
            {
                throw YamlException.At(YamlErrorCategory.PatchPath, "The index is outside the sequence.", s.Text, s.Node(_index).Start);
            }

            _doc!.Swap(PointerOps.WriteIn(s, _doc.Limits, container, Encoding.ASCII.GetBytes(index.ToString(System.Globalization.CultureInfo.InvariantCulture)), v, PointerOps.Mode.Set));
        }
    }

    // A document root with no text at all: nothing is written yet, so a write may give it a kind.
    private bool IsEmptyRoot(DocState s) => !_viaAlias && s.Node(_index).Parent < 0 && (s.Node(_index).Flags & NodeFlags.Empty) != 0 && s.Node(_index).Kind == NodeKind.Scalar && !s.HasTag(_index) && !s.HasAnchor(_index);

    // The collection an indexer writes into; writing through an alias is refused.
    private int WriteContainer(DocState s, NodeKind expected)
    {
        if (_viaAlias || s.Node(_index).Kind == NodeKind.Alias)
        {
            throw YamlException.At(YamlErrorCategory.RefusedAlias, "The edit would write through an alias and change every place that shares the anchored node.", s.Text, s.Node(_index).Start);
        }

        if (s.Node(_index).Kind != expected)
        {
            throw YamlException.At(YamlErrorCategory.PatchPath, expected == NodeKind.Mapping ? "The node is not a mapping." : "The node is not a sequence.", s.Text, s.Node(_index).Start);
        }

        return _index;
    }

    /// <summary>Replaces this node's value, keeping its style where it can hold the new value.</summary>
    /// <param name="value">null, a string, a boolean, a number, a <see cref="YamlNode"/>, or a dictionary or sequence of these.</param>
    /// <exception cref="ArgumentException">The value contains a type that has no YAML form.</exception>
    /// <exception cref="YamlException">The write goes through an alias, or the node holds an anchor that aliases still use inside it. The document is unchanged.</exception>
    /// <exception cref="InvalidOperationException">The view is stale or detached.</exception>
    public void Set(object? value)
    {
        ValueTree v = ValueTree.FromObject(value, (_doc?.Limits ?? YamlLimits.Default).MaxDepth);
        DocState s = State();
        if (_viaAlias)
        {
            throw YamlException.At(YamlErrorCategory.RefusedAlias, "The edit would write through an alias and change every place that shares the anchored node.", s.Text, s.Node(_index).Start);
        }

        _doc!.Swap(Editor.Replace(s, _doc.Limits, _index, v));
    }

    /// <summary>Removes this node from its mapping or sequence.</summary>
    /// <exception cref="YamlException">The node is a root, the write goes through an alias, or it holds an anchor that aliases still use. The document is unchanged.</exception>
    /// <exception cref="InvalidOperationException">The view is stale or detached.</exception>
    public void Remove()
    {
        DocState s = State();
        if (_viaAlias)
        {
            throw YamlException.At(YamlErrorCategory.RefusedAlias, "The edit would write through an alias and change every place that shares the anchored node.", s.Text, s.Node(_index).Start);
        }

        _doc!.Swap(Editor.Remove(s, _doc.Limits, _index));
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
            tree.AppendTree(ValueTree.FromObject(_value, limits.MaxDepth));
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
            return Convert.ToString(_value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        }

        DocState s = State();
        int start = s.PropStart(_index);
        return Encoding.UTF8.GetString(s.Text.Slice(start, s.Node(_index).End - start));
    }

    /// <inheritdoc />
    public bool Equals(YamlNode? other) => other is not null && ReferenceEquals(_doc, other._doc) && _index == other._index && _version == other._version && _viaAlias == other._viaAlias && Equals(_value, other._value);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is YamlNode other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(_doc, _index, _version, _value);

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

    /// <summary>Enumerates the items of a sequence or the values of a mapping.</summary>
    public struct ItemEnumerator : IEnumerator<YamlNode>
    {
        private readonly YamlNode _node;
        private readonly bool _via;
        private readonly int _step;
        private int _next;
        private int _remaining;

        internal ItemEnumerator(YamlNode node)
        {
            _node = node;
            Current = default!;
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
            if (_remaining <= 0)
            {
                return false;
            }

            DocState s = _node.State();
            Current = new YamlNode(_node._doc!, _next, _node._version, _via);
            _remaining -= _step;
            if (_remaining > 0)
            {
                int after = _next + s.Node(_next).Size;
                _next = _step == 2 ? after + s.Node(after).Size : after;
            }

            return true;
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

    /// <summary>Enumerates the entries of a mapping.</summary>
    public struct EntryEnumerator : IEnumerator<KeyValuePair<YamlNode, YamlNode>>
    {
        private readonly YamlNode _node;
        private readonly bool _via;
        private int _next;
        private int _remaining;

        internal EntryEnumerator(YamlNode node)
        {
            _node = node;
            Current = default;
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
            _next = t + 1;
        }

        /// <inheritdoc />
        public KeyValuePair<YamlNode, YamlNode> Current { get; private set; }

        object IEnumerator.Current => Current;

        /// <inheritdoc />
        public bool MoveNext()
        {
            if (_remaining <= 0)
            {
                return false;
            }

            DocState s = _node.State();
            int value = _next + s.Node(_next).Size;
            Current = new KeyValuePair<YamlNode, YamlNode>(
                new YamlNode(_node._doc!, _next, _node._version, _via),
                new YamlNode(_node._doc!, value, _node._version, _via));
            _remaining -= 2;
            _next = value + s.Node(value).Size;
            return true;
        }

        /// <inheritdoc />
        public void Reset() => throw new NotSupportedException();

        /// <inheritdoc />
        public void Dispose()
        {
        }
    }
}
