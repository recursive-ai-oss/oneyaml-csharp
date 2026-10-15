using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using OneYaml.Compat.YamlDotNet.Core;
using OneYaml.Compat.YamlDotNet.Core.Events;
using OneYaml.Compat.YamlDotNet.Helpers;
using OneYaml.Compat.YamlDotNet.Internal;

namespace OneYaml.Compat.YamlDotNet.RepresentationModel;

/// <summary>The kind of a node in the representation model.</summary>
public enum YamlNodeType
{
    /// <summary>An alias.</summary>
    Alias,

    /// <summary>A mapping.</summary>
    Mapping,

    /// <summary>A scalar.</summary>
    Scalar,

    /// <summary>A sequence.</summary>
    Sequence,
}

/// <summary>Visits the parts of a representation model.</summary>
public interface IYamlVisitor
{
    /// <summary>Visits a stream.</summary>
    /// <param name="stream">The stream.</param>
    void Visit(YamlStream stream);

    /// <summary>Visits a document.</summary>
    /// <param name="document">The document.</param>
    void Visit(YamlDocument document);

    /// <summary>Visits a scalar.</summary>
    /// <param name="scalar">The scalar.</param>
    void Visit(YamlScalarNode scalar);

    /// <summary>Visits a sequence.</summary>
    /// <param name="sequence">The sequence.</param>
    void Visit(YamlSequenceNode sequence);

    /// <summary>Visits a mapping.</summary>
    /// <param name="mapping">The mapping.</param>
    void Visit(YamlMappingNode mapping);
}

/// <summary>A visitor that walks the whole model; override the methods you care about.</summary>
public abstract class YamlVisitorBase : IYamlVisitor
{
    /// <inheritdoc/>
    public virtual void Visit(YamlStream stream) => VisitChildren(stream);

    /// <inheritdoc/>
    public virtual void Visit(YamlDocument document) => VisitChildren(document);

    /// <inheritdoc/>
    public virtual void Visit(YamlScalarNode scalar)
    {
    }

    /// <inheritdoc/>
    public virtual void Visit(YamlSequenceNode sequence) => VisitChildren(sequence);

    /// <inheritdoc/>
    public virtual void Visit(YamlMappingNode mapping) => VisitChildren(mapping);

    // A walk through the base class is bounded in depth, in nodes visited and in scalar text seen, because a model
    // built from aliases can be far larger as a tree than it is in memory. The counts start over with each walk.
    private int _depth;
    private int _visits;
    private long _bytes;

    private void Enter()
    {
        if (_depth == 0)
        {
            _visits = 0;
            _bytes = 0;
        }

        if (++_depth > YamlNode.MaxDepth || ++_visits > YamlNode.MaxVisits || !System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            _depth = 0;
            throw new MaximumRecursionLevelReachedException("The node tree is nested or shared more deeply than the limits allow.");
        }
    }

    private void Count(YamlNode child)
    {
        if (++_visits > YamlNode.MaxVisits)
        {
            _depth = 0;
            throw new MaximumRecursionLevelReachedException("The node tree is nested or shared more deeply than the limits allow.");
        }

        if (child is YamlScalarNode { Value: { } text } && (_bytes += text.Length) > YamlNode.MaxTextChars)
        {
            _depth = 0;
            throw new MaximumRecursionLevelReachedException("The node tree holds more text than the limits allow.");
        }
    }

    /// <summary>Visits the documents of a stream.</summary>
    /// <param name="stream">The node.</param>
    protected virtual void VisitChildren(YamlStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Enter();
        try
        {
            foreach (YamlDocument document in stream.Documents)
            {
                document.Accept(this);
            }
        }
        finally
        {
            if (_depth > 0)
            {
                _depth--;
            }
        }
    }

    /// <summary>Visits the root of a document.</summary>
    /// <param name="document">The node.</param>
    protected virtual void VisitChildren(YamlDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        Enter();
        try
        {
            Count(document.RootNode);
            document.RootNode.Accept(this);
        }
        finally
        {
            if (_depth > 0)
            {
                _depth--;
            }
        }
    }

