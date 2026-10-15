using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using OneYaml.Compat.YamlDotNet.Core;
using OneYaml.Compat.YamlDotNet.Core.Events;
using OneYaml.Compat.YamlDotNet.Internal;
using OneYaml.Compat.YamlDotNet.Serialization;
using OneYaml.Documents;
using Oy = global::OneYaml;

namespace OneYaml.Compat.YamlDotNet.RepresentationModel;

/// <summary>One YAML document: a root node.</summary>
public class YamlDocument
{
    /// <summary>Creates a document with a root node.</summary>
    /// <param name="rootNode">The root.</param>
    public YamlDocument(YamlNode rootNode)
    {
        RootNode = rootNode ?? throw new ArgumentNullException(nameof(rootNode));
    }

    /// <summary>Creates a document whose root is a scalar.</summary>
    /// <param name="rootNode">The text of the scalar.</param>
    public YamlDocument(string rootNode)
        : this(new YamlScalarNode(rootNode))
    {
    }

    /// <summary>The root node.</summary>
    public YamlNode RootNode { get; }

    /// <summary>The root and every node below it.</summary>
    public IEnumerable<YamlNode> AllNodes => RootNode.AllNodes;

    /// <summary>Accepts a visitor.</summary>
    /// <param name="visitor">The visitor.</param>
    public void Accept(IYamlVisitor visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        visitor.Visit(this);
    }
}

/// <summary>A stream of YAML documents, loaded from text or built in code.</summary>
/// <remarks>
/// A stream loaded from text keeps the text. <see cref="Save(TextWriter)"/> writes it back unchanged while the model still
/// matches it, comments and layout included, which YamlDotNet's does not; once the model differs the documents are written
/// from the model and the comments go.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "The type name is part of the YamlDotNet API this package mirrors.")]
public class YamlStream : IEnumerable<YamlDocument>
{
    private string? _loadedText;

    /// <summary>Creates an empty stream.</summary>
    public YamlStream()
    {
    }

    /// <summary>Creates a stream of documents.</summary>
    /// <param name="documents">The documents.</param>
    public YamlStream(params YamlDocument[] documents)
        : this((IEnumerable<YamlDocument>)documents)
    {
    }

    /// <summary>Creates a stream of documents.</summary>
    /// <param name="documents">The documents.</param>
    public YamlStream(IEnumerable<YamlDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        Documents.AddRange(documents);
    }

    /// <summary>The documents of the stream.</summary>
    public List<YamlDocument> Documents { get; } = [];

    /// <summary>Adds a document.</summary>
    /// <param name="document">The document.</param>
    public void Add(YamlDocument document) => Documents.Add(document ?? throw new ArgumentNullException(nameof(document)));

    /// <summary>Accepts a visitor.</summary>
    /// <param name="visitor">The visitor.</param>
    public void Accept(IYamlVisitor visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        visitor.Visit(this);
    }

    /// <inheritdoc/>
    public IEnumerator<YamlDocument> GetEnumerator() => Documents.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Reads the documents of a text, adding them to the stream.</summary>
    /// <param name="input">The reader.</param>
    public void Load(TextReader input)
    {
        ArgumentNullException.ThrowIfNull(input);
        Loaded source = Loaded.Parse(input);
        var documents = new List<YamlDocument>();
        try
        {
            DocumentBuilder.Build(source, documents);
        }
        catch (ArgumentException ex)
        {
            throw new Core.YamlException(Mark.Empty, Mark.Empty, ex.Message, ex);
        }
        finally
        {
            // The tree arrays go back to the pool; the nodes keep only the text for their marks.
            source.Dispose();
        }

        // The text is kept only for a stream that was empty before, so the saved text can stand for the whole stream.
        bool wasEmpty = Documents.Count == 0;
        Documents.AddRange(documents);
        _loadedText = wasEmpty ? source.Text : null;
    }

    /// <summary>Reads the documents from a parser, adding them to the stream.</summary>
    /// <param name="parser">The parser, before the stream start or at a document start.</param>
    public void Load(IParser parser)
    {
        ArgumentNullException.ThrowIfNull(parser);
        parser.TryConsume<StreamStart>(out _);
        while (!parser.TryConsume<StreamEnd>(out _))
        {
            Documents.Add(EventDocumentBuilder.Read(parser));
        }

        _loadedText = null;
    }

    /// <summary>Writes the stream as YAML text, giving anchors to nodes that are used more than once.</summary>
    /// <param name="output">The writer.</param>
    public void Save(TextWriter output) => Save(output, true);

