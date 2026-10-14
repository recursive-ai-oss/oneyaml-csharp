using System;

namespace OneYaml.Parsing;

internal ref partial struct YamlReader
{
    /// <summary>
    /// The facts needed to decode the current scalar later from the source text alone: the content
    /// span and, for block scalars, the indentation and chomping.
    /// </summary>
    /// <param name="contentStart">The offset where the scalar's content starts.</param>
    /// <param name="contentEnd">The offset where the scalar's content ends.</param>
    /// <param name="blockIndent">The indentation of a block scalar's content lines.</param>
    /// <param name="chomping">How a block scalar treats its trailing line breaks.</param>
    public readonly void GetScalarDetails(out int contentStart, out int contentEnd, out int blockIndent, out Chomping chomping)
    {
        contentStart = _contentStart;
        contentEnd = _contentEnd;
        blockIndent = _blockIndent;
        chomping = _chomping;
    }
}
