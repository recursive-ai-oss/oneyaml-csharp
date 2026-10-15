using System;
using System.IO;
using OneYaml.Compat.YamlDotNet.Core.Events;
using OneYaml.Compat.YamlDotNet.Internal;
using OneYaml.Documents;
using Oy = global::OneYaml;

namespace OneYaml.Compat.YamlDotNet.Core;

/// <summary>A source of parsing events.</summary>
public interface IParser
{
    /// <summary>The current event, or <see langword="null"/> before the first call to <see cref="MoveNext"/>.</summary>
    ParsingEvent? Current { get; }

    /// <summary>Moves to the next event.</summary>
    /// <returns><see langword="false"/> when the stream has ended.</returns>
    bool MoveNext();
}

/// <summary>A sink for parsing events that writes YAML text.</summary>
public interface IEmitter
{
    /// <summary>Writes an event.</summary>
    /// <param name="parsingEvent">The event.</param>
    void Emit(ParsingEvent parsingEvent);
}

/// <summary>Reads YAML text as a stream of events.</summary>
/// <remarks>The text is parsed as a whole when the parser is created, so errors surface from the constructor.</remarks>
public class Parser : IParser, IDisposable
{
    private enum Phase
    {
        Start,
        BetweenDocuments,
        DocumentRoot,
        InDocument,
        Done,
    }

    private readonly Loaded _source;
    private readonly bool _ownsSource;
    private readonly bool _subtree;
    private readonly int _subtreeRoot;
    private bool _pendingRoot;
    private int[] _node = new int[16];
    private int[] _next = new int[16];
    private int _depth;
    private int _document = -1;
    private Phase _phase;

    /// <summary>Creates a parser over text.</summary>
    /// <param name="input">The reader to take the text from.</param>
    public Parser(TextReader input)
    {
        _source = Loaded.Parse(input);
        _ownsSource = true;
    }

    // A parser over one node of a parsed text: no stream or document events, only the node.
    internal Parser(Loaded source, int root)
    {
        _source = source;
        _subtree = true;
        _subtreeRoot = root;
        _phase = Phase.InDocument;
        _pendingRoot = true;
    }

    /// <inheritdoc/>
    public ParsingEvent? Current { get; private set; }

    // The node the current event stands for (for scalars, aliases and collection starts), or -1.
    internal int CurrentNode { get; private set; } = -1;

    internal Loaded Source => _source;

    /// <inheritdoc/>
    public bool MoveNext()
    {
        if (Current is StreamEnd)
        {
            return false;
        }

        CurrentNode = -1;
        Current = Produce();
        return Current is not null;
    }

    // Leaves the node the current event starts: a scalar or alias is consumed, a collection is skipped with its content.
    internal void SkipNode()
    {
        if (Current is MappingStart or SequenceStart && _depth > 0 && _node[_depth - 1] == CurrentNode)
        {
            _depth--;
        }

        MoveNext();
    }

