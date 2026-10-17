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

    /// <summary>The first tokens written back as an RFC 6901 pointer, escaping <c>~</c> and <c>/</c>.</summary>
    /// <param name="count">How many tokens.</param>
    /// <returns>The pointer text; empty for no tokens.</returns>
    public string Escaped(int count)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < count && i < Count; i++)
        {
            sb.Append('/');
            AppendEscaped(sb, this[i]);
        }

        return sb.ToString();
    }

    /// <summary>The first tokens as pointer text for a message: cut to a short excerpt with control characters escaped.</summary>
    /// <param name="count">How many tokens.</param>
    /// <returns>The excerpt; the full text stays available from <see cref="Escaped"/>.</returns>
    public string Excerpt(int count)
    {
        // Stop building once the excerpt is full: a pointer of 600 KB must not cost 600 KB per error.
        var sb = new StringBuilder();
        for (int i = 0; i < count && i < Count && sb.Length <= YamlException.MessageExcerpt; i++)
        {
            sb.Append('/');
            ReadOnlySpan<byte> token = this[i];
            AppendEscaped(sb, token.Length > YamlException.MessageExcerpt * 4 ? token[..(YamlException.MessageExcerpt * 4)] : token);
        }

        return MessageText.Bound(sb.ToString());
    }

    /// <summary>Appends a token to a string builder, escaped.</summary>
    public static void AppendEscaped(StringBuilder sb, ReadOnlySpan<byte> token)
    {
        foreach (char c in Encoding.UTF8.GetString(token))
        {
            switch (c)
            {
                case '~':
                    sb.Append("~0");
                    break;
                case '/':
                    sb.Append("~1");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
    }

    /// <inheritdoc />
    public override string ToString() => Escaped(Count);

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

/// <summary>Keeps text taken from input short and on one line when it goes into a message: the exception's own excerpt rules.</summary>
internal static class MessageText
{
    public static string Bound(string text) => YamlException.Excerpt(text, YamlException.MessageExcerpt);

    public static string Bound(ReadOnlySpan<byte> utf8)
    {
        // Four bytes per character at most, so this many bytes always cover the characters that are kept.
        const int MaxBytes = YamlException.MessageExcerpt * 4;
        bool cut = utf8.Length > MaxBytes;
        string head = Encoding.UTF8.GetString(cut ? utf8[..MaxBytes] : utf8);
        return Bound(cut ? head + "…" : head);
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
                throw Path(s, YamlErrorCategory.PatchPath, $"The token '{MessageText.Bound(token)}' cannot address an item: a sequence is addressed by an index (0, 1, ...), or '-' for the position after the last item.", container);
            }

            position = idx;
            return idx < c.Count ? s.ChildAt(container, idx) : -1;
        }

        if (c.Count >= DocState.IndexMinNodes)
        {
            int indexed = s.IndexedFind(container, token);
            if (indexed != DocState.NotIndexed)
            {
                if (DocState.VerifyCaches && c.Count <= DocState.VerifyMaxNodes)
                {
                    VerifyIndexed(s, container, token, indexed);
                }

                return indexed;
            }
        }

        return FindInOrder(s, container, token);
    }

    // The slow way, and the reference the index is held to: every key in the mapping's order.
    private static int FindInOrder(DocState s, int container, ReadOnlySpan<byte> token)
    {
        int count = s.Node(container).Count;
        int found = -1;
        int k = container + 1;
        int i = 0;
        for (; i < count; i += 2)
        {
            int v = k + s.Node(k).Size;
            if (s.Node(k).Kind == NodeKind.Scalar && KeyMatches(s, k, token))
            {
                if (found >= 0)
                {
                    EditWork.KeysExamined += i / 2;
                    throw Path(s, YamlErrorCategory.RefusedAmbiguous, $"More than one key matches the pointer token '{MessageText.Bound(token)}'.", k);
                }

                found = v;
            }

            k = v + s.Node(v).Size;
        }

        EditWork.KeysExamined += i / 2;
        return found;
    }

    private static void VerifyIndexed(DocState s, int container, ReadOnlySpan<byte> token, int indexed)
    {
        // The check is not part of the work the counters are there to show.
        long counted = EditWork.KeysExamined;
        int expected;
        try
        {
            expected = FindInOrder(s, container, token);
        }
        catch (YamlException)
        {
            throw new InvalidOperationException("The key index found a key where searching in order finds two.");
        }

        EditWork.KeysExamined = counted;

        if (expected != indexed)
        {
            throw new InvalidOperationException($"The key index found node {indexed}, searching in order found node {expected}.");
        }
    }

    private static bool KeyMatches(DocState s, int key, ReadOnlySpan<byte> token) => s.KeyTextMatches(key, token);

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
            cur = Descend(s, ref budget, cur, ref viaAlias, p, i);
            int child = FindChild(s, cur, p[i], out _);
            if (child < 0)
            {
                throw Missing(s, p, i, cur);
            }

            cur = child;
        }

        return cur;
    }

    /// <summary>Follows an alias (charging the budget) and checks the node can hold children.</summary>
    public static int Descend(DocState s, ref AliasBudget budget, int node, ref bool viaAlias) => Descend(s, ref budget, node, ref viaAlias, default, -1);

    /// <summary>Follows an alias (charging the budget) and checks the node, reached by the first tokens of a pointer, can hold children.</summary>
    public static int Descend(DocState s, ref AliasBudget budget, int node, ref bool viaAlias, in Pointer p, int resolved)
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
            string where = resolved >= 0 ? $"The pointer '{p.Excerpt(p.Count)}' names nothing: '{p.Excerpt(resolved)}' is a scalar (an empty value is a null), which has no members or items to address." : "The pointer goes below a scalar (an empty value is a null), which has no members or items to address.";
            throw Path(s, YamlErrorCategory.PatchPath, where, node);
        }

        return node;
    }

    private static YamlException NothingAt(DocState s, in Pointer context, int container)
        => context.Count > 0 ? Missing(s, context, context.Count - 1, container) : Path(s, YamlErrorCategory.PatchPath, "The pointer names nothing in this document.", container);

    /// <summary>Builds the error for a token that names nothing in a collection.</summary>
    /// <param name="s">The document.</param>
    /// <param name="p">The pointer.</param>
    /// <param name="at">The index of the token that names nothing.</param>
    /// <param name="container">The collection the token was looked up in (aliases followed).</param>
    /// <returns>The error, category <see cref="YamlErrorCategory.PatchPath"/>.</returns>
    public static YamlException Missing(DocState s, in Pointer p, int at, int container)
    {
        string has = at == 0 ? "the document root" : "'" + p.Excerpt(at) + "'";
        var sb = new StringBuilder();
        sb.Append("The pointer '").Append(p.Excerpt(p.Count)).Append("' names nothing: ");
        ref readonly CstNode c = ref s.Node(container);
        string token = MessageText.Bound(p[at]);
        if (c.Kind == NodeKind.Sequence)
        {
            if (p[at].Length == 1 && p[at][0] == (byte)'-')
            {
                sb.Append("the token '-' is the position after the last item of ").Append(has).Append(", not an item.");
            }
            else
            {
                sb.Append(has).Append(" is a sequence of ").Append(c.Count).Append(c.Count == 1 ? " item" : " items").Append(" and has no item ").Append(token).Append('.');
            }

            return Path(s, YamlErrorCategory.PatchPath, sb.ToString(), container);
        }

        sb.Append(has).Append(" exists but has no key '").Append(token).Append("'.");
        string? hint = EscapeHint(s, p, at, container);
        if (hint is not null)
        {
            sb.Append(' ').Append(hint);
        }

        return Path(s, YamlErrorCategory.PatchPath, sb.ToString(), container);
    }

    // The most joined tokens the hint tries. A key that spans more slashes than this is not a typo to explain.
    private const int MaxHintTokens = 64;

    // A '/' in a key is written ~1 in a pointer. When the tokens from here on, joined with '/', name a key of the
    // mapping, say so and show the pointer that works. The work is bounded by the longest key of the mapping and by
    // MaxHintTokens, so a pointer of many tokens costs one scan of the keys, not one per token.
    private static string? EscapeHint(DocState s, in Pointer p, int at, int container)
    {
        if (s.Node(container).Kind != NodeKind.Mapping || at + 1 >= p.Count)
        {
            return null;
        }

        int longest = LongestKey(s, container);
        byte[] joined = new byte[Math.Min(longest, p[at].Length + 64)];
        int length = p[at].Length;
        if (length > longest)
        {
            return null;
        }

        p[at].CopyTo(joined);
        for (int j = at + 1; j < p.Count && j <= at + MaxHintTokens; j++)
        {
            ReadOnlySpan<byte> next = p[j];
            if (length + 1 + next.Length > longest)
            {
                return null;
            }

            if (length + 1 + next.Length > joined.Length)
            {
                Array.Resize(ref joined, Math.Min(longest, Math.Max(joined.Length * 2, length + 1 + next.Length)));
            }

            joined[length++] = (byte)'/';
            next.CopyTo(joined.AsSpan(length));
            length += next.Length;
            if (!HasKey(s, container, joined.AsSpan(0, length)))
            {
                continue;
            }

            var fixedPointer = new StringBuilder(p.Escaped(at)).Append('/');
            Pointer.AppendEscaped(fixedPointer, joined.AsSpan(0, length));
            fixedPointer.Append(p.Escaped(p.Count).AsSpan(p.Escaped(j + 1).Length));
            string key = MessageText.Bound(joined.AsSpan(0, length));
            return $"A key '{key}' exists; a '/' inside a key is written '~1' in a pointer, so the pointer '{MessageText.Bound(fixedPointer.ToString())}' reaches it.";
        }

        return null;
    }

    private static int LongestKey(DocState s, int mapping)
    {
        int longest = 0;
        int k = mapping + 1;
        for (int i = 0; i < s.Node(mapping).Count; i += 2)
        {
            int v = k + s.Node(k).Size;
            if (s.Node(k).Kind == NodeKind.Scalar)
            {
                // The source length is an upper bound of the decoded length for a plain or quoted key.
                longest = Math.Max(longest, s.Node(k).End - s.Node(k).Start);
            }

            k = v + s.Node(v).Size;
        }

        return longest;
    }

    private static bool HasKey(DocState s, int mapping, ReadOnlySpan<byte> key)
    {
        int k = mapping + 1;
        for (int i = 0; i < s.Node(mapping).Count; i += 2)
        {
            int v = k + s.Node(k).Size;
            if (s.Node(k).Kind == NodeKind.Scalar && KeyMatches(s, k, key))
            {
                return true;
            }

            k = v + s.Node(v).Size;
        }

        return false;
    }

    public static void CheckDocument(DocState s, int document)
    {
        if ((uint)document >= (uint)s.DocCount)
        {
            throw NoSuchDocument(document, s.NoRealDocument ? 0 : s.DocCount);
        }
    }

    /// <summary>The error for a document index outside the stream, the same wherever an index is taken.</summary>
    /// <param name="document">The index asked for.</param>
    /// <param name="count">How many documents the stream has.</param>
    /// <returns>The error.</returns>
    public static ArgumentOutOfRangeException NoSuchDocument(int document, int count)
        => new(nameof(document), document, NoSuchDocumentMessage(document, count));

    /// <summary>The same error for patches, which report problems as <see cref="YamlException"/> with a category.</summary>
    /// <param name="document">The index asked for.</param>
    /// <param name="count">How many documents the stream has.</param>
    /// <returns>The error, category <see cref="YamlErrorCategory.PatchPath"/>.</returns>
    public static YamlException NoSuchDocumentForPatch(int document, int count)
        => new(NoSuchDocumentMessage(document, count), YamlErrorCategory.PatchPath, 0, 0, -1);

    private static string NoSuchDocumentMessage(int document, int count)
        => $"The stream has {count} {(count == 1 ? "document" : "documents")}, so there is no document at index {document}.";

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

        int docRoot = s.Doc(document).Root;
        // An add is an RFC 6902 add: its parent has to exist, and the root of a document that is there
        // (a marker alone, say) is a null. Only a stream with no document yet is given one by an add.
        if (p.Count == 1 && mode != Mode.Replace && s.IsEmptyRoot(docRoot) && (mode == Mode.Set || s.NoRealDocument))
        {
            // A stream with no content takes its first member or item here, as an indexer does:
            // "-" and "0" start a sequence, any other token a mapping.
            ReadOnlySpan<byte> first = p[0];
            bool item = first.SequenceEqual("-"u8) || first.SequenceEqual("0"u8);
            return Editor.Replace(s, limits, docRoot, ValueTree.Wrap(item ? null : first.ToArray(), v));
        }

        int cur = Walk(s, limits, document, p, p.Count - 1, out bool viaAlias);
        if (!viaAlias && mode != Mode.Replace && IsEmptyCollection(s, cur, out bool emptySeq))
        {
            // A node with no content and a !!map or !!seq tag is an empty collection, not a null, so it takes its
            // first member or item. Writing the collection in its place keeps the tag, which still fits.
            ReadOnlySpan<byte> first = p[p.Count - 1];
            bool item = first.SequenceEqual("-"u8) || first.SequenceEqual("0"u8);
            if (emptySeq && !item)
            {
                throw Path(s, YamlErrorCategory.PatchPath, "The index is outside the sequence.", cur);
            }

            return Editor.Replace(s, limits, cur, ValueTree.Wrap(emptySeq ? null : first.ToArray(), v));
        }

        var budget = new AliasBudget(s, limits);
        int container = Descend(s, ref budget, cur, ref viaAlias, p, p.Count - 1);
        if (viaAlias)
        {
            throw Path(s, YamlErrorCategory.RefusedAlias, "The edit would write through an alias and change every place that shares the anchored node.", cur);
        }

        return WriteIn(s, limits, container, p[p.Count - 1], v, mode, p);
    }

    // A node written with no content whose tag says it is a mapping or a sequence.
    private static bool IsEmptyCollection(DocState s, int node, out bool sequence)
    {
        sequence = false;
        if (s.Node(node).Kind != NodeKind.Scalar || (s.Node(node).Flags & NodeFlags.Empty) == 0)
        {
            return false;
        }

        TagClass tag = s.TagClassOf(node);
        sequence = tag == TagClass.Seq;
        return tag is TagClass.Map or TagClass.Seq;
    }

    /// <summary>Writes a value into a collection under a token. The caller has followed and refused aliases.</summary>
    public static DocState WriteIn(DocState s, YamlLimits limits, int container, ReadOnlySpan<byte> token, ValueTree v, Mode mode, Pointer context = default)
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
                throw NothingAt(s, context, container);
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
                    throw NothingAt(s, context, container);
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
        if (!viaAlias && mode != Mode.Replace && IsEmptyCollection(s, cur, out _))
        {
            return new Destination(s.PropStart(cur), cur, cur + s.Node(cur).Size, cur);
        }

        var budget = new AliasBudget(s, limits);
        int container = Descend(s, ref budget, cur, ref viaAlias, p, p.Count - 1);
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
        int container = Descend(s, ref budget, cur, ref viaAlias, p, p.Count - 1);
        if (viaAlias)
        {
            throw Path(s, YamlErrorCategory.RefusedAlias, "The edit would write through an alias and change every place that shares the anchored node.", cur);
        }

        int child = FindChild(s, container, p[p.Count - 1], out _);
        if (child < 0)
        {
            throw Missing(s, p, p.Count - 1, container);
        }

        return Editor.Remove(s, limits, child);
    }

    /// <summary>Finds the node a pointer names, following aliases for reading.</summary>
    /// <returns>The node, or -1 when the pointer names nothing.</returns>
    public static int Find(DocState s, YamlLimits limits, int document, in Pointer p) => Find(s, limits, document, p, out _, out _);

    /// <summary>Finds the node a pointer names, and says where the search stopped when there is none.</summary>
    /// <param name="s">The document.</param>
    /// <param name="limits">The limits for alias budgets.</param>
    /// <param name="document">The document index.</param>
    /// <param name="p">The pointer.</param>
    /// <param name="failedAt">When nothing is found, the index of the token that failed.</param>
    /// <param name="failedIn">When nothing is found, the node the token was looked up in.</param>
    /// <returns>The node, or -1 when the pointer names nothing.</returns>
    public static int Find(DocState s, YamlLimits limits, int document, in Pointer p, out int failedAt, out int failedIn)
    {
        failedAt = -1;
        failedIn = -1;
        CheckDocument(s, document);
        int cur = s.Doc(document).Root;
        var budget = new AliasBudget(s, limits);
        for (int i = 0; i < p.Count; i++)
        {
            if (s.Node(cur).Kind == NodeKind.Alias)
            {
                budget.Charge(s, cur, s.Node(cur).Aux);
                cur = s.Node(cur).Aux;
            }

            failedAt = i;
            failedIn = cur;
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

        if (s.Node(cur).Kind == NodeKind.Alias)
        {
            budget.Charge(s, cur, s.Node(cur).Aux);
            cur = s.Node(cur).Aux;
        }

        return cur;
    }

    /// <summary>The error for a pointer that <see cref="Find(DocState, YamlLimits, int, in Pointer, out int, out int)"/> did not resolve.</summary>
    /// <param name="s">The document.</param>
    /// <param name="p">The pointer.</param>
    /// <param name="failedAt">The index of the token that failed.</param>
    /// <param name="failedIn">The node it was looked up in.</param>
    /// <returns>The error.</returns>
    public static YamlException Unresolved(DocState s, in Pointer p, int failedAt, int failedIn)
    {
        if (s.Node(failedIn).Kind == NodeKind.Scalar)
        {
            return Path(s, YamlErrorCategory.PatchPath, $"The pointer '{p.Excerpt(p.Count)}' names nothing: '{p.Excerpt(failedAt)}' is a scalar (an empty value is a null), which has no members or items to address.", failedIn);
        }

        return Missing(s, p, failedAt, failedIn);
    }
}
