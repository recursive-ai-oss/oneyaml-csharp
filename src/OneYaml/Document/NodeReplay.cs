using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using OneYaml.Parsing;
using OneYaml.Schema;

namespace OneYaml.Documents;

/// <summary>
/// Records a node of a document as the events the binder's reader delivers, so that binding a node
/// runs the same code, with the same limits and the same error positions, as binding the text it came from.
/// Aliases are not expanded here: each alias becomes an event that names a segment of the tape holding
/// the anchored node once, and the reader expands it when it is read and charges the budget.
/// </summary>
internal static class NodeReplay
{
    private const long Cap = 1L << 60;

    // What every node stands for once its aliases are followed (nodes and bytes), made when a read first
    // needs it and kept for the state, so many small reads of one document do not each scan all of it.
    // The state holds only a reference; the arrays come from the pool and go back when the state is disposed.
    [ThreadStatic]
    private static int t_sizePasses;

    /// <summary>How many times expansion sizes were worked out on this thread; a test reads it to see that reads share one pass.</summary>
    internal static int SizePassesOnThisThread => t_sizePasses;

    internal sealed class ExpansionSizes
    {
        public long[] Nodes = [];
        public long[] Bytes = [];

        public void Release()
        {
            long[] n = Nodes;
            long[] b = Bytes;
            Nodes = [];
            Bytes = [];
            if (n.Length != 0)
            {
                ArrayPool<long>.Shared.Return(n);
            }

            if (b.Length != 0)
            {
                ArrayPool<long>.Shared.Return(b);
            }
        }
    }

    /// <summary>The expansion sizes of a state, worked out on first use.</summary>
    /// <param name="s">The state.</param>
    /// <returns>The sizes, indexed by node.</returns>
    internal static ExpansionSizes SizesOf(DocState s)
    {
        if (s.ExpansionCache is ExpansionSizes known)
        {
            return known;
        }

        var sizes = new ExpansionSizes();
        try
        {
            ComputeSizes(s, out sizes.Nodes, out sizes.Bytes);
        }
        catch
        {
            sizes.Release();
            throw;
        }

        s.ExpansionCache = sizes;
        return sizes;
    }

    /// <summary>Binds a node with the converters of a type, as the serializer binds the text of that node.</summary>
    /// <typeparam name="T">The type to read.</typeparam>
    /// <param name="s">The document.</param>
    /// <param name="node">The node.</param>
    /// <param name="info">The metadata for <typeparamref name="T"/>.</param>
    /// <returns>The value.</returns>
    public static T? Bind<T>(DocState s, int node, YamlTypeInfo<T> info)
    {
        YamlSerializerOptions options = info.Options;
        options.MakeReadOnly();

        // A plain object target shares the value of an anchor between its aliases, as the serializer does for
        // text, when every anchor the aliases name lies inside the node.
        int count = 0;
        AnchorBook? shared = info.ConverterBase is GenericConverter ? RecordShared(s, node, out count) : null;
        bool share = shared is not null;
        AnchorBook book = shared ?? Record(s, node, out count);
        var reader = new Utf8YamlReader(s.Text, options, book, count, expandAliases: !share);
        try
        {
            reader.BeginReplay();
            T? value = info.Converter.ReadValue(ref reader, options);
            reader.EndReplay();
            return value;
        }
        finally
        {
            reader.Dispose();
        }
    }

    /// <summary>The content span of a scalar node: what the decoder reads.</summary>
    /// <param name="s">The document.</param>
    /// <param name="n">A scalar node.</param>
    /// <param name="start">Where the content starts.</param>
    /// <param name="end">Where it ends.</param>
    public static void ContentOf(DocState s, in CstNode n, out int start, out int end)
    {
        if ((n.Flags & NodeFlags.Empty) != 0)
        {
            start = end = n.Aux;
            return;
        }

        switch ((ScalarStyle)n.Style)
        {
            case ScalarStyle.SingleQuoted:
            case ScalarStyle.DoubleQuoted:
                start = n.Start + 1;
                end = n.End - 1;
                return;
            case ScalarStyle.Literal:
            case ScalarStyle.Folded:
                end = n.End;
                start = Math.Min(DocState.BodyStart(s.Text, n.Start), end);
                return;
            default:
                start = n.Start;
                end = n.End;
                return;
        }
    }