    /// <summary>Writes the stream as YAML text.</summary>
    /// <param name="output">The writer.</param>
    /// <param name="assignAnchors">Whether nodes used more than once get anchors.</param>
    public void Save(TextWriter output, bool assignAnchors)
    {
        ArgumentNullException.ThrowIfNull(output);
        output.Write(ToText(assignAnchors));
    }

    /// <summary>Writes the stream as events.</summary>
    /// <param name="emitter">The emitter.</param>
    /// <param name="assignAnchors">Whether nodes used more than once get anchors.</param>
    public void Save(IEmitter emitter, bool assignAnchors)
    {
        ArgumentNullException.ThrowIfNull(emitter);
        emitter.Emit(new StreamStart());
        foreach (YamlDocument document in Documents)
        {
            EventBuffer buffer = ModelWriter.Write(document, assignAnchors);
            emitter.Emit(new DocumentStart());
            EventTranslator.Emit(buffer, 0, buffer.Count, emitter);
            emitter.Emit(new DocumentEnd(true));
        }

        emitter.Emit(new StreamEnd());
    }

    private string ToText(bool assignAnchors)
    {
        if (_loadedText is not null)
        {
            // The tree was released after loading; parse again to learn whether the model still equals the text.
            using Loaded again = Loaded.Parse(_loadedText);
            if (ModelMatcher.Matches(this, again))
            {
                return _loadedText;
            }
        }

        var settings = new Settings { MaxRecursion = int.MaxValue };
        var sb = new StringBuilder();
        for (int i = 0; i < Documents.Count; i++)
        {
            EventBuffer buffer = ModelWriter.Write(Documents[i], assignAnchors);
            var renderer = new TextRenderer(settings);
            renderer.Document(buffer, 0, buffer.Count, i > 0);
            sb.Append(Encoding.UTF8.GetString(renderer.Written));
        }

        return sb.ToString();
    }
}

// Builds the node model from the flat document tree without going through events.
internal static class DocumentBuilder
{
    public static void Build(Loaded source, List<YamlDocument> into)
    {
        DocState s = source.State;
        int count = s.NoRealDocument ? 0 : s.DocCount;
        for (int d = 0; d < count; d++)
        {
            int root = s.Doc(d).Root;
            if (root < 0)
            {
                var empty = new YamlScalarNode(string.Empty) { Style = ScalarStyle.Plain };
                into.Add(new YamlDocument(empty));
                continue;
            }

            int end = root + s.Node(root).Size;
            var map = new YamlNode?[end - root];
            var pendingKey = new YamlNode?[end - root];
            for (int i = root; i < end; i++)
            {
                ref readonly CstNode n = ref s.Node(i);
                YamlNode node;
                if (n.Kind == NodeKind.Alias)
                {
                    node = map[n.Aux - root]!;
                }
                else
                {
                    node = n.Kind switch
                    {
                        NodeKind.Scalar => new YamlScalarNode(s.ScalarString(i)) { Style = StyleOf(n.Style) },
                        NodeKind.Mapping => new YamlMappingNode { Style = n.IsFlow ? MappingStyle.Flow : MappingStyle.Block },
                        _ => new YamlSequenceNode { Style = n.IsFlow ? SequenceStyle.Flow : SequenceStyle.Block },
                    };
                    if (s.HasAnchor(i))
                    {
                        node.Anchor = AnchorName.AsWritten(Encoding.UTF8.GetString(s.AnchorName(i)));
                    }

                    if (s.HasTag(i))
                    {
                        node.Tag = source.TagOf(i);
                    }

                    // YamlDotNet reports a collection's end at its start; scalars end where their text ends.
                    node.SetSource(source, s.PropStart(i), n.Kind == NodeKind.Scalar ? n.End : s.PropStart(i));
                }

                map[i - root] = node;
                if (n.Parent < 0 || i == root)
                {
                    continue;
                }

                YamlNode parent = map[n.Parent - root]!;
                if (parent is YamlSequenceNode sequence)
                {
                    sequence.Children.Add(node);
                }
                else if (pendingKey[n.Parent - root] is { } key)
                {
                    ((YamlMappingNode)parent).Children.Add(key, node);
                    pendingKey[n.Parent - root] = null;
                }
                else
                {
                    pendingKey[n.Parent - root] = node;
                }
            }

            into.Add(new YamlDocument(map[0]!));
        }
    }

