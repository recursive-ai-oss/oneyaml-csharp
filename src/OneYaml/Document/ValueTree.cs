using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;
using OneYaml.Emitting;
using OneYaml.Schema;

namespace OneYaml.Documents;

internal enum VKind : byte
{
    Scalar,
    Mapping,
    Sequence,
}

/// <summary>One node of a value to be written, in document order.</summary>
internal struct VNode
{
    public VKind Kind;

    /// <summary>For a scalar: what it is. A string is quoted when it must be; the others are written plain.</summary>
    public ScalarKind Scalar;

    /// <summary>The number of nodes in the subtree, including this one.</summary>
    public int Size;

    /// <summary>The number of children (a mapping counts keys and values separately).</summary>
    public int Count;

    /// <summary>The scalar's UTF-8 text.</summary>
    public byte[]? Text;
}

/// <summary>
/// A value to write into a document, flattened in document order so that building and writing it
/// need no recursion: only scalars, mappings and sequences, with aliases already expanded.
/// </summary>
internal sealed class ValueTree
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private VNode[] _nodes = new VNode[4];

    public int Count { get; private set; }

    public ref readonly VNode this[int index] => ref _nodes[index];

    public bool IsScalar => _nodes[0].Kind == VKind.Scalar;

    public static ValueTree Scalar(ScalarKind kind, byte[] text)
    {
        var t = new ValueTree();
        t.Add(new VNode { Kind = VKind.Scalar, Scalar = kind, Size = 1, Text = text });
        return t;
    }

    /// <summary>Builds a one-entry mapping or a one-item sequence around a value.</summary>
    /// <param name="key">The key's text for a mapping, or null for a sequence.</param>
    /// <param name="child">The entry's value or the item.</param>
    /// <returns>The new tree.</returns>
    public static ValueTree Wrap(byte[]? key, ValueTree child)
    {
        var t = new ValueTree();
        t.Add(new VNode { Kind = key is null ? VKind.Sequence : VKind.Mapping, Count = key is null ? 1 : 2, Size = 1 + (key is null ? 0 : 1) + child.Count });
        if (key is not null)
        {
            t.Add(new VNode { Kind = VKind.Scalar, Scalar = ScalarKind.String, Size = 1, Text = key });
        }

        t.AppendTree(child);
        return t;
    }

    public static ValueTree Empty(bool mapping)
    {
        var t = new ValueTree();
        t.Add(new VNode { Kind = mapping ? VKind.Mapping : VKind.Sequence, Size = 1 });
        return t;
    }

    private int Add(in VNode node)
    {
        if (Count == _nodes.Length)
        {
            Array.Resize(ref _nodes, Count * 2);
        }

        _nodes[Count] = node;
        return Count++;
    }

    // ---- From a CLR value ----

    private sealed class Frame
    {
        public IEnumerator? Items;
        public IDictionaryEnumerator? Entries;
        public int Node;
    }

    /// <summary>Flattens a CLR value.</summary>
    /// <param name="value">null, a string, a boolean, a number, a <see cref="YamlNode"/>, a dictionary or an enumerable of these.</param>
    /// <param name="maxDepth">How deeply the value may nest.</param>
    /// <returns>The tree.</returns>
    /// <exception cref="ArgumentException">The value contains a type that has no YAML form.</exception>
    /// <exception cref="YamlException">The value nests deeper than the limit.</exception>
    public static ValueTree FromObject(object? value, int maxDepth)
    {
        var tree = new ValueTree();
        var stack = new List<Frame>();
        tree.AddValue(value, stack, maxDepth);
        while (stack.Count > 0)
        {
            Frame f = stack[^1];
            if (f.Entries is not null)
            {
                if (!f.Entries.MoveNext())
                {
                    tree._nodes[f.Node].Size = tree.Count - f.Node;
                    stack.RemoveAt(stack.Count - 1);
                    continue;
                }

                tree.AddKey(f.Entries.Key);
                tree._nodes[f.Node].Count += 2;
                tree.AddValue(f.Entries.Value, stack, maxDepth);
            }
            else
            {
                if (!f.Items!.MoveNext())
                {
                    tree._nodes[f.Node].Size = tree.Count - f.Node;
                    stack.RemoveAt(stack.Count - 1);
                    continue;
                }

                tree._nodes[f.Node].Count++;
                tree.AddValue(f.Items.Current, stack, maxDepth);
            }
        }

        return tree;
    }

    /// <summary>Appends the nodes of another tree.</summary>
    /// <param name="other">The tree to copy.</param>
    public void AppendTree(ValueTree other)
    {
        for (int i = 0; i < other.Count; i++)
        {
            Add(in other._nodes[i]);
        }
    }

    private void AddKey(object? value)
    {
        if (value is null || value is IDictionary || (value is IEnumerable && value is not string) || value is YamlNode)
        {
            throw new ArgumentException("A mapping key must be a string or another scalar value.", nameof(value));
        }

        VNode s = ScalarOf(value);
        Add(in s);
    }

    private void AddValue(object? value, List<Frame> stack, int maxDepth)
    {
        if (value is YamlNode yn)
        {
            yn.AppendTo(this);
            return;
        }

        if (value is IDictionary dict)
        {
            Push(stack, maxDepth, new Frame { Entries = dict.GetEnumerator() }, VKind.Mapping);
            return;
        }

        if (value is IEnumerable seq && value is not string)
        {
            Push(stack, maxDepth, new Frame { Items = seq.GetEnumerator() }, VKind.Sequence);
            return;
        }

        VNode s = ScalarOf(value);
        Add(in s);
    }

    private void Push(List<Frame> stack, int maxDepth, Frame frame, VKind kind)
    {
        if (stack.Count >= maxDepth)
        {
            throw new YamlException("The value nests deeper than the configured limit.", YamlErrorCategory.Limit, 0, 0, -1);
        }

        frame.Node = Add(new VNode { Kind = kind, Size = 1 });
        stack.Add(frame);
    }

    private static VNode ScalarOf(object? value)
    {
        switch (value)
        {
            case null:
                return Make(ScalarKind.Null, "null"u8.ToArray());
            case string s:
                return Make(ScalarKind.String, Utf8Of(s));
            case char c:
                return Make(ScalarKind.String, Utf8Of(c.ToString()));
            case bool b:
                return Make(ScalarKind.Bool, b ? "true"u8.ToArray() : "false"u8.ToArray());
            case sbyte or byte or short or ushort or int or uint or long or ulong or nint or nuint:
                return Make(ScalarKind.Int, Ascii(((IFormattable)value).ToString(null, CultureInfo.InvariantCulture)));
            case BigInteger big:
                return Make(ScalarKind.Int, Ascii(big.ToString(CultureInfo.InvariantCulture)));
            case double d:
                return Make(ScalarKind.Float, FloatText(d, single: false));
            case float f:
                return Make(ScalarKind.Float, FloatText(f, single: true));
            case Half h:
                return Make(ScalarKind.Float, FloatText((double)h, single: false));
            case decimal m:
                {
                    string t = m.ToString(CultureInfo.InvariantCulture);
                    return Make(ScalarKind.Float, Ascii(t.Contains('.') ? t : t + ".0"));
                }

            case Enum e:
                return Make(ScalarKind.String, Utf8Of(e.ToString()));
            case Guid g:
                return Make(ScalarKind.String, Utf8Of(g.ToString()));
            default:
                throw new ArgumentException($"A value of type {value.GetType().Name} has no YAML form.", nameof(value));
        }
    }

    private static VNode Make(ScalarKind kind, byte[] text) => new() { Kind = VKind.Scalar, Scalar = kind, Size = 1, Text = text };

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    private static byte[] Utf8Of(string value)
    {
        try
        {
            return StrictUtf8.GetBytes(value);
        }
        catch (EncoderFallbackException)
        {
            throw new ArgumentException("The string contains an unpaired surrogate.", nameof(value));
        }
    }

    private static byte[] FloatText(double d, bool single)
    {
        using var buf = new ByteBuffer(40);
        var w = new Utf8YamlWriter(buf);
        if (single)
        {
            w.WriteSingle((float)d);
        }
        else
        {
            w.WriteDouble(d);
        }

        return buf.WrittenSpan.ToArray();
    }

    // ---- From a node of a document ----

    /// <summary>Appends the value of a node of a document, aliases expanded.</summary>
    /// <param name="state">The document.</param>
    /// <param name="node">The node.</param>
    /// <param name="budget">What aliases may expand to.</param>
    public void AppendFrom(DocState state, int node, ref AliasBudget budget)
    {
        int first = Count;
        var ranges = new List<(int Cur, int End)> { (node, node + state.Node(node).Size) };
        while (ranges.Count > 0)
        {
            int top = ranges.Count - 1;
            (int cur, int end) = ranges[top];
            if (cur >= end)
            {
                ranges.RemoveAt(top);
                continue;
            }

            ref readonly CstNode n = ref state.Node(cur);
            if (n.Kind == NodeKind.Alias)
            {
                int target = n.Aux;
                budget.Charge(state, cur, target);
                ranges[top] = (cur + 1, end);
                ranges.Add((target, target + state.Node(target).Size));
                continue;
            }

            if (n.Kind == NodeKind.Scalar)
            {
                ranges[top] = (cur + 1, end);
                Add(ScalarFrom(state, cur));
            }
            else
            {
                ranges[top] = (cur + 1, end);
                Add(new VNode { Kind = n.Kind == NodeKind.Mapping ? VKind.Mapping : VKind.Sequence, Size = 1, Count = n.Count });
            }
        }

        // Sizes come from the child counts, back to front.
        for (int i = Count - 1; i >= first; i--)
        {
            if (_nodes[i].Kind == VKind.Scalar)
            {
                _nodes[i].Size = 1;
                continue;
            }

            int size = 1;
            int c = i + 1;
            for (int k = 0; k < _nodes[i].Count; k++)
            {
                size += _nodes[c].Size;
                c += _nodes[c].Size;
            }

            _nodes[i].Size = size;
        }
    }

    private static VNode ScalarFrom(DocState state, int node)
    {
        ScalarKind kind = state.KindOf(node, out _);
        ReadOnlySpan<byte> text = state.DecodedScalar(node, out byte[]? rented);
        try
        {
            byte[] bytes = kind == ScalarKind.Null && text.IsEmpty ? "null"u8.ToArray() : text.ToArray();
            return Make(kind, bytes);
        }
        finally
        {
            DocState.ReleaseDecoded(rented);
        }
    }
}