    /// <summary>Records the subtree of a node.</summary>
    /// <param name="s">The document.</param>
    /// <param name="root">The node to record.</param>
    /// <param name="rootCount">The number of events of the node itself, which come first on the tape.</param>
    /// <returns>A book holding the tape and the anchored segments; its tape goes back to the pool when the reader that takes it is disposed.</returns>
    public static AnchorBook Record(DocState s, int root, out int rootCount)
    {
        var book = new AnchorBook();
        book.Tape = ArrayPool<TapeEvent>.Shared.Rent(Math.Max(16, Math.Min(s.Node(root).Size * 2 + 2, 1 << 16)));
        var closes = new CloseStack();
        Dictionary<int, int>? entryOf = null;
        List<int>? targets = null;
        try
        {
            RecordRange(s, book, ref closes, root, ref entryOf, ref targets);
            rootCount = book.TapeCount;
            for (int e = 0; targets is not null && e < targets.Count; e++)
            {
                int t = targets[e];
                book.Entries[e].TapeStart = book.TapeCount;
                RecordRange(s, book, ref closes, t, ref entryOf, ref targets);
                book.Entries[e].TapeEnd = book.TapeCount;
            }

            return book;
        }
        catch
        {
            book.ReturnBuffers();
            throw;
        }
        finally
        {
            closes.Dispose();
        }
    }

    /// <summary>
    /// Records a node with its aliases left as aliases, for a reader that does not expand them: an anchored
    /// node carries its entry, and each alias names the entry of its anchor.
    /// </summary>
    /// <param name="s">The document.</param>
    /// <param name="root">The node to record.</param>
    /// <param name="rootCount">The number of events recorded.</param>
    /// <returns>The book, or <see langword="null"/> when an alias names an anchor outside the node (the caller expands instead).</returns>
    public static AnchorBook? RecordShared(DocState s, int root, out int rootCount)
    {
        rootCount = 0;
        int end = root + s.Node(root).Size;
        bool any = false;
        for (int i = root; i < end; i++)
        {
            ref readonly CstNode n = ref s.Node(i);
            if (n.Kind != NodeKind.Alias)
            {
                continue;
            }

            int target = n.Aux;
            if (target < root || target + s.Node(target).Size > i)
            {
                return null;
            }

            any = true;
        }

        if (!any)
        {
            return null;
        }

        ExpansionSizes sizes = SizesOf(s);
        var book = new AnchorBook();
        book.Tape = ArrayPool<TapeEvent>.Shared.Rent(Math.Max(16, Math.Min(s.Node(root).Size * 2 + 2, 1 << 16)));
        var closes = new CloseStack();
        Dictionary<int, int> entryOf = [];
        try
        {
            for (int i = root; i < end; i++)
            {
                while (closes.Count > 0 && closes.EndOf(closes.Count - 1) <= i)
                {
                    Emit(book, EndEvent(s, closes.Pop()));
                }

                ref readonly CstNode n = ref s.Node(i);
                if (n.Kind == NodeKind.Alias)
                {
                    Emit(book, new TapeEvent { Type = YamlTokenType.Alias, Start = n.Start, End = n.End, Alias = entryOf[n.Aux] });
                    continue;
                }

                TapeEvent e = n.Kind == NodeKind.Scalar
                    ? ScalarEvent(s, i, in n)
                    : new TapeEvent
                    {
                        Type = n.Kind == NodeKind.Mapping ? YamlTokenType.MappingStart : YamlTokenType.SequenceStart,
                        Tag = s.TagClassOf(i),
                        Start = n.Start,
                        End = n.Start,
                    };
                if ((n.Flags & NodeFlags.HasAliases) != 0)
                {
                    int entry = book.Count++;
                    if (entry == book.Entries.Length)
                    {
                        Array.Resize(ref book.Entries, book.Entries.Length * 2);
                    }

                    book.Entries[entry] = new AnchorEntry
                    {
                        TapeStart = book.TapeCount,
                        TapeEnd = book.TapeCount + 1,
                        Nodes = Math.Min(sizes.Nodes[i], Cap),
                        Bytes = Math.Min(sizes.Bytes[i], Cap),
                        IsScalar = n.Kind == NodeKind.Scalar,
                    };
                    entryOf[i] = entry;
                    e.Anchor = entry + 1;
                }

                Emit(book, e);
                if (n.Kind != NodeKind.Scalar)
                {
                    closes.Push(i, i + n.Size);
                }
            }

            while (closes.Count > 0)
            {
                Emit(book, EndEvent(s, closes.Pop()));
            }

            rootCount = book.TapeCount;
            return book;
        }
        catch
        {
            book.ReturnBuffers();
            throw;
        }
        finally
        {
            closes.Dispose();
        }
    }

