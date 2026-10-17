using System;
using System.Collections.Generic;
using System.Text;

namespace OneYaml.Documents;

/// <summary>Where a copied or moved value will be written.</summary>
/// <param name="Position">The offset in the text where the new text starts.</param>
/// <param name="ReplacedFirst">The first node the write replaces, or -1 when it only inserts.</param>
/// <param name="ReplacedEnd">Just after the last node the write replaces.</param>
/// <param name="Within">The node the new text lands in or replaces: the collection that receives it, or the replaced node.</param>
internal readonly record struct Destination(int Position, int ReplacedFirst, int ReplacedEnd, int Within);

/// <summary>
/// Decides whether an alias inside a value that is copied or moved can stay an alias at its new place.
/// It can when the anchor it names comes before the destination, still exists afterwards, and is the
/// only definition of that name in the document. A second definition would change what every later
/// alias of that name means, so those aliases are written out instead.
/// </summary>
internal sealed class AliasPolicy
{
    private readonly DocState _state;
    private readonly int _sourceFirst;
    private readonly int _sourceEnd;
    private readonly int _rootFirst;
    private readonly int _rootEnd;
    private readonly Func<Destination> _destination;
    private Destination _dest;
    private bool _haveDest;
    private HashSet<string>? _repeated;

    /// <summary>Creates a policy for a value read from a document.</summary>
    /// <param name="state">The document state the value is read from and written to.</param>
    /// <param name="sourceFirst">The first node of the subtree being copied (its anchors are not carried along).</param>
    /// <param name="sourceEnd">Just after the last node of that subtree.</param>
    /// <param name="document">The document the pointers address.</param>
    /// <param name="destination">Finds where the value goes; called only if an alias needs it.</param>
    public AliasPolicy(DocState state, int sourceFirst, int sourceEnd, int document, Func<Destination> destination)
    {
        _state = state;
        _sourceFirst = sourceFirst;
        _sourceEnd = sourceEnd;
        int root = state.Doc(document).Root;
        _rootFirst = root;
        _rootEnd = root + state.NodeS(root).Size;
        _destination = destination;
    }

    /// <summary>Whether an alias of the anchored node may stay an alias.</summary>
    /// <param name="target">The anchored node.</param>
    /// <returns><see langword="true"/> to write <c>*name</c>; <see langword="false"/> to write the value out.</returns>
    public bool Keeps(int target)
    {
        if (target < _rootFirst || target >= _rootEnd || (target >= _sourceFirst && target < _sourceEnd))
        {
            return false;
        }

        if (!_haveDest)
        {
            _dest = _destination();
            _haveDest = true;
        }

        if (target >= _dest.ReplacedFirst && target < _dest.ReplacedEnd)
        {
            return false;
        }

        if (_state.PropStart(target) >= _dest.Position)
        {
            return false;
        }

        // An anchor takes effect when its node is complete: an alias placed inside the anchored node
        // itself would name an anchor that is not defined yet.
        if (target <= _dest.Within && _dest.Within < target + _state.NodeS(target).Size)
        {
            return false;
        }

        return !IsRepeated(target);
    }

    private bool IsRepeated(int target)
    {
        _repeated ??= FindRepeatedNames();
        return _repeated.Contains(Encoding.UTF8.GetString(_state.AnchorName(target)));
    }

    private HashSet<string> FindRepeatedNames()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var repeated = new HashSet<string>(StringComparer.Ordinal);
        for (int i = _rootFirst; i < _rootEnd; i++)
        {
            if (_state.HasAnchor(i) && !seen.Add(Encoding.UTF8.GetString(_state.AnchorName(i))))
            {
                repeated.Add(Encoding.UTF8.GetString(_state.AnchorName(i)));
            }
        }

        return repeated;
    }
}