    internal static ScalarStyle StyleOf(byte style) => ((Oy.Parsing.ScalarStyle)style) switch
    {
        Oy.Parsing.ScalarStyle.SingleQuoted => ScalarStyle.SingleQuoted,
        Oy.Parsing.ScalarStyle.DoubleQuoted => ScalarStyle.DoubleQuoted,
        Oy.Parsing.ScalarStyle.Literal => ScalarStyle.Literal,
        Oy.Parsing.ScalarStyle.Folded => ScalarStyle.Folded,
        _ => ScalarStyle.Plain,
    };
}

// Builds a document from parsing events (for a parser that is not backed by a loaded text).
internal static class EventDocumentBuilder
{
    public static YamlDocument Read(IParser parser)
    {
        parser.Consume<DocumentStart>();
        var anchors = new Dictionary<string, YamlNode>(StringComparer.Ordinal);
        var stack = new Stack<(YamlNode Node, YamlNode? PendingKey)>();
        YamlNode? root = null;
        while (true)
        {
            ParsingEvent current = parser.Require<ParsingEvent>();
            YamlNode? produced = null;
            bool closes = false;
            switch (current)
            {
                case Scalar scalar:
                    produced = new YamlScalarNode(scalar.Value) { Style = scalar.Style, Anchor = scalar.Anchor, Tag = scalar.Tag };
                    break;
                case AnchorAlias alias:
                    produced = anchors.TryGetValue(alias.Value.Value ?? string.Empty, out YamlNode? target)
                        ? target
                        : throw new AnchorNotFoundException(current.Start, current.End, $"Anchor '{alias.Value}' not found.");
                    break;
                case SequenceStart start:
                    produced = new YamlSequenceNode { Style = start.Style, Anchor = start.Anchor, Tag = start.Tag };
                    break;
                case MappingStart start:
                    produced = new YamlMappingNode { Style = start.Style, Anchor = start.Anchor, Tag = start.Tag };
                    break;
                case SequenceEnd or MappingEnd:
                    closes = true;
                    break;
                case DocumentEnd:
                    parser.MoveNext();
                    return new YamlDocument(root ?? new YamlScalarNode(string.Empty));
                default:
                    throw new Core.YamlException(current.Start, current.End, $"Unexpected '{current.GetType().Name}' inside a document.");
            }

            parser.MoveNext();
            if (closes)
            {
                stack.Pop();
                continue;
            }

            if (produced!.Anchor is { IsEmpty: false } name && current is not AnchorAlias)
            {
                anchors[name.Value!] = produced;
            }

            if (stack.Count == 0)
            {
                root = produced;
            }
            else
            {
                (YamlNode parentNode, YamlNode? pending) = stack.Pop();
                if (parentNode is YamlSequenceNode sequence)
                {
                    sequence.Children.Add(produced);
                    stack.Push((parentNode, null));
                }
                else if (pending is null)
                {
                    stack.Push((parentNode, produced));
                }
                else
                {
                    ((YamlMappingNode)parentNode).Children.Add(pending, produced);
                    stack.Push((parentNode, null));
                }
            }

            if (current is SequenceStart or MappingStart)
            {
                stack.Push((produced, null));
            }
        }
    }
}

// Turns a document model into output events. Recursion is bounded by the depth limit.
internal static class ModelWriter
{
    public static EventBuffer Write(YamlDocument document, bool assignAnchors)
    {
        var buffer = new EventBuffer();
        var seen = new Dictionary<YamlNode, int>(ReferenceEqualityComparer.Instance);
        int visits = 0;
        Emit(buffer, document.RootNode, seen, assignAnchors, 0, ref visits);
        return buffer;
    }