    /// <summary>Visits the items of a sequence.</summary>
    /// <param name="sequence">The node.</param>
    protected virtual void VisitChildren(YamlSequenceNode sequence)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        Enter();
        try
        {
            foreach (YamlNode node in sequence.Children)
            {
                Count(node);
                node.Accept(this);
            }
        }
        finally
        {
            if (_depth > 0)
            {
                _depth--;
            }
        }
    }

    /// <summary>Visits the keys and values of a mapping.</summary>
    /// <param name="mapping">The node.</param>
    protected virtual void VisitChildren(YamlMappingNode mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        Enter();
        try
        {
            foreach (KeyValuePair<YamlNode, YamlNode> pair in mapping.Children)
            {
                Count(pair.Key);
                pair.Key.Accept(this);
                Count(pair.Value);
                pair.Value.Accept(this);
            }
        }
        finally
        {
            if (_depth > 0)
            {
                _depth--;
            }
        }
    }
}

/// <summary>A node of a YAML document: a scalar, a sequence or a mapping.</summary>
public abstract class YamlNode
{
    // The deepest nesting and the most nodes any operation on a model walks. A model built from aliases can be far
    // larger as a tree than as text, so every walk is bounded.
    internal const int MaxDepth = 1000;
    internal const int MaxVisits = 1_000_000;

    // The most text, in characters, that ToString or a visitor walk may produce or see.
    internal const int MaxTextChars = 32 * 1024 * 1024;

    // Hashing looks at this many nodes at most, level by level so that keys that differ in a late item still differ. Equal nodes agree on every node they share, so equal nodes hash alike,
    // and a key that is a huge shared structure costs no more to hash than a small one.
    private const int HashNodes = 32;

    private Loaded? _source;
    private int _startOffset = -1;
    private int _endOffset = -1;

    private protected YamlNode()
    {
    }

    /// <summary>The anchor of the node, or the empty name.</summary>
    public AnchorName Anchor { get; set; }

    /// <summary>The tag of the node, or the empty tag.</summary>
    public TagName Tag { get; set; }

    /// <summary>Where the node starts in the source it was loaded from.</summary>
    public Mark Start => _source is null ? Mark.Empty : _source.MarkAt(_startOffset);

    /// <summary>Where the node ends in the source it was loaded from.</summary>
    public Mark End => _source is null ? Mark.Empty : _source.MarkAt(_endOffset);

    /// <summary>The kind of node.</summary>
    public abstract YamlNodeType NodeType { get; }

    /// <summary>This node and every node below it. A node that is reachable twice is returned once.</summary>
    public IEnumerable<YamlNode> AllNodes
    {
        get
        {
            var seen = new HashSet<YamlNode>(ReferenceEqualityComparer.Instance);
            var stack = new Stack<YamlNode>();
            stack.Push(this);
            while (stack.Count > 0)
            {
                YamlNode node = stack.Pop();
                if (!seen.Add(node))
                {
                    continue;
                }

                yield return node;
                foreach (YamlNode child in node.ChildNodes().Reverse())
                {
                    stack.Push(child);
                }
            }
        }
    }

    /// <summary>Gets an item of a sequence by position.</summary>
    /// <param name="index">The position.</param>
    /// <exception cref="ArgumentException">The node is not a sequence.</exception>
    public virtual YamlNode this[int index] => throw new ArgumentException("Accessing a collection by index is only supported on sequences.", nameof(index));

    /// <summary>Gets a value of a mapping by key.</summary>
    /// <param name="key">The key.</param>
    /// <exception cref="ArgumentException">The node is not a mapping.</exception>
    public virtual YamlNode this[YamlNode key] => throw new ArgumentException("Accessing a collection by key is only supported on mappings.", nameof(key));

    /// <summary>Makes a scalar node from a string.</summary>
    /// <param name="value">The text.</param>
    /// <returns>The node.</returns>
    public static implicit operator YamlNode(string value) => new YamlScalarNode(value);

    /// <summary>Makes a sequence of scalar nodes from strings.</summary>
    /// <param name="sequence">The items.</param>
    /// <returns>The node.</returns>
    public static implicit operator YamlNode(string[] sequence) => new YamlSequenceNode(sequence.Select(s => (YamlNode)new YamlScalarNode(s)));

    /// <summary>Makes a node from a string.</summary>
    /// <param name="value">The text.</param>
    /// <returns>The node.</returns>
    public static YamlNode FromString(string value) => new YamlScalarNode(value);

