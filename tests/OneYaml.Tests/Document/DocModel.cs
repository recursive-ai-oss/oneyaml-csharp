using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using OneYaml.Documents;
using OneYaml.Parsing;
using OneYaml.Schema;
using OneYaml.Text;

namespace OneYaml.Tests.Document;

/// <summary>
/// A plain tree of a YAML stream that carries everything an edit must preserve or change: kinds,
/// styles, tags, anchors, and scalar values resolved under the Core schema. It is built from the
/// reader's events (independently of the document's tree), so it is the oracle for both.
/// </summary>
internal sealed class Model
{
    public string Kind = "scalar";   // scalar, map, seq, alias
    public string Style = "";        // plain, single, double, literal, folded, block, flow
    public string? Tag;
    public string? Resolved;         // the tag with its handle resolved
    public string? Anchor;
    public string Type = "";         // scalar: null, bool, int, float, str
    public string Text = "";         // scalar: normalised value; alias: name
    public string Raw = "";          // scalar: the decoded text as written, which pointers match
    public List<Model> Kids = [];    // map: key, value, key, value...
    public bool Wild;                // an edited node: style, tag and anchor are not compared

    public static Model Str(string s) => new() { Type = "str", Text = s };

    public static Model FromJson(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Null:
                return new Model { Type = "null", Text = "null" };
            case JsonValueKind.True:
                return new Model { Type = "bool", Text = "true" };
            case JsonValueKind.False:
                return new Model { Type = "bool", Text = "false" };
            case JsonValueKind.String:
                return Str(e.GetString()!);
            case JsonValueKind.Number:
                {
                    string raw = e.GetRawText();
                    if (raw.Contains('.') || raw.Contains('e') || raw.Contains('E'))
                    {
                        return new Model { Type = "float", Text = Normalise(double.Parse(raw, CultureInfo.InvariantCulture)) };
                    }

                    return new Model { Type = "int", Text = BigInteger.Parse(raw, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) };
                }

            case JsonValueKind.Array:
                {
                    var m = new Model { Kind = "seq" };
                    foreach (JsonElement item in e.EnumerateArray())
                    {
                        m.Kids.Add(FromJson(item));
                    }

                    return m;
                }

            default:
                {
                    var m = new Model { Kind = "map" };
                    foreach (JsonProperty p in e.EnumerateObject())
                    {
                        m.Kids.Add(Str(p.Name));
                        m.Kids.Add(FromJson(p.Value));
                    }

                    return m;
                }
        }
    }

    public static string Normalise(double d) => double.IsNaN(d) ? ".nan" : d.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Resolves a scalar the way the Core schema does, for comparison.</summary>
    public static (string Type, string Text) Resolve(bool plain, string? rawTag, string? resolvedTag, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        ScalarKind kind;
        if (rawTag is not null)
        {
            CoreTag core = CoreSchema.ParseResolvedTag(Encoding.UTF8.GetBytes(resolvedTag ?? rawTag));
            if (core == CoreTag.Unknown || !CoreSchema.TryResolveTagged(core, bytes, out kind))
            {
                return ("str", value);
            }
        }
        else
        {
            kind = plain ? CoreSchema.ResolvePlain(bytes) : ScalarKind.String;
        }

        switch (kind)
        {
            case ScalarKind.Null:
                return ("null", "null");
            case ScalarKind.Bool:
                _ = CoreSchema.TryParseBool(bytes, out bool b);
                return ("bool", b ? "true" : "false");
            case ScalarKind.Int:
                if (CoreSchema.TryParseInt64(bytes, out long l))
                {
                    return ("int", l.ToString(CultureInfo.InvariantCulture));
                }

                _ = CoreSchema.TryParseBigInteger(bytes, out BigInteger big);
                return ("int", big.ToString(CultureInfo.InvariantCulture));
            case ScalarKind.Float:
                _ = CoreSchema.TryParseDouble(bytes, out double d);
                return ("float", Normalise(d));
            default:
                return ("str", value);
        }
    }

    /// <summary>Builds the models of all documents of a stream from the reader's events.</summary>
    public static List<Model> FromReader(byte[] input)
    {
        using YamlInput text = YamlInput.FromBytes(input, YamlLimits.None);
        using var reader = new YamlReader(text.Utf8Text, new YamlReaderOptions { Limits = YamlLimits.None });
        var docs = new List<Model>();
        var stack = new List<Model>();
        Model? pending = null;
        bool inDoc = false;
        while (reader.Read())
        {
            Model? node = null;
            switch (reader.TokenType)
            {
                case YamlTokenType.DocumentStart:
                    inDoc = true;
                    pending = null;
                    break;
                case YamlTokenType.DocumentEnd:
                    inDoc = false;
                    docs.Add(pending!);
                    break;
                case YamlTokenType.Scalar:
                    {
                        node = new Model();
                        node.Style = reader.ScalarStyle switch
                        {
                            ScalarStyle.Plain => "plain",
                            ScalarStyle.SingleQuoted => "single",
                            ScalarStyle.DoubleQuoted => "double",
                            ScalarStyle.Literal => "literal",
                            _ => "folded",
                        };
                        Props(ref node, in reader);
                        node.Raw = reader.GetString();
                        (node.Type, node.Text) = Resolve(reader.ScalarStyle == ScalarStyle.Plain, node.Tag, node.Resolved, node.Raw);
                        break;
                    }

                case YamlTokenType.Alias:
                    node = new Model { Kind = "alias", Text = Encoding.UTF8.GetString(reader.Anchor) };
                    break;
                case YamlTokenType.MappingStart:
                case YamlTokenType.SequenceStart:
                    node = new Model
                    {
                        Kind = reader.TokenType == YamlTokenType.MappingStart ? "map" : "seq",
                        Style = reader.CollectionStyle == CollectionStyle.Flow ? "flow" : "block",
                    };
                    Props(ref node, in reader);
                    break;
                case YamlTokenType.MappingEnd:
                case YamlTokenType.SequenceEnd:
                    stack.RemoveAt(stack.Count - 1);
                    break;
            }

            if (node is not null && inDoc)
            {
                if (stack.Count > 0)
                {
                    stack[^1].Kids.Add(node);
                }
                else
                {
                    pending = node;
                }

                if (node.Kind is "map" or "seq")
                {
                    stack.Add(node);
                }
            }
        }

        return docs;
    }

    private static void Props(ref Model node, in YamlReader r)
    {
        if (r.HasAnchor)
        {
            node.Anchor = Encoding.UTF8.GetString(r.Anchor);
        }

        if (r.HasTag)
        {
            node.Tag = Encoding.UTF8.GetString(r.RawTag);
            node.Resolved = r.GetResolvedTag();
        }
    }

    private static string? ResolvedOf(DocState s, int node) => s.TagClassOf(node) switch
    {
        TagClass.None => null,
        TagClass.Str => "tag:yaml.org,2002:str",
        TagClass.Int => "tag:yaml.org,2002:int",
        TagClass.Float => "tag:yaml.org,2002:float",
        TagClass.Bool => "tag:yaml.org,2002:bool",
        TagClass.Null => "tag:yaml.org,2002:null",
        _ => "tag:example.com,2000:other",
    };

    /// <summary>Builds the models of all documents from the document tree.</summary>
    public static List<Model> FromState(DocState s)
    {
        var docs = new List<Model>();
        for (int d = 0; d < s.DocCount; d++)
        {
            int root = s.Doc(d).Root;
            var models = new Model[s.Node(root).Size];
            var parents = new Stack<(Model Model, int Remaining)>();
            Model? result = null;
            for (int i = root; i < root + s.Node(root).Size; i++)
            {
                ref readonly CstNode n = ref s.Node(i);
                var m = new Model();
                switch (n.Kind)
                {
                    case NodeKind.Alias:
                        m.Kind = "alias";
                        m.Text = Encoding.UTF8.GetString(s.Text.Slice(n.Start + 1, n.End - n.Start - 1));
                        break;
                    case NodeKind.Scalar:
                        m.Style = (ScalarStyle)n.Style switch
                        {
                            ScalarStyle.Plain => "plain",
                            ScalarStyle.SingleQuoted => "single",
                            ScalarStyle.DoubleQuoted => "double",
                            ScalarStyle.Literal => "literal",
                            _ => "folded",
                        };
                        m.Tag = s.HasTag(i) ? Encoding.UTF8.GetString(s.RawTag(i)) : null;
                        m.Resolved = ResolvedOf(s, i);
                        m.Anchor = s.HasAnchor(i) ? Encoding.UTF8.GetString(s.AnchorName(i)) : null;
                        m.Raw = s.ScalarString(i);
                        (m.Type, m.Text) = Resolve((ScalarStyle)n.Style == ScalarStyle.Plain, m.Tag, m.Resolved, m.Raw);
                        break;
                    default:
                        m.Kind = n.Kind == NodeKind.Mapping ? "map" : "seq";
                        m.Style = n.IsFlow ? "flow" : "block";
                        m.Tag = s.HasTag(i) ? Encoding.UTF8.GetString(s.RawTag(i)) : null;
                        m.Anchor = s.HasAnchor(i) ? Encoding.UTF8.GetString(s.AnchorName(i)) : null;
                        break;
                }

                if (parents.Count == 0)
                {
                    result = m;
                }
                else
                {
                    (Model p, int rem) = parents.Pop();
                    p.Kids.Add(m);
                    if (rem > 1)
                    {
                        parents.Push((p, rem - 1));
                    }
                }

                if (n.Kind is NodeKind.Mapping or NodeKind.Sequence && n.Count > 0)
                {
                    parents.Push((m, n.Count));
                }
            }

            _ = models;
            docs.Add(result!);
        }

        return docs;
    }

    /// <summary>The model as indented text, for failure messages.</summary>
    public string Dump()
    {
        var sb = new StringBuilder();
        var stack = new Stack<(Model M, int Depth)>();
        stack.Push((this, 0));
        while (stack.Count > 0)
        {
            (Model m, int depth) = stack.Pop();
            sb.Append(' ', depth * 2).Append(m.Kind).Append(' ').Append(m.Style).Append(m.Anchor is null ? string.Empty : " &" + m.Anchor).Append(m.Tag is null ? string.Empty : " " + m.Tag).Append(m.Wild ? " wild" : string.Empty);
            sb.Append(m.Kind is "scalar" or "alias" ? " " + m.Type + ":" + m.Text : string.Empty).AppendLine();
            for (int i = m.Kids.Count - 1; i >= 0; i--)
            {
                stack.Push((m.Kids[i], depth + 1));
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// The value a copy writes: the node (aliases at its top followed), without anchors or tags. Returns
    /// null when the value holds an alias, because the document may keep it as an alias or write it out.
    /// </summary>
    public static Model? CopyValue(Model root, string path)
    {
        Model? found = Find(root, path);
        if (found is null)
        {
            return null;
        }

        Model copy = found.Clone();
        var stack = new Stack<Model>();
        stack.Push(copy);
        while (stack.Count > 0)
        {
            Model m = stack.Pop();
            if (m.Kind == "alias")
            {
                return null;
            }

            m.Anchor = null;
            m.Tag = null;
            if (m.Kind == "scalar" && m.Type == "null" && m.Raw.Length == 0)
            {
                // A null left out in the source is written out when it is copied.
                m.Raw = "null";
            }

            foreach (Model k in m.Kids)
            {
                stack.Push(k);
            }
        }

        return copy;
    }

    /// <summary>A deep copy.</summary>
    public Model Clone()
    {
        var m = (Model)MemberwiseClone();
        m.Kids = Kids.Select(k => k.Clone()).ToList();
        return m;
    }

    /// <summary>
    /// The value with every alias replaced by what it stands for, and styles, tags and anchors ignored:
    /// the YAML data model, for comparing a patched document that may have written aliases out.
    /// </summary>
    public static Model Expand(Model root)
    {
        var anchors = new Dictionary<string, Model>();
        return ExpandNode(root, anchors);
    }

    private static Model ExpandNode(Model m, Dictionary<string, Model> anchors)
    {
        if (m.Kind == "alias")
        {
            return anchors[m.Text].Clone();
        }

        Model copy = (Model)m.MemberwiseClone();
        copy.Wild = true;
        copy.Anchor = null;
        copy.Tag = null;
        copy.Kids = m.Kids.Select(k => ExpandNode(k, anchors)).ToList();
        if (m.Anchor is not null)
        {
            anchors[m.Anchor] = copy;
        }

        return copy;
    }

    /// <summary>The node a pointer names in the expected model, aliases followed, or null.</summary>
    public static Model? Find(Model root, string path)
    {
        Model? cur = root;
        string[] tokens = path.Length == 0 ? [] : path[1..].Split('/').Select(t => t.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)).ToArray();
        foreach (string token in tokens)
        {
            cur = Child(Resolve(root, cur!), token);
            if (cur is null)
            {
                return null;
            }
        }

        return Resolve(root, cur!);
    }

    // An alias means the latest anchor of its name that comes before it in document order.
    private static Model Resolve(Model root, Model m)
    {
        while (m.Kind == "alias")
        {
            var anchors = new Dictionary<string, Model>();
            Model? found = null;
            var stack = new Stack<Model>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                Model cur = stack.Pop();
                if (ReferenceEquals(cur, m))
                {
                    found = anchors[m.Text];
                    break;
                }

                if (cur.Anchor is not null)
                {
                    anchors[cur.Anchor] = cur;
                }

                for (int i = cur.Kids.Count - 1; i >= 0; i--)
                {
                    stack.Push(cur.Kids[i]);
                }
            }

            m = found!;
        }

        return m;
    }

    /// <summary>Applies an add, replace or remove with a model value.</summary>
    public static void ApplyModel(Model root, string op, string path, Model value) => Apply(root, op, path, () => value.Clone());

    private static void Become(Model target, Model n)
    {
        target.Kind = n.Kind;
        target.Type = n.Type;
        target.Text = n.Text;
        target.Raw = n.Raw;
        target.Kids = n.Kids;
        target.Style = n.Style;
        target.Wild = true;
    }

    /// <summary>Applies an RFC 7396 merge patch to the expected model.</summary>
    public static void Merge(Model target, JsonElement patch, Model? root = null)
    {
        if (patch.ValueKind != JsonValueKind.Object)
        {
            Become(target, FromJson(patch));
            return;
        }

        // An alias to a mapping already is an object: an empty patch changes nothing, and the alias stays.
        if (target.Kind == "alias" && root is not null && !patch.EnumerateObject().Any() && Resolve(root, target).Kind == "map")
        {
            return;
        }

        if (target.Kind != "map")
        {
            Become(target, new Model { Kind = "map", Style = "block" });
        }

        target.Wild = true;
        var deletes = new List<string>();
        foreach (JsonProperty p in patch.EnumerateObject())
        {
            if (p.Value.ValueKind == JsonValueKind.Null)
            {
                deletes.Add(p.Name);
                continue;
            }

            int at = IndexOfKey(target, p.Name);
            if (at < 0)
            {
                Model key = Str(p.Name);
                key.Raw = p.Name;
                key.Wild = true;
                var fresh = new Model { Kind = "map", Style = "block", Wild = true };
                target.Kids.Add(key);
                target.Kids.Add(fresh);
                at = target.Kids.Count - 2;
                if (p.Value.ValueKind != JsonValueKind.Object)
                {
                    Become(fresh, FromJson(p.Value));
                    continue;
                }
            }

            Merge(target.Kids[at + 1], p.Value, root);
        }

        foreach (string name in deletes)
        {
            int at = IndexOfKey(target, name);
            if (at >= 0)
            {
                target.Kids.RemoveRange(at, 2);
                Emptied(target);
            }
        }
    }

    private static int IndexOfKey(Model map, string name)
    {
        for (int i = 0; i < map.Kids.Count; i += 2)
        {
            if (map.Kids[i].Kind == "scalar" && map.Kids[i].Raw == name)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Applies an add, replace or remove to the expected model.</summary>
    public static void ApplyTo(Model root, string op, string path, JsonElement value) => Apply(root, op, path, () => FromJson(value));

    /// <summary>Applies an add, replace or remove with a CLR value to the expected model.</summary>
    public static void ApplyObject(Model root, string op, string path, object? value) => Apply(root, op, path, () => FromObject(value));

    public static Model FromObject(object? o)
    {
        switch (o)
        {
            case null: return new Model { Type = "null", Text = "null" };
            case bool b: return new Model { Type = "bool", Text = b ? "true" : "false" };
            case long l: return new Model { Type = "int", Text = l.ToString(CultureInfo.InvariantCulture) };
            case int i: return new Model { Type = "int", Text = i.ToString(CultureInfo.InvariantCulture) };
            case double d: return new Model { Type = "float", Text = Normalise(d) };
            case string s: return Str(s);
            case System.Collections.IDictionary dict:
                {
                    var m = new Model { Kind = "map" };
                    foreach (System.Collections.DictionaryEntry e in dict)
                    {
                        Model k = Str((string)e.Key);
                        k.Raw = (string)e.Key;
                        m.Kids.Add(k);
                        m.Kids.Add(FromObject(e.Value));
                    }

                    return m;
                }

            default:
                {
                    var m = new Model { Kind = "seq" };
                    foreach (object? item in (System.Collections.IEnumerable)o)
                    {
                        m.Kids.Add(FromObject(item));
                    }

                    return m;
                }
        }
    }

    private static void Apply(Model root, string op, string path, Func<Model> make)
    {
        string[] tokens = path.Length == 0 ? [] : path[1..].Split('/').Select(t => t.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)).ToArray();
        if (tokens.Length == 0)
        {
            Model n = make();
            root.Kind = n.Kind;
            root.Type = n.Type;
            root.Text = n.Text;
            root.Kids = n.Kids;
            root.Wild = true;
            return;
        }

        Model parent = root;
        for (int i = 0; i < tokens.Length - 1; i++)
        {
            parent = Child(parent, tokens[i])!;
        }

        string last = tokens[^1];
        if (parent.Kind == "map")
        {
            int at = -1;
            for (int i = 0; i < parent.Kids.Count; i += 2)
            {
                if (parent.Kids[i].Kind == "scalar" && parent.Kids[i].Raw == last)
                {
                    at = i;
                }
            }

            if (op == "remove")
            {
                parent.Kids.RemoveRange(at, 2);
                Emptied(parent);
                return;
            }

            Model nv = make();
            nv.Wild = true;
            if (at >= 0)
            {
                nv.Anchor = parent.Kids[at + 1].Anchor;
                parent.Kids[at + 1] = nv;
            }
            else
            {
                Model key = Str(last);
                key.Raw = last;
                key.Wild = true;
                parent.Kids.Add(key);
                parent.Kids.Add(nv);
            }

            return;
        }

        int idx = last == "-" ? parent.Kids.Count : int.Parse(last, CultureInfo.InvariantCulture);
        if (op == "remove")
        {
            parent.Kids.RemoveAt(idx);
            Emptied(parent);
            return;
        }

        Model item = make();
        item.Wild = true;
        if (op == "replace")
        {
            item.Anchor = parent.Kids[idx].Anchor;
            parent.Kids[idx] = item;
        }
        else
        {
            parent.Kids.Insert(idx, item);
        }
    }

    private static void Emptied(Model parent)
    {
        if (parent.Kids.Count == 0 && parent.Style == "block")
        {
            parent.Style = "flow";
        }
    }

    private static Model? Child(Model parent, string token)
    {
        if (parent.Kind == "map")
        {
            for (int i = 0; i < parent.Kids.Count; i += 2)
            {
                if (parent.Kids[i].Kind == "scalar" && parent.Kids[i].Raw == token)
                {
                    return parent.Kids[i + 1];
                }
            }

            return null;
        }

        return parent.Kids[int.Parse(token, CultureInfo.InvariantCulture)];
    }

    /// <summary>Describes the first difference, or returns null when the trees are equal.</summary>
    public static string? Diff(Model expected, Model actual, string path = "")
    {
        // An explicit stack: the oracle must survive the deep inputs too.
        var work = new Stack<(Model E, Model A, string P, bool Wild)>();
        work.Push((expected, actual, path, false));
        while (work.Count > 0)
        {
            (Model e, Model a, string p, bool wild) = work.Pop();
            bool w = wild || e.Wild;
            if (e.Kind != a.Kind)
            {
                return $"{p}: kind {e.Kind} vs {a.Kind}";
            }

            if (!w)
            {
                if (e.Style != a.Style)
                {
                    return $"{p}: style {e.Style} vs {a.Style}";
                }

                if (e.Tag != a.Tag)
                {
                    return $"{p}: tag {e.Tag} vs {a.Tag}";
                }

                if (e.Anchor != a.Anchor)
                {
                    return $"{p}: anchor {e.Anchor} vs {a.Anchor}";
                }
            }

            if (e.Kind is "scalar" or "alias")
            {
                if (e.Type != a.Type || e.Text != a.Text)
                {
                    return $"{p}: value {e.Type}:{e.Text} vs {a.Type}:{a.Text}";
                }

                continue;
            }

            if (e.Kids.Count != a.Kids.Count)
            {
                return $"{p}: {e.Kids.Count} children vs {a.Kids.Count}";
            }

            for (int i = e.Kids.Count - 1; i >= 0; i--)
            {
                string name = e.Kind == "map" ? (i % 2 == 0 ? "key " : "/") + (e.Kids[i & ~1].Text) : "/" + i;
                work.Push((e.Kids[i], a.Kids[i], p + name, w));
            }
        }

        return null;
    }
}
