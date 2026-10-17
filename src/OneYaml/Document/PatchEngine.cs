using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OneYaml.Schema;

namespace OneYaml.Documents;

/// <summary>The six operations of RFC 6902.</summary>
internal enum PatchKind : byte
{
    Add,
    Remove,
    Replace,
    Move,
    Copy,
    Test,
}

/// <summary>One validated patch operation. The value is already a flat tree.</summary>
internal sealed class PatchStep
{
    public PatchStep(PatchKind kind, string path, string? from, ValueTree? value)
    {
        Kind = kind;
        Path = path;
        From = from;
        Value = value;
    }

    public PatchKind Kind { get; }

    public string Path { get; }

    public string? From { get; }

    public ValueTree? Value { get; }
}

/// <summary>
/// Applies patches to a document state. Every operation reads a state and returns the next one; the
/// document only takes the last state when all operations worked, so a failure changes nothing.
/// </summary>
internal static class PatchEngine
{
    private static YamlException Invalid(string message, int? operation = null)
        => new(message, YamlErrorCategory.PatchInvalid, 0, 0, -1, operation);

    private static YamlException At(DocState s, YamlErrorCategory category, string message, int node)
        => YamlException.At(category, message, s.Text, node >= 0 ? s.NodeH(node).Start : 0);

    // ---- Reading a patch document ----