    /// <summary>Accepts a visitor.</summary>
    /// <param name="visitor">The visitor.</param>
    public abstract void Accept(IYamlVisitor visitor);

    /// <inheritdoc/>
    public override string ToString()
    {
        var sb = new StringBuilder();
        int visits = 0;
        Print(sb, 0, ref visits);
        return sb.ToString();
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is YamlNode other && StructurallyEqual(this, other);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        if (this is YamlScalarNode only)
        {
            // The usual key is a scalar: no queue needed.
            hash.Add(NodeType);
            hash.Add(Tag.Value, StringComparer.Ordinal);
            hash.Add(only.Value, StringComparer.Ordinal);
            return hash.ToHashCode();
        }

        var queue = new Queue<YamlNode>();
        queue.Enqueue(this);
        int seen = 0;
        while (queue.Count > 0 && seen++ < HashNodes)
        {
            YamlNode node = queue.Dequeue();
            hash.Add(node.NodeType);
            hash.Add(node.Tag.Value, StringComparer.Ordinal);
            switch (node)
            {
                case YamlScalarNode scalar:
                    hash.Add(scalar.Value, StringComparer.Ordinal);
                    break;
                case YamlSequenceNode sequence:
                    hash.Add(sequence.Children.Count);
                    for (int i = 0; i < Math.Min(sequence.Children.Count, HashNodes); i++)
                    {
                        queue.Enqueue(sequence.Children[i]);
                    }

                    break;
                case YamlMappingNode mapping:
                    hash.Add(mapping.Children.Count);
                    var pairs = new List<YamlNode>();
                    foreach (KeyValuePair<YamlNode, YamlNode> pair in mapping.Children)
                    {
                        if (pairs.Count >= HashNodes)
                        {
                            break;
                        }

                        pairs.Add(pair.Key);
                        pairs.Add(pair.Value);
                    }

                    foreach (YamlNode child in pairs)
                    {
                        queue.Enqueue(child);
                    }

                    break;
            }
        }

        return hash.ToHashCode();
    }

    internal void SetSource(Loaded source, int start, int end)
    {
        _source = source;
        _startOffset = start;
        _endOffset = end;
    }

    internal Loaded? Source => _source;

    internal abstract IEnumerable<YamlNode> ChildNodes();

    internal abstract void Print(StringBuilder sb, int depth, ref int visits);

    // Text produced by ToString is bounded: a shared structure can print far larger than it is in memory.
    internal static void CheckSize(StringBuilder sb)
    {
        if (sb.Length > MaxTextChars)
        {
            throw new MaximumRecursionLevelReachedException("The text of the node tree is longer than the limits allow.");
        }
    }

    internal static void Guard(int depth, ref int visits)
    {
        if (depth > MaxDepth || ++visits > MaxVisits)
        {
            throw new MaximumRecursionLevelReachedException("The node tree is nested or shared more deeply than the limits allow.");
        }
    }

    // Structural equality with the same bounds as every other walk. Two nodes are equal when kind, tag and content agree.
    internal static bool StructurallyEqual(YamlNode a, YamlNode b)
    {
        var stack = new Stack<(YamlNode, YamlNode, int)>();
        stack.Push((a, b, 0));
        int visits = 0;
        long compared = 0;
        while (stack.Count > 0)
        {
            (YamlNode x, YamlNode y, int depth) = stack.Pop();
            if (ReferenceEquals(x, y))
            {
                continue;
            }

            Guard(depth, ref visits);
            if (x.GetType() != y.GetType() || x.Tag != y.Tag)
            {
                return false;
            }

            switch (x)
            {
                case YamlScalarNode sx:
                    if (!string.Equals(sx.Value, ((YamlScalarNode)y).Value, StringComparison.Ordinal))
                    {
                        return false;
                    }

                    if ((compared += sx.Value?.Length ?? 0) > MaxTextChars)
                    {
                        throw new MaximumRecursionLevelReachedException("The node trees hold more text than the limits allow.");
                    }

                    break;
                case YamlSequenceNode qx:
                    {
                        var qy = (YamlSequenceNode)y;
                        if (qx.Children.Count != qy.Children.Count)
                        {
                            return false;
                        }

                        for (int i = 0; i < qx.Children.Count; i++)
                        {
                            stack.Push((qx.Children[i], qy.Children[i], depth + 1));
                        }

                        break;
                    }

                case YamlMappingNode mx:
                    {
                        var my = (YamlMappingNode)y;
                        if (mx.Children.Count != my.Children.Count)
                        {
                            return false;
                        }

                        for (int i = 0; i < mx.Children.Count; i++)
                        {
                            stack.Push((mx.Children[i].Key, my.Children[i].Key, depth + 1));
                            stack.Push((mx.Children[i].Value, my.Children[i].Value, depth + 1));
                        }

                        break;
                    }
            }
        }

        return true;
    }
}

