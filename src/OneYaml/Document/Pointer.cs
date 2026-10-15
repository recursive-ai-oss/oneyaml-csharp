using System;
using System.Collections.Generic;
using System.Text;

namespace OneYaml.Documents;

/// <summary>An RFC 6901 JSON Pointer, split into tokens and unescaped, as UTF-8.</summary>
internal readonly struct Pointer
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly byte[] _data;
    private readonly int[] _bounds;

    private Pointer(byte[] data, int[] bounds)
    {
        _data = data;
        _bounds = bounds;
    }

    /// <summary>The number of tokens. The empty pointer, which names the whole document, has none.</summary>
    public int Count => _bounds.Length / 2;

    public ReadOnlySpan<byte> this[int i] => _data.AsSpan(_bounds[i * 2], _bounds[(i * 2) + 1]);

    /// <summary>Splits a pointer into tokens.</summary>
    /// <param name="pointer">The pointer text: empty, or starting with <c>/</c>.</param>
    /// <returns>The tokens.</returns>
    /// <exception cref="YamlException">The pointer does not start with a slash, or has an escape other than <c>~0</c> and <c>~1</c>.</exception>
    public static Pointer Parse(string pointer)
    {
        ArgumentNullException.ThrowIfNull(pointer);
        if (pointer.Length == 0)
        {
            return new Pointer([], []);
        }

        if (pointer[0] != '/')
        {
            throw new YamlException("A JSON Pointer must be empty or start with '/'.", YamlErrorCategory.PatchInvalid, 0, 0, -1);
        }

        byte[] utf8;
        try
        {
            utf8 = StrictUtf8.GetBytes(pointer);
        }
        catch (EncoderFallbackException)
        {
            throw new YamlException("The pointer contains an unpaired surrogate.", YamlErrorCategory.PatchInvalid, 0, 0, -1);
        }

        int tokens = 0;
        foreach (byte b in utf8)
        {
            if (b == (byte)'/')
            {
                tokens++;
            }
        }

        byte[] data = new byte[utf8.Length];
        int[] bounds = new int[tokens * 2];
        int w = 0;
        int t = -1;
        for (int i = 0; i < utf8.Length; i++)
        {
            byte b = utf8[i];
            if (b == (byte)'/')
            {
                if (t >= 0)
                {
                    bounds[(t * 2) + 1] = w - bounds[t * 2];
                }

                t++;
                bounds[t * 2] = w;
            }
            else if (b == (byte)'~')
            {
                byte next = i + 1 < utf8.Length ? utf8[i + 1] : (byte)0;
                if (next == (byte)'0')
                {
                    data[w++] = (byte)'~';
                }
                else if (next == (byte)'1')
                {
                    data[w++] = (byte)'/';
                }
                else
                {
                    throw new YamlException("A '~' in a JSON Pointer must be followed by 0 or 1.", YamlErrorCategory.PatchInvalid, 0, 0, -1);
                }

                i++;
            }
            else
            {
                data[w++] = b;
            }
        }

        bounds[(t * 2) + 1] = w - bounds[t * 2];
        return new Pointer(data, bounds);
    }

    /// <summary>Builds a pointer from tokens that are already unescaped.</summary>
    /// <param name="tokens">The tokens, UTF-8.</param>
    /// <returns>The pointer.</returns>
    public static Pointer FromTokens(IReadOnlyList<byte[]> tokens)
    {
        int total = 0;
        foreach (byte[] t in tokens)
        {
            total += t.Length;
        }

        byte[] data = new byte[total];
        int[] bounds = new int[tokens.Count * 2];
        int w = 0;
        for (int i = 0; i < tokens.Count; i++)
        {
            bounds[i * 2] = w;
            bounds[(i * 2) + 1] = tokens[i].Length;
            tokens[i].CopyTo(data, w);
            w += tokens[i].Length;
        }

        return new Pointer(data, bounds);
    }

    /// <summary>Tells whether the first <paramref name="count"/> tokens of this pointer equal the first tokens of another.</summary>
    /// <param name="other">The other pointer.</param>
    /// <param name="count">How many tokens to compare.</param>
    /// <returns><see langword="true"/> if both have at least that many tokens and they are equal.</returns>
    public bool StartsWith(in Pointer other, int count)
    {
        if (Count < count || other.Count < count)
        {
            return false;
        }

        for (int i = 0; i < count; i++)
        {
            if (!this[i].SequenceEqual(other[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Reads a token as a sequence index: no sign, no leading zero, within the int range.</summary>
    /// <param name="token">The token.</param>
    /// <param name="index">The index.</param>
    /// <returns><see langword="false"/> when the token is not an array index.</returns>
    public static bool TryIndex(ReadOnlySpan<byte> token, out int index)
    {
        index = 0;
        if (token.IsEmpty || token.Length > 10 || (token[0] == (byte)'0' && token.Length > 1))
        {
            return false;
        }

        long v = 0;
        foreach (byte b in token)
        {
            if (b is < (byte)'0' or > (byte)'9')
            {
                return false;
            }

            v = (v * 10) + (b - '0');
        }

        if (v > int.MaxValue)
        {
            return false;
        }

        index = (int)v;
        return true;
    }
}

/// <summary>Pointer resolution and the pointer-addressed edits, over one state.</summary>
internal static class PointerOps
{
    private static YamlException Path(DocState s, YamlErrorCategory category, string message, int node)
        => YamlException.At(category, message, s.Text, node >= 0 ? s.Node(node).Start : 0);

    /// <summary>Finds the child of a collection that a token names.</summary>
    /// <param name="s">The document.</param>
    /// <param name="container">A mapping or sequence (aliases already followed).</param>
    /// <param name="token">The unescaped token.</param>
    /// <param name="position">For a sequence, the index the token names (even when it is out of range).</param>
    /// <returns>The value or item node, or -1 when there is none.</returns>
    /// <exception cref="YamlException">Two keys match (<see cref="YamlErrorCategory.RefusedAmbiguous"/>), or the token is not an index into a sequence.</exception>
    public static int FindChild(DocState s, int container, ReadOnlySpan<byte> token, out int position)
    {
        position = -1;
        ref readonly CstNode c = ref s.Node(container);
        if (c.Kind == NodeKind.Sequence)
        {
            if (token.Length == 1 && token[0] == (byte)'-')
            {
                position = c.Count;
                return -1;
            }

            if (!Pointer.TryIndex(token, out int idx))
            {
                throw Path(s, YamlErrorCategory.PatchPath, "A sequence is addressed by a number.", container);
            }

            position = idx;
            return idx < c.Count ? s.ChildAt(container, idx) : -1;
        }

        int found = -1;
        int k = container + 1;
        for (int i = 0; i < c.Count; i += 2)
        {
            int v = k + s.Node(k).Size;
            if (s.Node(k).Kind == NodeKind.Scalar && KeyMatches(s, k, token))
            {
                if (found >= 0)
                {
                    throw Path(s, YamlErrorCategory.RefusedAmbiguous, "More than one key matches this pointer token.", k);
                }

                found = v;
            }

            k = v + s.Node(v).Size;
        }

        return found;
    }

    private static bool KeyMatches(DocState s, int key, ReadOnlySpan<byte> token)
    {
        if (s.IsRaw(key))
        {
            return s.RawContent(key).SequenceEqual(token);
        }

        ReadOnlySpan<byte> text = s.DecodedScalar(key, out byte[]? rented);
        try
        {
            return text.SequenceEqual(token);
        }
        finally
        {
            DocState.ReleaseDecoded(rented);
        }
    }

    /// <summary>Follows the first tokens of a pointer from a document root.</summary>
    /// <param name="s">The document.</param>
    /// <param name="limits">The limits for alias budgets.</param>
    /// <param name="document">The document index.</param>
    /// <param name="p">The pointer.</param>
    /// <param name="take">How many tokens to follow.</param>
    /// <param name="viaAlias">Set when an alias was followed on the way.</param>
    /// <returns>The node reached (an alias is not followed at the end).</returns>
    public static int Walk(DocState s, YamlLimits limits, int document, in Pointer p, int take, out bool viaAlias)
    {
        viaAlias = false;
        int cur = s.Doc(document).Root;
        var budget = new AliasBudget(s, limits);
        for (int i = 0; i < take; i++)
        {
            cur = Descend(s, ref budget, cur, ref viaAlias);
            int child = FindChild(s, cur, p[i], out _);
            if (child < 0)
            {
                throw Path(s, YamlErrorCategory.PatchPath, "The pointer names nothing in this document.", cur);
            }

            cur = child;
        }

        return cur;
    }

    /// <summary>Follows an alias (charging the budget) and checks the node can hold children.</summary>
    public static int Descend(DocState s, ref AliasBudget budget, int node, ref bool viaAlias)
    {
        if (s.Node(node).Kind == NodeKind.Alias)
        {
            int target = s.Node(node).Aux;
            budget.Charge(s, node, target);
            node = target;
            viaAlias = true;
        }

        if (s.Node(node).Kind == NodeKind.Scalar)
        {
            throw Path(s, YamlErrorCategory.PatchPath, "A scalar has no children to address.", node);
        }

        return node;
    }

    public static void CheckDocument(DocState s, int document)
    {
        if ((uint)document >= (uint)s.DocCount)
        {
            throw new YamlException("The stream has no document with this index.", YamlErrorCategory.PatchPath, 0, 0, -1);
        }
    }

    public enum Mode
    {
        /// <summary>Replace a value that exists, add a member that does not, append at <c>-</c> or the length.</summary>
        Set,

        /// <summary>RFC 6902 add: a sequence index inserts.</summary>
        Add,

        /// <summary>RFC 6902 replace: the target must exist.</summary>
        Replace,
    }

    /// <summary>Writes a value at a pointer, in one of the three modes.</summary>
    public static DocState Write(DocState s, YamlLimits limits, int document, in Pointer p, ValueTree v, Mode mode)
    {
        CheckDocument(s, document);
        if (p.Count == 0)
        {
            return Editor.Replace(s, limits, s.Doc(document).Root, v);
        }

        int cur = Walk(s, limits, document, p, p.Count - 1, out bool viaAlias);
        var budget = new AliasBudget(s, limits);
        int container = Descend(s, ref budget, cur, ref viaAlias);
        if (viaAlias)
        {
            throw Path(s, YamlErrorCategory.RefusedAlias, "The edit would write through an alias and change every place that shares the anchored node.", cur);
        }

        return WriteIn(s, limits, container, p[p.Count - 1], v, mode);
    }

    /// <summary>Writes a value into a collection under a token. The caller has followed and refused aliases.</summary>
    public static DocState WriteIn(DocState s, YamlLimits limits, int container, ReadOnlySpan<byte> token, ValueTree v, Mode mode)
    {
        int child = FindChild(s, container, token, out int position);
        if (s.Node(container).Kind == NodeKind.Mapping)
        {
            if (child >= 0)
            {
                return Editor.Replace(s, limits, child, v);
            }

            if (mode == Mode.Replace)
            {
                throw Path(s, YamlErrorCategory.PatchPath, "The pointer names nothing in this document.", container);
            }

            return Editor.AddMember(s, limits, container, token.ToArray(), v);
        }

        bool dash = token.Length == 1 && token[0] == (byte)'-';
        int count = s.Node(container).Count;
        switch (mode)
        {
            case Mode.Replace:
                if (child < 0 || dash)
                {
                    throw Path(s, YamlErrorCategory.PatchPath, "The pointer names nothing in this document.", container);
                }

                return Editor.Replace(s, limits, child, v);
            case Mode.Set when child >= 0:
                return Editor.Replace(s, limits, child, v);
            default:
                if (position > count)
                {
                    throw Path(s, YamlErrorCategory.PatchPath, "The index is outside the sequence.", container);
                }

                return Editor.InsertItem(s, limits, container, position, v);
        }
    }

    /// <summary>Works out where a write at a pointer would put its text, without writing.</summary>
    /// <param name="s">The state.</param>
    /// <param name="limits">The limits for alias budgets.</param>
    /// <param name="document">The document index.</param>
    /// <param name="p">The pointer.</param>
    /// <param name="mode">The write mode.</param>
    /// <returns>The offset and the nodes that the write replaces.</returns>
    /// <exception cref="YamlException">The write would be refused.</exception>
    public static Destination Locate(DocState s, YamlLimits limits, int document, in Pointer p, Mode mode)
    {
        CheckDocument(s, document);
        if (p.Count == 0)
        {
            int r = s.Doc(document).Root;
            return new Destination(s.PropStart(r), r, r + s.Node(r).Size, r);
        }

        int cur = Walk(s, limits, document, p, p.Count - 1, out bool viaAlias);
        var budget = new AliasBudget(s, limits);
        int container = Descend(s, ref budget, cur, ref viaAlias);
        if (viaAlias)
        {
            throw Path(s, YamlErrorCategory.RefusedAlias, "The edit would write through an alias and change every place that shares the anchored node.", cur);
        }

        int child = FindChild(s, container, p[p.Count - 1], out int position);
        bool replaces = child >= 0 && (s.Node(container).Kind == NodeKind.Mapping || mode != Mode.Add);
        if (replaces)
        {
            return new Destination(s.PropStart(child), child, child + s.Node(child).Size, child);
        }

        if (s.Node(container).Kind == NodeKind.Sequence && position >= 0 && position < s.Node(container).Count)
        {
            return new Destination(s.PropStart(s.ChildAt(container, position)), -1, -1, container);
        }

        return new Destination(s.Node(container).End, -1, -1, container);
    }

    /// <summary>Removes the entry a pointer names.</summary>
    public static DocState Remove(DocState s, YamlLimits limits, int document, in Pointer p)
    {
        CheckDocument(s, document);
        if (p.Count == 0)
        {
            throw Path(s, YamlErrorCategory.PatchInvalid, "The root of a document cannot be removed.", s.Doc(document).Root);
        }

        int cur = Walk(s, limits, document, p, p.Count - 1, out bool viaAlias);
        var budget = new AliasBudget(s, limits);
        int container = Descend(s, ref budget, cur, ref viaAlias);
        if (viaAlias)
        {
            throw Path(s, YamlErrorCategory.RefusedAlias, "The edit would write through an alias and change every place that shares the anchored node.", cur);
        }

        int child = FindChild(s, container, p[p.Count - 1], out _);
        if (child < 0)
        {
            throw Path(s, YamlErrorCategory.PatchPath, "The pointer names nothing in this document.", container);
        }

        return Editor.Remove(s, limits, child);
    }

    /// <summary>Finds the node a pointer names, following aliases for reading.</summary>
    /// <returns>The node, or -1 when the pointer names nothing.</returns>
    public static int Find(DocState s, YamlLimits limits, int document, in Pointer p)
    {
        CheckDocument(s, document);
        int cur = s.Doc(document).Root;
        var budget = new AliasBudget(s, limits);
        bool via = false;
        for (int i = 0; i < p.Count; i++)
        {
            if (s.Node(cur).Kind == NodeKind.Alias)
            {
                budget.Charge(s, cur, s.Node(cur).Aux);
                cur = s.Node(cur).Aux;
            }

            if (s.Node(cur).Kind == NodeKind.Scalar)
            {
                return -1;
            }

            int child = FindChild(s, cur, p[i], out _);
            if (child < 0)
            {
                return -1;
            }

            cur = child;
        }

        _ = via;
        if (s.Node(cur).Kind == NodeKind.Alias)
        {
            budget.Charge(s, cur, s.Node(cur).Aux);
            cur = s.Node(cur).Aux;
        }

        return cur;
    }
}
