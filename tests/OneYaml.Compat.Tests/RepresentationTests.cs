using System.Collections;
using System.Text;
using Xunit;
using Cm = global::OneYaml.Compat.YamlDotNet.RepresentationModel;
using Rl = global::YamlDotNet.RepresentationModel;

namespace OneYaml.Compat.Tests;

// Loads the same text with the real YamlStream and the compat one and compares what the model holds.
public class RepresentationTests
{
    // The model as text, built by reflection so one walker serves both libraries. A node reached twice prints as a back reference,
    // which also proves that aliases share one node object.
    internal static string Shape(object stream)
    {
        var sb = new StringBuilder();
        var seen = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
        foreach (object doc in (IEnumerable)stream)
        {
            sb.Append("doc[");
            Node(Prop(doc, "RootNode")!, sb, seen, 0);
            sb.Append("]\n");
        }

        return sb.ToString();
    }

    private static object? Prop(object o, string name) => o.GetType().GetProperty(name)!.GetValue(o);

    private static void Node(object node, StringBuilder sb, Dictionary<object, int> seen, int depth)
    {
        if (seen.TryGetValue(node, out int id))
        {
            sb.Append('*').Append(id);
            return;
        }

        id = seen.Count;
        seen[node] = id;
        string type = node.GetType().Name;
        sb.Append(type).Append('#').Append(id).Append('(')
            .Append("a=").Append(Prop(node, "Anchor")).Append(",t=").Append(Prop(node, "Tag"))
            .Append(",s=").Append(Prop(node, "Style")).Append(',');
        switch (type)
        {
            case "YamlScalarNode":
                sb.Append("v=").Append(((string?)Prop(node, "Value"))?.Replace("\n", "\\n", StringComparison.Ordinal) ?? "<null>");
                break;
            case "YamlSequenceNode":
                foreach (object item in (IEnumerable)Prop(node, "Children")!)
                {
                    Node(item, sb, seen, depth + 1);
                    sb.Append(';');
                }

                break;
            default:
                foreach (object pair in (IEnumerable)Prop(node, "Children")!)
                {
                    Node(Prop(pair, "Key")!, sb, seen, depth + 1);
                    sb.Append("=>");
                    Node(Prop(pair, "Value")!, sb, seen, depth + 1);
                    sb.Append(';');
                }

                break;
        }

        sb.Append(')');
    }

    private static (string Real, string Compat) Load(string yaml, bool marks = false)
    {
        var r = new Rl.YamlStream();
        r.Load(new StringReader(yaml));
        var c = new Cm.YamlStream();
        c.Load(new StringReader(yaml));
        if (marks)
        {
            string rm = string.Join(";", r.Documents[0].RootNode.AllNodes.Select(n => $"{n.Start}>{n.End}"));
            string cm = string.Join(";", c.Documents[0].RootNode.AllNodes.Select(n => $"{n.Start}>{n.End}"));
            Assert.Equal(rm, cm);
        }

        return (Shape(r), Shape(c));
    }

    private static string Outcome(Func<string> f)
    {
        try
        {
            return f();
        }
        catch (Exception e)
        {
            return "EX " + Differ.Kind(e);
        }
    }

    public static readonly TheoryData<string> Texts =
    [
        "a: 1",
        "- a\n- b\n- [c, d]\n- {e: f}",
        "a: &x [1, 2]\nb: *x\nc: *x",
        "a: !!str 1\nb: !custom {x: 1}\nc: !<tag:example.com,2000:t> v",
        "a: |\n  one\n  two\nb: >-\n  fold\n  ed\nc: 'single'\nd: \"dou\\tble\"",
        "? [1, 2]\n: seq key\n? {a: b}\n: map key",
        "# c\na: 1 # trailing\n\n\nb: 2\n",
        "---\na: 1\n...\n---\nb: 2\n",
        "--- text\n--- [x]\n",
        "a:\nb: ~\nc: null\nd: ''\n",
        "- - - deep\n  - x\n- y",
        "plain scalar\nwith two lines",
        "a: 1\na: 2",
        "&root {a: *root}",
        "%YAML 1.2\n---\na: 1",
        "",
        "# only a comment\n",
        "k: v\n  bad: indent",
        "a: [1, 2",
        "a: *missing",
        "a: &x 1\nb: &x 2\nc: *x",
    ];

    [Theory]
    [MemberData(nameof(Texts))]
    public void Loads_the_same_model(string yaml)
    {
        string real = Outcome(() => Load(yaml).Real);
        string compat = Outcome(() => Load(yaml).Compat);
        Assert.Equal(real, compat);
    }

    [Fact]
    public void Node_marks_match()
    {
        _ = Load("a: 1\nb:\n  - x\n  - y\n", marks: true);
        _ = Load("[a, b]\n", marks: true);
        _ = Load("\n\n  key: value\n", marks: true);
        _ = Load("é: \U0001F600 x\nü日: [a, \U0001F600]\nlast: 1\r\nmore: 2\n", marks: true);
    }