/// <summary>A scalar node: text with an optional style, tag and anchor.</summary>
public sealed class YamlScalarNode : YamlNode
{
    /// <summary>Creates a scalar with no value.</summary>
    public YamlScalarNode()
    {
    }

    /// <summary>Creates a scalar.</summary>
    /// <param name="value">The text.</param>
    public YamlScalarNode(string? value)
    {
        Value = value;
    }

    /// <summary>The text of the scalar.</summary>
    public string? Value { get; set; }

    /// <summary>How the scalar is written. <see cref="ScalarStyle.Any"/> lets the writer choose.</summary>
    public ScalarStyle Style { get; set; }

    /// <inheritdoc/>
    public override YamlNodeType NodeType => YamlNodeType.Scalar;

    /// <inheritdoc/>
    public override void Accept(IYamlVisitor visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        visitor.Visit(this);
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => base.Equals(obj);

    /// <inheritdoc/>
    public override int GetHashCode() => base.GetHashCode();

    internal override IEnumerable<YamlNode> ChildNodes() => [];

    internal override void Print(StringBuilder sb, int depth, ref int visits)
    {
        Guard(depth, ref visits);
        sb.Append(Value);
        CheckSize(sb);
    }

}

/// <summary>A sequence node.</summary>
public sealed class YamlSequenceNode : YamlNode, IEnumerable<YamlNode>
{
    private readonly List<YamlNode> _children = [];

    /// <summary>Creates an empty sequence.</summary>
    public YamlSequenceNode()
    {
    }

    /// <summary>Creates a sequence of items.</summary>
    /// <param name="children">The items.</param>
    public YamlSequenceNode(params YamlNode[] children)
        : this((IEnumerable<YamlNode>)children)
    {
    }

    /// <summary>Creates a sequence of items.</summary>
    /// <param name="children">The items.</param>
    public YamlSequenceNode(IEnumerable<YamlNode> children)
    {
        ArgumentNullException.ThrowIfNull(children);
        _children.AddRange(children);
    }

    /// <summary>The items.</summary>
    public IList<YamlNode> Children => _children;

    /// <summary>How the sequence is written.</summary>
    public SequenceStyle Style { get; set; }

    /// <inheritdoc/>
    public override YamlNodeType NodeType => YamlNodeType.Sequence;

    /// <inheritdoc/>
    public override YamlNode this[int index] => Children[index];

    /// <summary>Adds an item.</summary>
    /// <param name="child">The item.</param>
    public void Add(YamlNode child) => Children.Add(child ?? throw new ArgumentNullException(nameof(child)));

    /// <summary>Adds a scalar item.</summary>
    /// <param name="child">The text.</param>
    public void Add(string child) => Children.Add(new YamlScalarNode(child));

    /// <inheritdoc/>
    public override void Accept(IYamlVisitor visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        visitor.Visit(this);
    }

    /// <inheritdoc/>
    public IEnumerator<YamlNode> GetEnumerator() => Children.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <inheritdoc/>
    public override bool Equals(object? obj) => base.Equals(obj);

    /// <inheritdoc/>
    public override int GetHashCode() => base.GetHashCode();

    internal override IEnumerable<YamlNode> ChildNodes() => Children;

    internal override void Print(StringBuilder sb, int depth, ref int visits)
    {
        Guard(depth, ref visits);
        CheckSize(sb);
        sb.Append("[ ");
        for (int i = 0; i < Children.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }

            Children[i].Print(sb, depth + 1, ref visits);
        }

        sb.Append(" ]");
    }

}

/// <summary>A mapping node.</summary>
public sealed class YamlMappingNode : YamlNode, IEnumerable<KeyValuePair<YamlNode, YamlNode>>
{
    /// <summary>Creates an empty mapping.</summary>
    public YamlMappingNode()
    {
    }

