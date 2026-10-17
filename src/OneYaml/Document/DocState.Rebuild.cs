using System;
using System.Collections.Generic;
using OneYaml.Emitting;
using OneYaml.Parsing;
using OneYaml.Schema;

namespace OneYaml.Documents;

/// <summary>One replacement of a span of the old text by new text held in a shared buffer.</summary>
/// <param name="Start">First byte replaced.</param>
/// <param name="End">Just after the last byte replaced.</param>
/// <param name="TextStart">Where the new text starts in the shared buffer.</param>
/// <param name="TextLength">The new text's length.</param>
internal readonly record struct TextEdit(int Start, int End, int TextStart, int TextLength);

internal sealed partial class DocState
{
    /// <summary>
    /// Builds the state that results from replacing spans of this state's text. A state that is not
    /// <see cref="Exclusive"/> is not changed; if the result does not parse, nothing is returned and nothing
    /// leaks. An exclusive state may be updated in place and returned. Only the part of the text around the
    /// edit is parsed when that is safe; otherwise the whole text is.
    /// </summary>
    /// <param name="old">The state to edit.</param>
    /// <param name="edits">Replacements sorted by position and not overlapping.</param>
    /// <param name="texts">The buffer the replacement texts live in.</param>
    /// <param name="limits">The limits for parsing the result.</param>
    /// <returns>The new state.</returns>
    public static DocState Rebuild(DocState old, ReadOnlySpan<TextEdit> edits, ReadOnlySpan<byte> texts, YamlLimits limits)
    {
        // Most edits sit inside one small collection: parse only that and splice the result in.
        DocState? spliced = TrySplice(old, edits, texts, limits, out int reparsed);
        if (spliced is not null)
        {
            spliced.Trace = old.Trace;
            RebuildObserver?.Invoke(spliced, limits, reparsed);
            return spliced;
        }

        DocState full = RebuildAll(old, edits, texts, limits);
        full.Trace = old.Trace;
        RebuildObserver?.Invoke(full, limits, -1);
        return full;
    }

    private static DocState RebuildAll(DocState old, ReadOnlySpan<TextEdit> edits, ReadOnlySpan<byte> texts, YamlLimits limits)
    {
        long length = old.Length;
        foreach (TextEdit e in edits)
        {
            length += e.TextLength - (e.End - e.Start);
        }

        if (length > int.MaxValue - 64)
        {
            throw new YamlException("The document would be larger than 2 GiB.", YamlErrorCategory.Limit, 0, 0, -1);
        }

        byte[] buf = System.Buffers.ArrayPool<byte>.Shared.Rent(Math.Max((int)length, 1));
        int w = 0;
        int r = 0;
        ReadOnlySpan<byte> src = old.Text;
        foreach (TextEdit e in edits)
        {
            src.Slice(r, e.Start - r).CopyTo(buf.AsSpan(w));
            w += e.Start - r;
            texts.Slice(e.TextStart, e.TextLength).CopyTo(buf.AsSpan(w));
            w += e.TextLength;
            r = e.End;
        }

        src.Slice(r).CopyTo(buf.AsSpan(w));
        w += src.Length - r;
        var state = new DocState(buf, w, old.Encoding, old.HasByteOrderMark, old.AllowDuplicateKeys) { EditMode = old.EditMode };
        try
        {
            state.Parse(limits);
            return state;
        }
        catch
        {
            state.Dispose();
            throw;
        }
    }

    /// <summary>The previous sibling of a node, or -1 for a first child or a root.</summary>
    /// <param name="node">A node.</param>
    /// <returns>The sibling's index.</returns>
    public int PrevSibling(int node)
    {
        int parent = N(node).Parent;
        if (parent < 0 || node == parent + 1)
        {
            return -1;
        }

        // The node just before this one in the array is the last of the previous sibling's subtree.
        int c = node - 1;
        while (N(c).Parent != parent)
        {
            c = N(c).Parent;
        }

        return c;
    }
}
