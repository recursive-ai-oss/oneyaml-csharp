using System.Runtime.CompilerServices;
using System.Text;
using OneYaml.Documents;

namespace OneYaml.Tests.Document;

/// <summary>
/// Compares two document states field by field: text, every node, property, comment and document.
/// An edit that updates a state in place must give exactly what a fresh parse of the same bytes gives.
/// </summary>
internal static class StateEquality
{
    /// <summary>Describes the first difference, or returns null when the states are identical.</summary>
    internal static string? Diff(DocState a, DocState b)
    {
        if (!a.Text.SequenceEqual(b.Text))
        {
            return "text differs";
        }

        if (a.Encoding != b.Encoding || a.HasByteOrderMark != b.HasByteOrderMark || a.AllowDuplicateKeys != b.AllowDuplicateKeys)
        {
            return "encoding, byte order mark or duplicate-key flag differs";
        }

        if (a.NodeCount != b.NodeCount)
        {
            return $"node count {a.NodeCount} vs {b.NodeCount}";
        }

        if (a.DocCount != b.DocCount || a.NoRealDocument != b.NoRealDocument)
        {
            return $"document count {a.DocCount} vs {b.DocCount}";
        }

        if (a.CommentCount != b.CommentCount || a.AliasCount != b.AliasCount)
        {
            return $"comment count {a.CommentCount} vs {b.CommentCount}, alias count {a.AliasCount} vs {b.AliasCount}";
        }

        for (int i = 0; i < a.NodeCount; i++)
        {
            ref readonly CstNode x = ref a.Node(i);
            ref readonly CstNode y = ref b.Node(i);
            if (x.Start != y.Start || x.End != y.End || x.Parent != y.Parent || x.Size != y.Size || x.Count != y.Count
                || x.Aux != y.Aux || x.Kind != y.Kind || x.Style != y.Style || x.Flags != y.Flags || x.Chomp != y.Chomp
                || (x.Props >= 0) != (y.Props >= 0))
            {
                return $"node {i}: {Describe(x)} vs {Describe(y)}";
            }

            if (a.HasAnchor(i) != b.HasAnchor(i) || a.HasTag(i) != b.HasTag(i)
                || !a.AnchorName(i).SequenceEqual(b.AnchorName(i)) || !a.RawTag(i).SequenceEqual(b.RawTag(i))
                || a.PropStart(i) != b.PropStart(i) || a.PropEnd(i) != b.PropEnd(i) || a.TagClassOf(i) != b.TagClassOf(i))
            {
                return $"node {i}: properties differ (anchor {Encoding.UTF8.GetString(a.AnchorName(i))}/{Encoding.UTF8.GetString(b.AnchorName(i))}, start {a.PropStart(i)}/{b.PropStart(i)}, end {a.PropEnd(i)}/{b.PropEnd(i)})";
            }
        }

        for (int d = 0; d < a.DocCount; d++)
        {
            ref readonly DocInfo x = ref a.Doc(d);
            ref readonly DocInfo y = ref b.Doc(d);
            if (x.Root != y.Root || x.StartPos != y.StartPos || x.StartEnd != y.StartEnd || x.ExplicitStart != y.ExplicitStart || x.EndPos != y.EndPos)
            {
                return $"document {d}: root {x.Root}/{y.Root}, start {x.StartPos}/{y.StartPos}, startEnd {x.StartEnd}/{y.StartEnd}, end {x.EndPos}/{y.EndPos}";
            }
        }

        for (int c = 0; c < a.CommentCount; c++)
        {
            if (a.Comment(c) != b.Comment(c))
            {
                return $"comment {c}: {a.Comment(c)} vs {b.Comment(c)}";
            }
        }

        return null;
    }

    private static string Describe(in CstNode n)
        => $"{n.Kind}/{n.Flags} [{n.Start},{n.End}) parent {n.Parent} size {n.Size} count {n.Count} aux {n.Aux} style {n.Style} chomp {n.Chomp} props {n.Props >= 0}";
}

/// <summary>
/// Installs a check on every state an edit produces, for the whole test run: the state is compared with a
/// fresh parse of its own bytes. It also counts how many edits parsed only part of the text.
/// </summary>
internal static class SpliceCheck
{
    private static long _spliced;
    private static long _full;
    private static long _splicedBytes;
    private static long _fullBytes;

    // The same counts for the calling thread, which a test resets and reads without other tests getting in.
    [ThreadStatic]
    private static long _threadSpliced;

    [ThreadStatic]
    private static long _threadFull;

    [ThreadStatic]
    private static long _threadBytes;

    [ThreadStatic]
    private static long _threadLarge;

    internal static long ThreadSpliced => _threadSpliced;

    internal static long ThreadFull => _threadFull;

    /// <summary>Bytes parsed by incremental edits on this thread.</summary>
    internal static long ThreadBytes => _threadBytes;

    internal static void ResetThread() => _threadSpliced = _threadFull = _threadBytes = 0;

    internal static long Spliced => Interlocked.Read(ref _spliced);

    internal static long Full => Interlocked.Read(ref _full);

    internal static long SplicedBytes => Interlocked.Read(ref _splicedBytes);

    internal static long FullBytes => Interlocked.Read(ref _fullBytes);

#pragma warning disable CA2255 // The check must be in place before any test runs, whichever one is first.
    [ModuleInitializer]
    internal static void Install()
    {
        DocState.RebuildObserver = Observe;

        // Every remembered layout and key index is held to a fresh computation while the tests run.
        DocState.VerifyCaches = true;
        DocState.IndexMinNodes = 8;
        DocState.LayoutMinSteps = 0;
    }
#pragma warning restore CA2255

    private static void Observe(DocState state, YamlLimits limits, int reparsed)
    {
        if (reparsed >= 0)
        {
            _threadSpliced++;
            _threadBytes += reparsed;
            Interlocked.Increment(ref _spliced);
            Interlocked.Add(ref _splicedBytes, reparsed);
        }
        else
        {
            _threadFull++;
            Interlocked.Increment(ref _full);
            Interlocked.Add(ref _fullBytes, state.Length);
            return;
        }

        // Parsing the whole text again for every edit would make a long script quadratic, and would skew the
        // time-bounded cases; large states are checked one time in a thousand.
        if (state.Length > 16384 && ++_threadLarge % 1000 != 0)
        {
            return;
        }

        DocState fresh;
        try
        {
            fresh = DocState.Build(state.Text, state.Encoding, state.HasByteOrderMark, limits, state.AllowDuplicateKeys);
        }
        catch (YamlException ex)
        {
            throw new InvalidOperationException("An incremental edit gave a state whose bytes do not parse: " + ex.Message, ex);
        }

        try
        {
            string? diff = StateEquality.Diff(state, fresh);
            if (diff is not null)
            {
                throw new InvalidOperationException("An incremental edit differs from a fresh parse of the same bytes: " + diff + "\n" + Encoding.UTF8.GetString(state.Text));
            }
        }
        finally
        {
            fresh.Dispose();
        }
    }
}
