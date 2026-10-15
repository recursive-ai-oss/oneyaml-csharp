using System;
using System.Runtime.InteropServices;

namespace OneYaml.Documents;

/// <summary>The structural kind of a node in the flat tree.</summary>
internal enum NodeKind : byte
{
    Scalar,
    Mapping,
    Sequence,
    Alias,
}

/// <summary>Facts about a node that the edit engine and the readers need.</summary>
[Flags]
internal enum NodeFlags : byte
{
    None = 0,

    /// <summary>A flow collection (<c>[ ]</c>, <c>{ }</c>, or a single pair inside a flow sequence).</summary>
    Flow = 1,

    /// <summary>The scalar's value differs from its raw source text (escapes, folding, doubled quotes).</summary>
    Escaped = 2,

    /// <summary>The anchor of this node is used by at least one alias.</summary>
    HasAliases = 4,

    /// <summary>The node is a key of a mapping.</summary>
    IsKey = 8,

    /// <summary>The scalar has no text in the source: a null that was left out.</summary>
    Empty = 16,

    /// <summary>A mapping written without braces inside a flow sequence: <c>[a: 1]</c>.</summary>
    Pair = 32,

    /// <summary>A mapping value left out entirely, with not even a colon: <c>{a, b: 1}</c>, <c>? a</c>.</summary>
    Bare = 64,
}

/// <summary>
/// One node of the lossless tree. Nodes are stored in document order, so the first child of a
/// collection is the next node and the next sibling of a node is found by skipping its subtree.
/// A mapping's children alternate key, value.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CstNode
{
    /// <summary>First byte of the node's own text, after any anchor or tag. For an empty scalar: where text would go.</summary>
    public int Start;

    /// <summary>
    /// Just after the node's own text. For a block collection: the end of its last entry. For a
    /// block scalar: the end of its last line of text, plus trailing blank lines when they are part of the value.
    /// </summary>
    public int End;

    /// <summary>The index of the parent collection, or -1 for a document's root.</summary>
    public int Parent;

    /// <summary>The number of nodes in the subtree, including this one.</summary>
    public int Size;

    /// <summary>The number of children (a mapping counts keys and values separately).</summary>
    public int Count;

    /// <summary>For an alias: the node it refers to. For a block scalar: the column of its content lines.</summary>
    public int Aux;

    /// <summary>The row of the property table holding the anchor and tag spans, or -1.</summary>
    public int Props;

    public NodeKind Kind;

    /// <summary>For a scalar, a <see cref="Parsing.ScalarStyle"/>.</summary>
    public byte Style;

    public NodeFlags Flags;

    /// <summary>For a block scalar, a <see cref="Parsing.Chomping"/>.</summary>
    public byte Chomp;

    public readonly bool IsFlow => (Flags & NodeFlags.Flow) != 0;
}

/// <summary>Where a document sits in the stream.</summary>
internal struct DocInfo
{
    /// <summary>The index of the root node.</summary>
    public int Root;

    /// <summary>Start of the <c>---</c> marker, or of the document when the marker is absent.</summary>
    public int StartPos;

    /// <summary>End of the <c>---</c> marker (equal to <see cref="StartPos"/> when absent).</summary>
    public int StartEnd;

    public bool ExplicitStart;

    /// <summary>Start of the <c>...</c> marker or of whatever ends the document.</summary>
    public int EndPos;
}