    private static void Emit(EventBuffer buffer, YamlNode node, Dictionary<YamlNode, int> seen, bool assignAnchors, int depth, ref int visits)
    {
        YamlNode.Guard(depth, ref visits);
        bool shared = node is not YamlScalarNode || !node.Anchor.IsEmpty;
        if (shared && seen.TryGetValue(node, out int first))
        {
            if (assignAnchors || !node.Anchor.IsEmpty)
            {
                buffer[first].NeedsAnchor = true;
                buffer.Add(new Ev { Kind = EvKind.Alias, Target = first });
                return;
            }
        }
        else if (shared)
        {
            seen[node] = buffer.Count;
        }

        string? tag = node.Tag.Value;
        string? anchor = node.Anchor.Value;
        switch (node)
        {
            case YamlScalarNode scalar:
                buffer.Add(new Ev { Kind = EvKind.Scalar, Text = scalar.Value ?? string.Empty, Style = scalar.Style, Tag = tag, Anchor = anchor, Target = -1 });
                break;
            case YamlSequenceNode sequence:
                buffer.Add(new Ev { Kind = EvKind.SequenceStart, Flow = sequence.Style == SequenceStyle.Flow, Tag = tag, Anchor = anchor, Target = -1 });
                foreach (YamlNode child in sequence.Children)
                {
                    Emit(buffer, child, seen, assignAnchors, depth + 1, ref visits);
                }

                buffer.Add(new Ev { Kind = EvKind.SequenceEnd });
                break;
            case YamlMappingNode mapping:
                buffer.Add(new Ev { Kind = EvKind.MappingStart, Flow = mapping.Style == MappingStyle.Flow, Tag = tag, Anchor = anchor, Target = -1 });
                foreach (KeyValuePair<YamlNode, YamlNode> pair in mapping.Children)
                {
                    Emit(buffer, pair.Key, seen, assignAnchors, depth + 1, ref visits);
                    Emit(buffer, pair.Value, seen, assignAnchors, depth + 1, ref visits);
                }

                buffer.Add(new Ev { Kind = EvKind.MappingEnd });
                break;
            default:
                throw new NotSupportedException($"Unknown node type {node.GetType()}.");
        }
    }
}

// Tells whether the model still describes exactly the text it was loaded from.
internal static class ModelMatcher
{
    public static bool Matches(YamlStream stream, Loaded source)
    {
        DocState s = source.State;
        int count = s.NoRealDocument ? 0 : s.DocCount;
        if (stream.Documents.Count != count)
        {
            return false;
        }

        int visits = 0;
        for (int d = 0; d < count; d++)
        {
            int root = s.Doc(d).Root;
            YamlNode model = stream.Documents[d].RootNode;
            if (root < 0)
            {
                if (model is not YamlScalarNode { Value.Length: 0 })
                {
                    return false;
                }

                continue;
            }

            var stack = new Stack<(YamlNode Node, int Cst)>();
            stack.Push((model, root));
            while (stack.Count > 0)
            {
                (YamlNode node, int i) = stack.Pop();
                if (++visits > YamlNode.MaxVisits)
                {
                    return false;
                }

                ref readonly CstNode n = ref s.Node(i);
                if (n.Kind == NodeKind.Alias)
                {
                    ReadOnlySpan<byte> name = s.AnchorName(n.Aux);
                    if (node.Anchor.Value is not { } given || !name.SequenceEqual(Encoding.UTF8.GetBytes(given)))
                    {
                        return false;
                    }

                    continue;
                }

                ReadOnlySpan<byte> anchor = s.AnchorName(i);
                if (!(anchor.IsEmpty ? node.Anchor.IsEmpty : node.Anchor.Value is { } an && anchor.SequenceEqual(Encoding.UTF8.GetBytes(an))))
                {
                    return false;
                }

                if (source.TagOf(i) != node.Tag)
                {
                    return false;
                }

                switch (n.Kind)
                {
                    case NodeKind.Scalar:
                        if (node is not YamlScalarNode scalar || scalar.Style != DocumentBuilder.StyleOf(n.Style) || !string.Equals(scalar.Value, s.ScalarString(i), StringComparison.Ordinal))
                        {
                            return false;
                        }

                        break;
                    case NodeKind.Sequence:
                        {
                            if (node is not YamlSequenceNode sequence || sequence.Children.Count != n.Count || (sequence.Style == SequenceStyle.Flow) != n.IsFlow)
                            {
                                return false;
                            }

                            int child = i + 1;
                            for (int k = 0; k < n.Count; k++)
                            {
                                stack.Push((sequence.Children[k], child));
                                child += s.Node(child).Size;
                            }

                            break;
                        }

                    default:
                        {
                            if (node is not YamlMappingNode mapping || mapping.Children.Count != n.Count / 2 || (mapping.Style == MappingStyle.Flow) != n.IsFlow)
                            {
                                return false;
                            }

                            int child = i + 1;
                            for (int k = 0; k < mapping.Children.Count; k++)
                            {
                                stack.Push((mapping.Children[k].Key, child));
                                child += s.Node(child).Size;
                                stack.Push((mapping.Children[k].Value, child));
                                child += s.Node(child).Size;
                            }

                            break;
                        }
                }
            }
        }

        return true;
    }
}