    [Fact]
    public void Indexers_and_values_match()
    {
        const string yaml = "name: x\nitems:\n  - a\n  - b\nmap: {k: v}\n";
        var r = new Rl.YamlStream();
        r.Load(new StringReader(yaml));
        var c = new Cm.YamlStream();
        c.Load(new StringReader(yaml));
        var rr = (Rl.YamlMappingNode)r.Documents[0].RootNode;
        var cc = (Cm.YamlMappingNode)c.Documents[0].RootNode;
        Assert.Equal(((Rl.YamlScalarNode)rr["name"]).Value, ((Cm.YamlScalarNode)cc["name"]).Value);
        Assert.Equal(((Rl.YamlSequenceNode)rr["items"])[1].ToString(), ((Cm.YamlSequenceNode)cc["items"])[1].ToString());
        Assert.Equal(rr["map"].ToString(), cc["map"].ToString());
        Assert.Equal(rr.AllNodes.Count(), cc.AllNodes.Count());
        Assert.Equal(rr.NodeType.ToString(), cc.NodeType.ToString());
        Assert.Equal(new Rl.YamlScalarNode("a").Equals(new Rl.YamlScalarNode("a")), new Cm.YamlScalarNode("a").Equals(new Cm.YamlScalarNode("a")));
        Assert.Equal(new Rl.YamlScalarNode("a").Equals(new Rl.YamlScalarNode("b")), new Cm.YamlScalarNode("a").Equals(new Cm.YamlScalarNode("b")));
        Assert.Equal(new Rl.YamlScalarNode("a").GetHashCode() == new Rl.YamlScalarNode("a").GetHashCode(), new Cm.YamlScalarNode("a").GetHashCode() == new Cm.YamlScalarNode("a").GetHashCode());
        Assert.Equal(Differ.Kind(Assert.ThrowsAny<Exception>(() => rr["zzz"])), Differ.Kind(Assert.ThrowsAny<Exception>(() => cc["zzz"])));
    }

    [Fact]
    public void Built_models_save_and_reparse_to_the_same_shape()
    {
        var r = new Rl.YamlStream(new Rl.YamlDocument(new Rl.YamlMappingNode
        {
            { "a", "1" },
            { "list", new Rl.YamlSequenceNode("x", "y z", "") },
            { "m", new Rl.YamlMappingNode { { "k", "v" } } },
        }));
        var c = new Cm.YamlStream(new Cm.YamlDocument(new Cm.YamlMappingNode
        {
            { "a", "1" },
            { "list", new Cm.YamlSequenceNode("x", "y z", "") },
            { "m", new Cm.YamlMappingNode { { "k", "v" } } },
        }));
        var rw = new StringWriter();
        r.Save(rw, false);
        var cw = new StringWriter();
        c.Save(cw, false);

        // The text can differ in layout details; what a reader gets back must not.
        var viaCompat = new Rl.YamlStream();
        viaCompat.Load(new StringReader(cw.ToString()));
        var viaReal = new Rl.YamlStream();
        viaReal.Load(new StringReader(rw.ToString()));
        Assert.Equal(Shape(viaReal), Shape(viaCompat));
        Assert.Equal(rw.ToString(), cw.ToString().TrimEnd() + "\n...\n");
    }

    [Fact]
    public void Save_of_an_unedited_stream_keeps_the_text_and_an_edited_one_reparses()
    {
        const string yaml = "# keep me\na: 1   # and me\nb:   [1,   2]\n";
        var c = new Cm.YamlStream();
        c.Load(new StringReader(yaml));
        var w = new StringWriter();
        c.Save(w, false);
        Assert.Equal(yaml, w.ToString());

        ((Cm.YamlMappingNode)c.Documents[0].RootNode).Add("c", "3");
        var w2 = new StringWriter();
        c.Save(w2, false);

        var reparsed = new Rl.YamlStream();
        reparsed.Load(new StringReader(w2.ToString()));
        var expected = new Rl.YamlStream();
        expected.Load(new StringReader("a: 1\nb: [1, 2]\nc: 3\n"));
        Assert.Equal(Shape(expected).Replace("s=Flow", "s=Any", StringComparison.Ordinal), Shape(reparsed).Replace("s=Flow", "s=Any", StringComparison.Ordinal));
    }

    [Fact]
    public void Anchors_are_shared_node_objects()
    {
        var c = new Cm.YamlStream();
        c.Load(new StringReader("a: &x [1]\nb: *x\n"));
        var map = (Cm.YamlMappingNode)c.Documents[0].RootNode;
        Assert.Same(map["a"], map["b"]);
    }
}