    /// <summary>Creates a mapping from entries.</summary>
    /// <param name="children">The entries.</param>
    public YamlMappingNode(params KeyValuePair<YamlNode, YamlNode>[] children)
        : this((IEnumerable<KeyValuePair<YamlNode, YamlNode>>)children)
    {
    }

    /// <summary>Creates a mapping from entries.</summary>
    /// <param name="children">The entries.</param>
    public YamlMappingNode(IEnumerable<KeyValuePair<YamlNode, YamlNode>> children)
    {
        ArgumentNullException.ThrowIfNull(children);
        foreach (KeyValuePair<YamlNode, YamlNode> pair in children)
        {
            Children.Add(pair);
        }
    }

    /// <summary>Creates a mapping from keys and values in turn: key, value, key, value.</summary>
    /// <param name="children">The nodes.</param>
    public YamlMappingNode(params YamlNode[] children)
        : this((IEnumerable<YamlNode>)children)
    {
    }

    /// <summary>Creates a mapping from keys and values in turn: key, value, key, value.</summary>
    /// <param name="children">The nodes.</param>
    public YamlMappingNode(IEnumerable<YamlNode> children)
    {
        ArgumentNullException.ThrowIfNull(children);
        using IEnumerator<YamlNode> e = children.GetEnumerator();
        while (e.MoveNext())
        {
            YamlNode key = e.Current;
            if (!e.MoveNext())
            {
                throw new ArgumentException("When constructing a mapping node with a sequence, the number of elements of the sequence must be even.", nameof(children));
            }

            Add(key, e.Current);
        }
    }

    /// <summary>The entries, in the order they were added.</summary>
    public IOrderedDictionary<YamlNode, YamlNode> Children { get; } = new Helpers.OrderedDictionary<YamlNode, YamlNode>();

    /// <summary>How the mapping is written.</summary>
    public MappingStyle Style { get; set; }

    /// <inheritdoc/>
    public override YamlNodeType NodeType => YamlNodeType.Mapping;

    /// <inheritdoc/>
    public override YamlNode this[YamlNode key] => Children[key];

    /// <summary>Adds an entry.</summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The value.</param>
    public void Add(YamlNode key, YamlNode value) => Children.Add(key ?? throw new ArgumentNullException(nameof(key)), value ?? throw new ArgumentNullException(nameof(value)));

    /// <summary>Adds an entry with a scalar key.</summary>
    /// <param name="key">The key text.</param>
    /// <param name="value">The value.</param>
    public void Add(string key, YamlNode value) => Add(new YamlScalarNode(key), value);

    /// <summary>Adds an entry with a scalar value.</summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The value text.</param>
    public void Add(YamlNode key, string value) => Add(key, new YamlScalarNode(value));

    /// <summary>Adds an entry with a scalar key and value.</summary>
    /// <param name="key">The key text.</param>
    /// <param name="value">The value text.</param>
    public void Add(string key, string value) => Add(new YamlScalarNode(key), new YamlScalarNode(value));

    /// <inheritdoc/>
    public override void Accept(IYamlVisitor visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        visitor.Visit(this);
    }

    /// <inheritdoc/>
    public IEnumerator<KeyValuePair<YamlNode, YamlNode>> GetEnumerator() => Children.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <inheritdoc/>
    public override bool Equals(object? obj) => base.Equals(obj);

    /// <inheritdoc/>
    public override int GetHashCode() => base.GetHashCode();

    internal override IEnumerable<YamlNode> ChildNodes()
    {
        foreach (KeyValuePair<YamlNode, YamlNode> pair in Children)
        {
            yield return pair.Key;
            yield return pair.Value;
        }
    }

    internal override void Print(StringBuilder sb, int depth, ref int visits)
    {
        Guard(depth, ref visits);
        CheckSize(sb);
        sb.Append("{ ");
        bool first = true;
        foreach (KeyValuePair<YamlNode, YamlNode> pair in Children)
        {
            if (!first)
            {
                sb.Append(", ");
            }

            first = false;
            sb.Append("{ ");
            pair.Key.Print(sb, depth + 1, ref visits);
            sb.Append(", ");
            pair.Value.Print(sb, depth + 1, ref visits);
            sb.Append(" }");
        }

        sb.Append(" }");
    }

}