    /// <summary>Parses JSON text into a node tree.</summary>
    /// <param name="json">The JSON text.</param>
    /// <param name="limits">The limits; the nesting depth applies.</param>
    /// <returns>The tree, or <see langword="null"/> for the JSON value null.</returns>
    /// <exception cref="YamlException">The text is not valid JSON, or a JSON object repeats a member name.</exception>
    public static JsonNode? ParseJson(string json, YamlLimits limits)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            return JsonNode.Parse(json, null, DocumentOptions(limits));
        }
        catch (JsonException ex)
        {
            throw JsonError(ex);
        }
    }

    /// <summary>Parses UTF-8 JSON into a node tree.</summary>
    /// <param name="json">The JSON bytes.</param>
    /// <param name="limits">The limits; the nesting depth applies.</param>
    /// <returns>The tree, or <see langword="null"/> for the JSON value null.</returns>
    /// <exception cref="YamlException">The bytes are not valid JSON, or a JSON object repeats a member name.</exception>
    public static JsonNode? ParseJson(ReadOnlySpan<byte> json, YamlLimits limits)
    {
        try
        {
            return JsonNode.Parse(json, null, DocumentOptions(limits));
        }
        catch (JsonException ex)
        {
            throw JsonError(ex);
        }
    }

    /// <summary>
    /// Walks a JSON tree once, so that what the node types only report when touched (a repeated member
    /// name, a string with half a surrogate pair) is reported here, as a patch error, and nowhere later.
    /// </summary>
    /// <param name="root">The tree.</param>
    /// <param name="maxDepth">The deepest nesting allowed.</param>
    /// <exception cref="YamlException">The tree repeats a member name, holds a broken string or nests too deeply.</exception>
    public static void CheckJson(JsonNode? root, int maxDepth)
    {
        var work = new Stack<(JsonNode? Node, int Depth)>();
        work.Push((root, 1));
        while (work.Count > 0)
        {
            (JsonNode? node, int depth) = work.Pop();
            if (node is null)
            {
                continue;
            }

            if (depth > maxDepth)
            {
                throw new YamlException("The patch nests deeper than the configured limit.", YamlErrorCategory.Limit, 0, 0, -1);
            }

            try
            {
                switch (node)
                {
                    case JsonObject obj:
                        foreach (KeyValuePair<string, JsonNode?> member in obj)
                        {
                            work.Push((member.Value, depth + 1));
                        }

                        break;
                    case JsonArray array:
                        foreach (JsonNode? item in array)
                        {
                            work.Push((item, depth + 1));
                        }

                        break;
                    case JsonValue value:
                        if (value.TryGetValue(out JsonElement element) && element.ValueKind == JsonValueKind.String)
                        {
                            _ = element.GetString();
                        }

                        break;
                }
            }
            catch (ArgumentException ex)
            {
                // JsonObject refuses a member name it already holds.
                throw new YamlException("The patch has an object with the same member twice.", YamlErrorCategory.PatchInvalid, 0, 0, -1, null, ex);
            }
            catch (InvalidOperationException ex)
            {
                throw new YamlException("The patch has a string that is not valid text.", YamlErrorCategory.PatchInvalid, 0, 0, -1, null, ex);
            }
        }
    }

    private static JsonDocumentOptions DocumentOptions(YamlLimits limits)
        => new() { MaxDepth = limits.MaxDepth < 1 ? 1 : limits.MaxDepth };

    private static YamlException JsonError(JsonException ex)
        => new("The patch is not valid JSON: " + ex.Message, YamlErrorCategory.PatchInvalid, (int)Math.Min((ex.LineNumber ?? -1) + 1, int.MaxValue), (int)Math.Min((ex.BytePositionInLine ?? -1) + 1, int.MaxValue), -1, null, ex);

    /// <summary>Checks a patch document and turns it into operations.</summary>
    /// <param name="patch">A JSON array of RFC 6902 operations.</param>
    /// <param name="limits">The limits; the value nesting depth applies.</param>
    /// <returns>The operations.</returns>
    /// <exception cref="YamlException">The document is not a valid patch (<see cref="YamlErrorCategory.PatchInvalid"/>).</exception>
    public static List<PatchStep> ReadOperations(JsonNode? patch, YamlLimits limits)
    {
        CheckJson(patch, limits.MaxDepth);
        if (patch is not JsonArray array)
        {
            throw Invalid("A patch is a JSON array of operations.");
        }

        var steps = new List<PatchStep>(array.Count);
        int index = 0;
        foreach (JsonNode? item in array)
        {
            try
            {
                steps.Add(ReadOperation(item, limits));
            }
            catch (YamlException ex) when (ex.OperationIndex is null)
            {
                throw new YamlException(ex.Message, ex.Category, ex.Line, ex.Column, ex.Offset, index, ex) { IsAliasBudget = ex.IsAliasBudget };
            }

            index++;
        }

        return steps;
    }

    private static PatchStep ReadOperation(JsonNode? item, YamlLimits limits)
    {
        if (item is not JsonObject op)
        {
            throw Invalid("An operation is a JSON object.");
        }

        string name = RequiredString(op, "op");
        string path = RequiredString(op, "path");
        PatchKind kind;
        switch (name)
        {
            case "add":
                kind = PatchKind.Add;
                break;
            case "remove":
                kind = PatchKind.Remove;
                break;
            case "replace":
                kind = PatchKind.Replace;
                break;
            case "move":
                kind = PatchKind.Move;
                break;
            case "copy":
                kind = PatchKind.Copy;
                break;
            case "test":
                kind = PatchKind.Test;
                break;
            default:
                throw Invalid("The operation '" + name + "' is not one of add, remove, replace, move, copy and test.");
        }

        string? from = kind is PatchKind.Move or PatchKind.Copy ? RequiredString(op, "from") : null;
        ValueTree? value = null;
        if (kind is PatchKind.Add or PatchKind.Replace or PatchKind.Test)
        {
            if (!op.TryGetPropertyValue("value", out JsonNode? v))
            {
                throw Invalid("The operation '" + name + "' needs a \"value\".");
            }

            value = ValueTree.FromJsonNode(v, limits.MaxDepth, maxIntegerDigits: limits.MaxIntegerDigits);
        }

        return new PatchStep(kind, path, from, value);
    }

    private static string RequiredString(JsonObject op, string member)
    {
        if (op.TryGetPropertyValue(member, out JsonNode? n) && n is JsonValue v && v.TryGetValue(out string? s))
        {
            return s;
        }

        throw Invalid("The operation needs a string \"" + member + "\".");
    }

    // ---- RFC 6902 ----

    /// <summary>Applies operations in order to one document.</summary>
    /// <param name="original">The state to start from. It is not changed or disposed.</param>
    /// <param name="limits">The limits.</param>
    /// <param name="document">The document index.</param>
    /// <param name="steps">The operations.</param>
    /// <returns>The state after the last operation, or <paramref name="original"/> when nothing changed.</returns>
    /// <exception cref="YamlException">An operation failed; <see cref="YamlException.OperationIndex"/> says which.</exception>
    public static DocState Apply(DocState original, YamlLimits limits, int document, IReadOnlyList<PatchStep> steps)
    {
        PointerOps.CheckDocument(original, document);
        DocState cur = original;
        var budget = new AliasBudget(original, limits);
        int i = 0;
        bool done = false;
        try
        {
            for (; i < steps.Count; i++)
            {
                DocState next = Run(cur, limits, document, steps[i], ref budget);
                if (!ReferenceEquals(next, cur))
                {
                    // Only the first state belongs to the document; the ones made on the way are ours.
                    if (!ReferenceEquals(cur, original))
                    {
                        cur.Dispose();
                    }

                    cur = next;

                    // From here on the state is ours alone, so the next operation may edit it in place.
                    cur.Exclusive = true;
                }
            }

            done = true;
            cur.Exclusive = false;
            return cur;
        }
        catch (YamlException ex) when (ex.OperationIndex is null)
        {
            throw new YamlException(ex.Message, ex.Category, ex.Line, ex.Column, ex.Offset, i, ex) { IsAliasBudget = ex.IsAliasBudget };
        }
        finally
        {
            if (!done && !ReferenceEquals(cur, original))
            {
                cur.Dispose();
            }
        }
    }

    private static DocState Run(DocState s, YamlLimits limits, int document, PatchStep step, ref AliasBudget budget)
    {
        Pointer path = Pointer.Parse(step.Path);
        switch (step.Kind)
        {
            case PatchKind.Add:
                return PointerOps.Write(s, limits, document, path, step.Value!, PointerOps.Mode.Add);
            case PatchKind.Replace:
                return PointerOps.Write(s, limits, document, path, step.Value!, PointerOps.Mode.Replace);
            case PatchKind.Remove:
                return PointerOps.Remove(s, limits, document, path);
            case PatchKind.Test:
                Test(s, limits, document, path, step.Value!, ref budget);
                return s;
            case PatchKind.Copy:
                return Copy(s, limits, document, Pointer.Parse(step.From!), path, remove: false, ref budget);
            default:
                return Copy(s, limits, document, Pointer.Parse(step.From!), path, remove: true, ref budget);
        }
    }

    private static void Test(DocState s, YamlLimits limits, int document, in Pointer path, ValueTree expected, ref AliasBudget budget)
    {
        int node = PointerOps.Find(s, limits, document, path, out int failedAt, out int failedIn);
        if (node < 0)
        {
            throw PointerOps.Unresolved(s, path, failedAt, failedIn);
        }

        if (!DataEquals(s, node, expected, ref budget))
        {
            throw At(s, YamlErrorCategory.PatchTest, $"The value at '{path}' is not the value the test expects.", node);
        }
    }

    // Copy and move: read the value, then write it at its place (and remove the source).
    private static DocState Copy(DocState s, YamlLimits limits, int document, in Pointer from, in Pointer to, bool remove, ref AliasBudget budget)
    {
        PointerOps.CheckDocument(s, document);
        int located = PointerOps.Walk(s, limits, document, from, from.Count, out _);
        if (remove)
        {
            if (to.Count > from.Count && to.StartsWith(from, from.Count))
            {
                throw At(s, YamlErrorCategory.PatchInvalid, "A value cannot be moved into itself.", located);
            }

            if (to.Count == from.Count && to.StartsWith(from, from.Count))
            {
                return s;
            }
        }

        int source = located;
        if (s.NodeS(source).Kind == NodeKind.Alias)
        {
            // Reading follows the alias: the value that moves or is copied is what the alias stands for.
            budget.Charge(s, source, s.NodeH(source).Aux);
            source = s.NodeH(source).Aux;
        }

        Pointer dest = to;
        var policy = new AliasPolicy(s, source, source + s.NodeS(source).Size, document, () => PointerOps.Locate(s, limits, document, dest, PointerOps.Mode.Add));
        var value = new ValueTree();
        // The copy reads nodes as they are, so nothing may be held back.
        s.SettleNodes();
        value.AppendFrom(s, source, ref budget, policy, chargeCopies: !remove);
        if (!remove)
        {
            return PointerOps.Write(s, limits, document, to, value, PointerOps.Mode.Add);
        }

        // Writing first keeps a mapping from collapsing to {} for a moment, which gives the smaller diff.
        // That is only possible when both sides are mappings and the destination is not above the source.
        bool writeFirst = WritesFirst(s, limits, document, located, from, to);
        DocState middle = writeFirst
            ? PointerOps.Write(s, limits, document, to, value, PointerOps.Mode.Add)
            : PointerOps.Remove(s, limits, document, from);
        try
        {
            DocState last = writeFirst
                ? PointerOps.Remove(middle, limits, document, from)
                : PointerOps.Write(middle, limits, document, to, value, PointerOps.Mode.Add);
            if (!ReferenceEquals(middle, s) && !ReferenceEquals(middle, last))
            {
                middle.Dispose();
            }

            return last;
        }
        catch
        {
            // The state made on the way is ours to return to the pool; the caller never sees it.
            if (!ReferenceEquals(middle, s))
            {
                middle.Dispose();
            }

            throw;
        }
    }

    private static bool WritesFirst(DocState s, YamlLimits limits, int document, int located, in Pointer from, in Pointer to)
    {
        if (to.Count == 0 || from.Count == 0 || s.NodeS(located).Parent < 0 || s.NodeS(s.NodeH(located).Parent).Kind != NodeKind.Mapping)
        {
            return false;
        }

        // The destination is above the source when its pointer is a prefix of the source's.
        if (to.Count <= from.Count && from.StartsWith(to, to.Count))
        {
            return false;
        }

        int parent = PointerOps.Walk(s, limits, document, to, to.Count - 1, out _);
        var budget = new AliasBudget(s, limits);
        bool via = false;
        int container = PointerOps.Descend(s, ref budget, parent, ref via);
        return s.NodeS(container).Kind == NodeKind.Mapping;
    }

    // ---- Comparing by the data model ----

    private static bool DataEquals(DocState s, int node, ValueTree v, ref AliasBudget budget)
    {
        var work = new Stack<(int Node, int Value)>();
        work.Push((node, 0));
        while (work.Count > 0)
        {
            (int d, int vi) = work.Pop();
            if (s.NodeS(d).Kind == NodeKind.Alias)
            {
                budget.Charge(s, d, s.NodeH(d).Aux);
                d = s.NodeH(d).Aux;
            }

            ref readonly VNode vn = ref v[vi];
            ref readonly CstNode dn = ref s.NodeH(d);
            switch (vn.Kind)
            {
                case VKind.Scalar:
                    if (dn.Kind != NodeKind.Scalar || !ScalarEquals(s, d, vn))
                    {
                        return false;
                    }

                    break;
                case VKind.Sequence:
                    {
                        if (dn.Kind != NodeKind.Sequence || dn.Count != vn.Count)
                        {
                            return false;
                        }

                        int dc = d + 1;
                        int vc = vi + 1;
                        for (int i = 0; i < vn.Count; i++)
                        {
                            work.Push((dc, vc));
                            dc += s.NodeS(dc).Size;
                            vc += v[vc].Size;
                        }

                        break;
                    }

                case VKind.Mapping:
                    {
                        if (dn.Kind != NodeKind.Mapping || dn.Count != vn.Count)
                        {
                            return false;
                        }

                        int vc = vi + 1;
                        for (int i = 0; i < vn.Count; i += 2)
                        {
                            int match = FindEqualKey(s, d, v[vc]);
                            if (match < 0)
                            {
                                return false;
                            }

                            vc += v[vc].Size;
                            work.Push((match, vc));
                            vc += v[vc].Size;
                        }

                        break;
                    }

                default:
                    return false;
            }
        }

        return true;
    }

    // The value of the key of a mapping that equals the given scalar key, or -1.
    private static int FindEqualKey(DocState s, int map, in VNode key)
    {
        if (key.Kind != VKind.Scalar)
        {
            return -1;
        }

        int found = -1;
        int k = map + 1;
        for (int i = 0; i < s.NodeS(map).Count; i += 2)
        {
            int val = k + s.NodeS(k).Size;
            if (s.NodeS(k).Kind == NodeKind.Scalar && ScalarEquals(s, k, key))
            {
                if (found >= 0)
                {
                    throw At(s, YamlErrorCategory.RefusedAmbiguous, "More than one key matches this key of the test value.", k);
                }

                found = val;
            }

            k = val + s.NodeS(val).Size;
        }

        return found;
    }

    private static bool ScalarEquals(DocState s, int node, in VNode expected)
    {
        ScalarKind kind = s.KindOf(node, out bool valid);
        if (!valid || kind != expected.Scalar)
        {
            return false;
        }

        if (kind == ScalarKind.Null)
        {
            return true;
        }

        ReadOnlySpan<byte> text = s.DecodedScalar(node, out byte[]? rented);
        try
        {
            return DocState.SameValue(kind, text, expected.Text, s.MaxIntegerDigits);
        }
        finally
        {
            DocState.ReleaseDecoded(rented);
        }
    }

    // ---- RFC 7396 ----

    private sealed class MergeFrame
    {
        public List<KeyValuePair<string, JsonNode?>> Members = [];
        public List<string> Deletions = [];
        public int Next;
        public bool PopToken;
    }

    /// <summary>Applies a merge patch to one document.</summary>
    /// <param name="original">The state to start from. It is not changed or disposed.</param>
    /// <param name="limits">The limits.</param>
    /// <param name="document">The document index.</param>
    /// <param name="patch">The patch: any JSON value. An object merges; anything else replaces.</param>
    /// <returns>The new state, or <paramref name="original"/> when nothing changed.</returns>
    /// <exception cref="YamlException">The patch cannot be applied; the document is unchanged.</exception>
    public static DocState ApplyMerge(DocState original, YamlLimits limits, int document, JsonNode? patch)
    {
        PointerOps.CheckDocument(original, document);
        CheckJson(patch, limits.MaxDepth);
        DocState cur = original;
        bool done = false;
        try
        {
            var tokens = new List<byte[]>();
            var frames = new List<MergeFrame>();
            Enter(ref cur, original, limits, document, tokens, frames, patch, popToken: false);
            while (frames.Count > 0)
            {
                MergeFrame f = frames[^1];
                if (f.Next < f.Members.Count)
                {
                    KeyValuePair<string, JsonNode?> member = f.Members[f.Next++];
                    tokens.Add(Utf8Strict(member.Key));
                    Enter(ref cur, original, limits, document, tokens, frames, member.Value, popToken: true);
                    continue;
                }

                if (!DeleteMany(ref cur, original, limits, document, tokens, f.Deletions, out List<byte[]>? encoded))
                {
                    for (int i = 0; i < f.Deletions.Count; i++)
                    {
                        tokens.Add(encoded is null ? Utf8Strict(f.Deletions[i]) : encoded[i]);
                        Swap(ref cur, original, DeleteMember(cur, limits, document, tokens));
                        tokens.RemoveAt(tokens.Count - 1);
                    }
                }

                frames.RemoveAt(frames.Count - 1);
                if (f.PopToken)
                {
                    tokens.RemoveAt(tokens.Count - 1);
                }
            }

            done = true;
            cur.Exclusive = false;
            return cur;
        }
        finally
        {
            if (!done && !ReferenceEquals(cur, original))
            {
                cur.Dispose();
            }
        }
    }

    private static byte[] Utf8Strict(string text)
    {
        try
        {
            return new UTF8Encoding(false, true).GetBytes(text);
        }
        catch (EncoderFallbackException)
        {
            throw Invalid("A member name contains an unpaired surrogate.");
        }
    }

    private static void Swap(ref DocState cur, DocState original, DocState next)
    {
        if (!ReferenceEquals(next, cur))
        {
            if (!ReferenceEquals(cur, original))
            {
                cur.Dispose();
            }

            cur = next;
            cur.Exclusive = true;
        }
    }

    // Where the merge stands at the path in the tokens: the node there, or -1. Aliases are not followed.
    private static int Locate(DocState s, int document, List<byte[]> tokens)
    {
        int cur = s.Doc(document).Root;
        foreach (byte[] token in tokens)
        {
            if (s.NodeS(cur).Kind != NodeKind.Mapping)
            {
                return -1;
            }

            cur = PointerOps.FindChild(s, cur, token, out _);
            if (cur < 0)
            {
                return -1;
            }
        }

        return cur;
    }

    private static void Enter(ref DocState cur, DocState original, YamlLimits limits, int document, List<byte[]> tokens, List<MergeFrame> frames, JsonNode? patch, bool popToken)
    {
        int target = Locate(cur, document, tokens);
        if (patch is JsonObject obj && target >= 0 && cur.NodeS(target).Kind == NodeKind.Mapping)
        {
            var frame = new MergeFrame { PopToken = popToken };
            foreach (KeyValuePair<string, JsonNode?> member in obj)
            {
                if (member.Value is null || (member.Value is JsonValue jv && jv.GetValueKind() == JsonValueKind.Null))
                {
                    frame.Deletions.Add(member.Key);
                }
                else
                {
                    frame.Members.Add(member);
                }
            }

            frames.Add(frame);
            return;
        }

        if (patch is JsonObject && target >= 0 && cur.NodeS(target).Kind == NodeKind.Alias)
        {
            // Merging into an alias would change every place that shares the anchored node.
            int resolved = cur.NodeH(target).Aux;
            if (cur.NodeS(resolved).Kind == NodeKind.Mapping && ((JsonObject)patch).Count == 0)
            {
                Finish(tokens, popToken);
                return;
            }

            if (cur.NodeS(resolved).Kind == NodeKind.Mapping)
            {
                throw At(cur, YamlErrorCategory.RefusedAlias, "The patch would merge into an alias and change every place that shares the anchored node.", target);
            }
        }

        // Anything else replaces what is there (or fills in what is missing), without the null members.
        ValueTree value = ValueTree.FromJsonNode(patch, limits.MaxDepth, dropNulls: true, maxIntegerDigits: limits.MaxIntegerDigits);
        Pointer p = Pointer.FromTokens(tokens);
        Swap(ref cur, original, PointerOps.Write(cur, limits, document, p, value, PointerOps.Mode.Add));
        Finish(tokens, popToken);
    }

    private static void Finish(List<byte[]> tokens, bool popToken)
    {
        if (popToken)
        {
            tokens.RemoveAt(tokens.Count - 1);
        }
    }

    // Many null members of one mapping: one pass over the mapping and one rebuild, when the plain block case applies.
    private static bool DeleteMany(ref DocState cur, DocState original, YamlLimits limits, int document, List<byte[]> tokens, List<string> names, out List<byte[]>? keys)
    {
        keys = null;
        if (names.Count < 2)
        {
            return false;
        }

        int map = Locate(cur, document, tokens);
        if (map < 0 || cur.NodeS(map).Kind != NodeKind.Mapping)
        {
            return false;
        }

        keys = new List<byte[]>(names.Count);
        foreach (string name in names)
        {
            keys.Add(Utf8Strict(name));
        }

        if (!Editor.TryRemoveMany(cur, limits, map, keys, out DocState next))
        {
            return false;
        }

        Swap(ref cur, original, next);
        return true;
    }

    private static DocState DeleteMember(DocState s, YamlLimits limits, int document, List<byte[]> tokens)
    {
        // Deleting what is not there is not an error.
        int cur = s.Doc(document).Root;
        for (int i = 0; i < tokens.Count; i++)
        {
            if (s.NodeS(cur).Kind != NodeKind.Mapping)
            {
                return s;
            }

            cur = PointerOps.FindChild(s, cur, tokens[i], out _);
            if (cur < 0)
            {
                return s;
            }
        }

        return PointerOps.Remove(s, limits, document, Pointer.FromTokens(tokens));
    }
}