    /// <summary>Releases the parsed text.</summary>
    public void Dispose()
    {
        if (_ownsSource)
        {
            _source.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private ParsingEvent? Produce()
    {
        DocState s = _source.State;
        while (true)
        {
            switch (_phase)
            {
                case Phase.Start:
                    _phase = Phase.BetweenDocuments;
                    return new StreamStart(_source.MarkAt(0), _source.MarkAt(0));
                case Phase.BetweenDocuments:
                    {
                        int count = s.NoRealDocument ? 0 : s.DocCount;
                        _document++;
                        if (_document >= count)
                        {
                            _phase = Phase.Done;
                            Mark end = _source.MarkAt(s.Length);
                            return new StreamEnd(end, end);
                        }

                        _phase = Phase.DocumentRoot;
                        ref readonly DocInfo d = ref s.Doc(_document);
                        return new DocumentStart(!d.ExplicitStart, _source.MarkAt(d.StartPos), _source.MarkAt(d.StartEnd));
                    }

                case Phase.DocumentRoot:
                    {
                        int root = s.Doc(_document).Root;
                        _phase = Phase.InDocument;
                        if (root < 0)
                        {
                            _depth = 0;
                            Mark at = _source.MarkAt(s.Doc(_document).StartEnd);
                            return new Scalar(AnchorName.Empty, TagName.Empty, string.Empty, ScalarStyle.Plain, true, false, at, at, false);
                        }

                        return Start(root);
                    }

                case Phase.InDocument:
                    if (_pendingRoot)
                    {
                        _pendingRoot = false;
                        return Start(_subtreeRoot);
                    }

                    if (_depth == 0)
                    {
                        if (_subtree)
                        {
                            _phase = Phase.Done;
                            return null;
                        }

                        _phase = Phase.BetweenDocuments;
                        ref readonly DocInfo d = ref s.Doc(_document);
                        return new DocumentEnd(true, _source.MarkAt(d.EndPos), _source.MarkAt(d.EndPos));
                    }

                    {
                        int top = _depth - 1;
                        int node = _node[top];
                        int end = node + s.Node(node).Size;
                        if (_next[top] >= end)
                        {
                            _depth--;
                            Mark at = _source.MarkAt(s.Node(node).End);
                            return s.Node(node).Kind == NodeKind.Mapping ? new MappingEnd(at, at) : new SequenceEnd(at, at);
                        }

                        int child = _next[top];
                        _next[top] = child + s.Node(child).Size;
                        return Start(child);
                    }

                default:
                    return null;
            }
        }
    }

    private ParsingEvent Start(int node)
    {
        DocState s = _source.State;
        ref readonly CstNode n = ref s.Node(node);
        CurrentNode = node;
        Mark start = _source.MarkAt(s.PropStart(node));
        Mark end = _source.MarkAt(n.End);
        if (n.Kind == NodeKind.Alias)
        {
            return new AnchorAlias(AnchorName.AsWritten(System.Text.Encoding.UTF8.GetString(s.AnchorName(n.Aux))), start, end);
        }

        AnchorName anchor = AnchorName.AsWritten(s.AnchorName(node).IsEmpty ? null : System.Text.Encoding.UTF8.GetString(s.AnchorName(node)));
        TagName tag = _source.TagOf(node);
        if (n.Kind == NodeKind.Scalar)
        {
            ScalarStyle style = ((Oy.Parsing.ScalarStyle)n.Style) switch
            {
                Oy.Parsing.ScalarStyle.SingleQuoted => ScalarStyle.SingleQuoted,
                Oy.Parsing.ScalarStyle.DoubleQuoted => ScalarStyle.DoubleQuoted,
                Oy.Parsing.ScalarStyle.Literal => ScalarStyle.Literal,
                Oy.Parsing.ScalarStyle.Folded => ScalarStyle.Folded,
                _ => ScalarStyle.Plain,
            };
            bool plain = style == ScalarStyle.Plain;
            return new Scalar(anchor, tag, s.ScalarString(node), style, plain && tag.IsEmpty, !plain && tag.IsEmpty, start, end, (n.Flags & NodeFlags.IsKey) != 0);
        }

        if (_depth == _node.Length)
        {
            Array.Resize(ref _node, _depth * 2);
            Array.Resize(ref _next, _depth * 2);
        }

        _node[_depth] = node;
        _next[_depth] = node + 1;
        _depth++;
        return n.Kind == NodeKind.Mapping
            ? new MappingStart(anchor, tag, tag.IsEmpty, n.IsFlow ? MappingStyle.Flow : MappingStyle.Block, start, end)
            : new SequenceStart(anchor, tag, tag.IsEmpty, n.IsFlow ? SequenceStyle.Flow : SequenceStyle.Block, start, end);
    }
}

/// <summary>Helpers for reading events from an <see cref="IParser"/>.</summary>
public static class ParserExtensions
{
    /// <summary>Requires the current event to be of a type, then moves past it.</summary>
    /// <typeparam name="T">The event type.</typeparam>
    /// <param name="parser">The parser.</param>
    /// <returns>The event.</returns>
    /// <exception cref="YamlException">The current event is of another type.</exception>
    public static T Consume<T>(this IParser parser)
        where T : ParsingEvent
    {
        T required = Require<T>(parser);
        parser.MoveNext();
        return required;
    }

    /// <summary>Moves past the current event when it has the type.</summary>
    /// <typeparam name="T">The event type.</typeparam>
    /// <param name="parser">The parser.</param>
    /// <param name="event">The event, when it had the type.</param>
    /// <returns>Whether the event had the type.</returns>
    public static bool TryConsume<T>(this IParser parser, out T @event)
        where T : ParsingEvent
    {
        if (Accept(parser, out @event))
        {
            parser.MoveNext();
            return true;
        }

        return false;
    }

    /// <summary>Tells whether the current event has the type.</summary>
    /// <typeparam name="T">The event type.</typeparam>
    /// <param name="parser">The parser.</param>
    /// <param name="event">The event, when it had the type.</param>
    /// <returns>Whether the event had the type.</returns>
    public static bool Accept<T>(this IParser parser, out T @event)
        where T : ParsingEvent
    {
        ArgumentNullException.ThrowIfNull(parser);
        if (parser.Current is null && !parser.MoveNext())
        {
            throw new EndOfStreamException();
        }

        if (parser.Current is T typed)
        {
            @event = typed;
            return true;
        }

        @event = default!;
        return false;
    }

    /// <summary>Tells whether the current event has the type.</summary>
    /// <typeparam name="T">The event type.</typeparam>
    /// <param name="parser">The parser.</param>
    /// <returns>Whether the event has the type.</returns>
    public static bool Accept<T>(this IParser parser)
        where T : ParsingEvent => Accept<T>(parser, out _);

    /// <summary>Returns the current event, which must have the type.</summary>
    /// <typeparam name="T">The event type.</typeparam>
    /// <param name="parser">The parser.</param>
    /// <returns>The event.</returns>
    /// <exception cref="YamlException">The current event is of another type.</exception>
    public static T Require<T>(this IParser parser)
        where T : ParsingEvent
    {
        if (!Accept(parser, out T @event))
        {
            ParsingEvent? current = parser.Current;
            Mark at = current?.Start ?? Mark.Empty;
            throw new Core.YamlException(at, at, $"Expected '{typeof(T).Name}', got '{current?.GetType().Name}' (at {at}).");
        }

        return @event;
    }

    /// <summary>Skips the current event and everything nested in it.</summary>
    /// <param name="parser">The parser.</param>
    public static void SkipThisAndNestedEvents(this IParser parser)
    {
        ArgumentNullException.ThrowIfNull(parser);
        int depth = 0;
        do
        {
            depth += parser.Current?.NestingIncrease ?? 0;
            parser.MoveNext();
        }
        while (depth > 0);
    }
}