    private static void RecordRange(
        DocState s,
        AnchorBook book,
        ref CloseStack closes,
        int first,
        ref Dictionary<int, int>? entryOf,
        ref List<int>? targets)
    {
        int end = first + s.Node(first).Size;
        int baseHeight = closes.Count;
        for (int i = first; i < end; i++)
        {
            while (closes.Count > baseHeight && closes.EndOf(closes.Count - 1) <= i)
            {
                Emit(book, EndEvent(s, closes.Pop()));
            }

            ref readonly CstNode n = ref s.Node(i);
            switch (n.Kind)
            {
                case NodeKind.Alias:
                    {
                        int target = n.Aux;
                        if (target >= i || target + s.Node(target).Size > i)
                        {
                            throw YamlException.At(YamlErrorCategory.Syntax, "The alias refers to an anchor that is not defined before it.", s.Text, n.Start);
                        }

                        entryOf ??= [];
                        targets ??= [];
                        if (!entryOf.TryGetValue(target, out int entry))
                        {
                            ExpansionSizes sizes = SizesOf(s);

                            entry = book.Count++;
                            if (entry == book.Entries.Length)
                            {
                                Array.Resize(ref book.Entries, book.Entries.Length * 2);
                            }

                            book.Entries[entry] = new AnchorEntry { Nodes = Math.Min(sizes.Nodes[target], Cap), Bytes = Math.Min(sizes.Bytes[target], Cap) };
                            entryOf[target] = entry;
                            targets.Add(target);
                        }

                        Emit(book, new TapeEvent { Type = YamlTokenType.Alias, Start = n.Start, End = n.End, Alias = entry });
                        break;
                    }

                case NodeKind.Scalar:
                    Emit(book, ScalarEvent(s, i, in n));
                    break;
                default:
                    Emit(book, new TapeEvent
                    {
                        Type = n.Kind == NodeKind.Mapping ? YamlTokenType.MappingStart : YamlTokenType.SequenceStart,
                        Tag = s.TagClassOf(i),
                        Start = n.Start,
                        End = n.Start,
                    });
                    closes.Push(i, i + n.Size);
                    break;
            }
        }

        while (closes.Count > baseHeight)
        {
            Emit(book, EndEvent(s, closes.Pop()));
        }
    }

    private static TapeEvent ScalarEvent(DocState s, int index, in CstNode n)
    {
        ContentOf(s, in n, out int cs, out int ce);
        bool empty = (n.Flags & NodeFlags.Empty) != 0;
        bool block = (ScalarStyle)n.Style is ScalarStyle.Literal or ScalarStyle.Folded;
        int start = empty ? n.Aux : n.Start;
        return new TapeEvent
        {
            Type = YamlTokenType.Scalar,
            Style = (ScalarStyle)n.Style,
            Tag = s.TagClassOf(index),
            Chomping = block ? (Chomping)n.Chomp : default,
            Escaped = (n.Flags & NodeFlags.Escaped) != 0,
            Start = start,
            End = empty ? start : n.End,
            ContentStart = cs,
            ContentEnd = ce,
            BlockIndent = block ? n.Aux : 0,
        };
    }

    private static TapeEvent EndEvent(DocState s, int collection)
    {
        ref readonly CstNode n = ref s.Node(collection);
        return new TapeEvent
        {
            Type = n.Kind == NodeKind.Mapping ? YamlTokenType.MappingEnd : YamlTokenType.SequenceEnd,
            Start = n.Aux,
            End = Math.Max(n.End, n.Aux),
        };
    }

    private static void Emit(AnchorBook book, in TapeEvent e)
    {
        if (book.TapeCount == book.Tape.Length)
        {
            TapeEvent[] bigger = ArrayPool<TapeEvent>.Shared.Rent(book.Tape.Length * 2);
            Array.Copy(book.Tape, bigger, book.TapeCount);
            ArrayPool<TapeEvent>.Shared.Return(book.Tape);
            book.Tape = bigger;
        }

        book.Tape[book.TapeCount++] = e;
    }

    // What every node expands to once its aliases are followed: the node count and the byte count the
    // alias budgets use. Aliases point backwards, so one forward pass that closes subtrees as it goes has
    // the answer for a target by the time an alias to it comes up.
    private static void ComputeSizes(DocState s, out long[] nodes, out long[] bytes)
    {
        t_sizePasses++;
        int count = s.NodeCount;
        nodes = ArrayPool<long>.Shared.Rent(Math.Max(count, 1));
        bytes = ArrayPool<long>.Shared.Rent(Math.Max(count, 1));
        Array.Clear(nodes, 0, count);
        Array.Clear(bytes, 0, count);
        var open = new CloseStack();
        long[] accN = ArrayPool<long>.Shared.Rent(16);
        long[] accB = ArrayPool<long>.Shared.Rent(16);
        try
        {
            for (int i = 0; i < count; i++)
            {
                while (open.Count > 0 && open.EndOf(open.Count - 1) <= i)
                {
                    Close(ref open, nodes, bytes, accN, accB);
                }

                ref readonly CstNode n = ref s.Node(i);
                if (n.Kind is NodeKind.Mapping or NodeKind.Sequence)
                {
                    if (open.Count == accN.Length)
                    {
                        Grow(ref accN);
                        Grow(ref accB);
                    }

                    accN[open.Count] = 1;
                    accB[open.Count] = 1;
                    open.Push(i, i + n.Size);
                    continue;
                }

                long ln;
                long lb;
                if (n.Kind == NodeKind.Alias)
                {
                    int t = n.Aux;
                    if (t >= i || t + s.Node(t).Size > i)
                    {
                        throw YamlException.At(YamlErrorCategory.Syntax, "The alias refers to an anchor that is not defined before it.", s.Text, n.Start);
                    }

                    ln = nodes[t];
                    lb = bytes[t];
                }
                else
                {
                    ContentOf(s, in n, out int cs, out int ce);
                    ln = 1;
                    lb = (ce - cs) + 1L;
                }

                nodes[i] = ln;
                bytes[i] = lb;
                if (open.Count > 0)
                {
                    accN[open.Count - 1] = Math.Min(accN[open.Count - 1] + ln, Cap);
                    accB[open.Count - 1] = Math.Min(accB[open.Count - 1] + lb, Cap);
                }
            }

            while (open.Count > 0)
            {
                Close(ref open, nodes, bytes, accN, accB);
            }
        }
        finally
        {
            open.Dispose();
            ArrayPool<long>.Shared.Return(accN);
            ArrayPool<long>.Shared.Return(accB);
        }
    }

    private static void Close(ref CloseStack open, long[] nodes, long[] bytes, long[] accN, long[] accB)
    {
        int level = open.Count - 1;
        int node = open.Pop();
        nodes[node] = accN[level];
        bytes[node] = accB[level];
        if (level > 0)
        {
            accN[level - 1] = Math.Min(accN[level - 1] + accN[level], Cap);
            accB[level - 1] = Math.Min(accB[level - 1] + accB[level], Cap);
        }
    }

    private static void Grow(ref long[] array)
    {
        long[] bigger = ArrayPool<long>.Shared.Rent(array.Length * 2);
        Array.Copy(array, bigger, array.Length);
        ArrayPool<long>.Shared.Return(array);
        array = bigger;
    }

    // Open collections as pairs of (node, index after its subtree), on a pooled array.
    private struct CloseStack : IDisposable
    {
        private int[] _items;
        private int _count;

        public readonly int Count => _count;

        public void Push(int node, int end)
        {
            _items ??= ArrayPool<int>.Shared.Rent(32);
            if ((_count * 2) + 2 > _items.Length)
            {
                int[] bigger = ArrayPool<int>.Shared.Rent(_items.Length * 2);
                Array.Copy(_items, bigger, _count * 2);
                ArrayPool<int>.Shared.Return(_items);
                _items = bigger;
            }

            _items[_count * 2] = node;
            _items[(_count * 2) + 1] = end;
            _count++;
        }

        public readonly int EndOf(int level) => _items[(level * 2) + 1];

        public int Pop() => _items[--_count * 2];

        public void Dispose()
        {
            if (_items is not null)
            {
                ArrayPool<int>.Shared.Return(_items);
                _items = null!;
            }
        }
    }
}
