using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Unicode;

namespace OneYaml;

/// <summary>
/// Writes YAML as UTF-8 into an <see cref="IBufferWriter{T}"/>, in block style. The caller describes the value as a
/// sequence of calls (<see cref="WriteStartMapping()"/>, <see cref="WritePropertyName(string)"/>, a scalar value,
/// <see cref="WriteEndMapping"/>); the emitter decides indentation, quoting and block scalars so the text reads back as exactly
/// the value described, under the Core schema and under YAML 1.1. One call to a value method writes one root value
/// and ends it with a line break.
/// </summary>
/// <remarks>
/// The emitter is not thread-safe. A scalar written where a mapping key is expected becomes the key, so a converter
/// for a key type writes its key like any other value.
/// </remarks>
public sealed partial class Utf8YamlEmitter
{
    // The first element or entry of a collection is written where the parent left the cursor.
    private const byte OpenLineStart = 0; // at the start of a line (the root)
    private const byte OpenInline = 1;    // after "- ", on the same line
    private const byte OpenAfterKey = 2;  // after "key:", so a line break comes first

    // Power of two at which the stack is searched for a value that contains itself.
    private const int CycleCheckFrom = 256;

    [ThreadStatic]
    private static Utf8YamlEmitter? s_cached;

    private IBufferWriter<byte> _output = null!;
    private Emitting.Utf8YamlWriter _w;
    private Frame[] _frames = new Frame[16];
    private int _depth;
    private int _indentSize;
    private int _maxDepth;
    private bool _rootDone;
    private Embedding _embedding;
    private int _embeddedParentIndent;
    private bool _usedFrames;
    private ArrayBufferWriter<byte>? _scratch;
    private byte[]? _smallBuffer;

    // Set while the generic writer walks collections for this emitter, so that a nested walk starts with no frame storage.
    internal bool GenericWalkActive;

    // Where the root value of this emitter sits when it is not a document of its own but the value of an entry
    // in text that is already written: the caller has written "key:" (AfterKey) or "- " (AfterDash).
    internal enum Embedding : byte
    {
        None,
        AfterKey,
        AfterDash,
    }

    private struct Frame
    {
        public object? Reference;
        public int Count;
        public int Indent;
        public byte Open;
        public bool IsMapping;
        public bool KeyPending;
    }

    /// <summary>Creates an emitter.</summary>
    /// <param name="output">Where the UTF-8 bytes go.</param>
    /// <param name="options">Options; <see langword="null"/> means <see cref="YamlSerializerOptions.Default"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="output"/> is <see langword="null"/>.</exception>
    public Utf8YamlEmitter(IBufferWriter<byte> output, YamlSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        Reset(output, options ?? YamlSerializerOptions.Default);
    }

    private void Reset(IBufferWriter<byte> output, YamlSerializerOptions options)
    {
        _output = output;
        _w = new Emitting.Utf8YamlWriter(output);
        _indentSize = options.IndentSize;
        _maxDepth = options.Limits.MaxDepth;
        Options = options;
        _depth = 0;
        _rootDone = false;
        _embedding = Embedding.None;
        _embeddedParentIndent = 0;
    }

    // Makes the root value the value of an entry at the given column (of the key or of the dash). The value is
    // written the way the entry's siblings are, ending with a line break.
    internal void Embed(Embedding embedding, int parentIndent)
    {
        _embedding = embedding;
        _embeddedParentIndent = parentIndent;
    }

    /// <summary>The options in effect.</summary>
    public YamlSerializerOptions Options { get; private set; } = null!;

    /// <summary>The number of collections open now.</summary>
    public int CurrentDepth => _depth;

    // A cached emitter keeps the frame array between calls, so a steady stream of small writes allocates nothing.
    internal static Utf8YamlEmitter Rent(IBufferWriter<byte> output, YamlSerializerOptions options)
    {
        Utf8YamlEmitter? e = s_cached;
        if (e is null)
        {
            return new Utf8YamlEmitter(output, options);
        }

        s_cached = null;
        e.Reset(output, options);
        return e;
    }

    internal static void Return(Utf8YamlEmitter emitter)
    {
        if (emitter._usedFrames)
        {
            // References would keep values alive after the call.
            Array.Clear(emitter._frames);
            emitter._usedFrames = false;
        }

        if (emitter._usedKeyCache)
        {
            // The table holds the caller's strings.
            Array.Clear(emitter._keyCache!);
            emitter._keyArenaUsed = 0;
            emitter._usedKeyCache = false;
        }

        emitter._depth = 0;
        emitter._w = default;
        emitter._output = null!;
        emitter.Options = null!;
        s_cached = emitter;
    }

    // Checks that exactly one root value was written and every collection was closed.
    internal void Finish()
    {
        if (_depth != 0)
        {
            throw Misuse("A collection was started and not ended.");
        }

        if (!_rootDone)
        {
            throw Misuse("No value was written.");
        }
    }

    internal static YamlException Misuse(string message) => new(message, YamlErrorCategory.Binding, 0, 0, 0);

    private Span<byte> GetSpan(int size) => _output.GetSpan(size);

    private void Advance(int count) => _output.Advance(count);

    // ---- collections ----

    /// <summary>Starts a mapping. Every entry is a <see cref="WritePropertyName(string)"/> call followed by one value.</summary>
    /// <exception cref="YamlException">A value is not allowed here, or the nesting is deeper than <see cref="YamlLimits.MaxDepth"/>.</exception>
    public void WriteStartMapping() => Begin(isMapping: true, reference: null);

    /// <summary>Ends the mapping started last.</summary>
    /// <exception cref="YamlException">No mapping is open, or a key has no value.</exception>
    public void WriteEndMapping() => End(isMapping: true);

    /// <summary>Starts a sequence. Every item is one value.</summary>
    /// <exception cref="YamlException">A value is not allowed here, or the nesting is deeper than <see cref="YamlLimits.MaxDepth"/>.</exception>
    public void WriteStartSequence() => Begin(isMapping: false, reference: null);

    /// <summary>Ends the sequence started last.</summary>
    /// <exception cref="YamlException">No sequence is open.</exception>
    public void WriteEndSequence() => End(isMapping: false);

    // The reference is the object being written; the emitter uses it to tell a value that contains itself from deep nesting.
    internal void Begin(bool isMapping, object? reference)
    {
        // Only converters that call each other nest deeper than the explicit stacks do, and a level costs them a few hundred
        // bytes, so asking the runtime every eighth level keeps a wide margin and costs nothing on the others.
        if ((_depth & 7) == 0 && !RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            throw new YamlException("The value is nested too deeply for the stack available.", YamlErrorCategory.Limit, 0, 0, 0);
        }

        byte open;
        int indent;
        if (_depth == 0)
        {
            if (_rootDone)
            {
                throw Misuse("A value was already written; one call writes one document.");
            }

            switch (_embedding)
            {
                case Embedding.AfterKey:
                    open = OpenAfterKey;
                    indent = _embeddedParentIndent + _indentSize;
                    break;
                case Embedding.AfterDash:
                    open = OpenInline;
                    indent = _embeddedParentIndent + 2;
                    break;
                default:
                    open = OpenLineStart;
                    indent = 0;
                    break;
            }
        }
        else
        {
            ref Frame parent = ref _frames[_depth - 1];
            if (parent.IsMapping)
            {
                if (!parent.KeyPending)
                {
                    throw Misuse("A mapping key must be a scalar; write a property name first.");
                }

                parent.KeyPending = false;
                open = OpenAfterKey;
                indent = parent.Indent + _indentSize;
            }
            else
            {
                WriteLead(StartEntryLead(ref parent, dash: true));
                open = OpenInline;
                indent = parent.Indent + 2;
            }
        }

        if (_depth >= _maxDepth || (_depth >= CycleCheckFrom && (_depth & (_depth - 1)) == 0))
        {
            CheckNesting(reference);
        }

        if (_depth == _frames.Length)
        {
            Array.Resize(ref _frames, _frames.Length * 2);
        }

        _usedFrames = true;
        _frames[_depth++] = new Frame { Reference = reference, Indent = indent, Open = open, IsMapping = isMapping };
    }

    // Called when the nesting is deep: a value that contains itself is an error of its own, so the message says so.
    private void CheckNesting(object? reference)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < _depth; i++)
        {
            if (_frames[i].Reference is { } r && !seen.Add(r))
            {
                throw new YamlException("The value refers back to itself, so it has no finite YAML form.", YamlErrorCategory.Binding, 0, 0, 0);
            }
        }

        if (reference is not null && seen.Contains(reference))
        {
            throw new YamlException("The value refers back to itself, so it has no finite YAML form.", YamlErrorCategory.Binding, 0, 0, 0);
        }

        if (_depth >= _maxDepth)
        {
            throw new YamlException("The value is nested deeper than the limit allows.", YamlErrorCategory.Limit, 0, 0, 0);
        }
    }

    private void End(bool isMapping)
    {
        if (_depth == 0 || _frames[_depth - 1].IsMapping != isMapping)
        {
            throw Misuse(isMapping ? "There is no open mapping to end." : "There is no open sequence to end.");
        }

        ref Frame f = ref _frames[_depth - 1];
        if (f.KeyPending)
        {
            throw Misuse("A mapping key was written without a value.");
        }

        if (f.Count == 0)
        {
            // An empty collection has no block form.
            if (f.Open == OpenAfterKey)
            {
                _w.WriteByte((byte)' ');
            }

            _w.WriteRaw(isMapping ? "{}\n"u8 : "[]\n"u8);
        }

        f.Reference = null;
        _depth--;
        if (_depth == 0)
        {
            _rootDone = true;
        }
    }

    // What must come before a scalar or an entry: a line break, indentation, then a dash or a space. It is described
    // first and written afterwards so the lead, the text and the line end can go out in one buffer request.
    private readonly struct Lead(bool newline, int spaces, byte tail)
    {
        public readonly bool Newline = newline;
        public readonly int Spaces = spaces;
        public readonly byte Tail = tail; // 0 nothing, 1 a space, 2 a dash and a space

        public int Length => (Newline ? 1 : 0) + Spaces + Tail;
    }

    // Counts the entry and says what comes before it: the line break and indentation, then the dash of a sequence item.
    private static Lead StartEntryLead(ref Frame f, bool dash = false)
    {
        bool newline = false;
        int spaces = 0;
        if (f.Count == 0)
        {
            switch (f.Open)
            {
                case OpenAfterKey:
                    newline = true;
                    spaces = f.Indent;
                    break;
                case OpenLineStart:
                    spaces = f.Indent;
                    break;
                default:
                    break;
            }
        }
        else
        {
            spaces = f.Indent;
        }

        f.Count++;
        return new Lead(newline, spaces, dash ? (byte)2 : (byte)0);
    }

    private void WriteLead(Lead lead)
    {
        if (lead.Length != 0)
        {
            Span<byte> dst = OpenLead(lead, 0);
            _ = dst;
            Advance(lead.Length);
        }
    }

    // Reserves room for the lead and a payload after it, writes the lead and returns the span for the payload. The
    // caller copies the payload and advances by the lead's length plus the payload's.
    private Span<byte> OpenLead(Lead lead, int payload)
    {
        Span<byte> span = GetSpan(lead.Length + payload);
        int at = 0;
        if (lead.Newline)
        {
            span[at++] = (byte)'\n';
        }

        if (lead.Spaces > 0)
        {
            span.Slice(at, lead.Spaces).Fill((byte)' ');
            at += lead.Spaces;
        }

        if (lead.Tail == 1)
        {
            span[at++] = (byte)' ';
        }
        else if (lead.Tail == 2)
        {
            span[at++] = (byte)'-';
            span[at++] = (byte)' ';
        }

        return span.Slice(at);
    }

    // ---- scalar positions ----

    private enum Place : byte
    {
        Root,
        MapValue,
        SeqItem,
        MapKey,
    }

    // Writes the prefix a scalar needs (the dash of a sequence item, the space after a key) and says where the scalar sits.
    private Place BeginScalar(out int parentIndent)
    {
        Place place = BeginScalarLead(out parentIndent, out Lead lead);
        WriteLead(lead);
        return place;
    }

    // Like BeginScalar, but leaves writing the prefix to the caller.
    private Place BeginScalarLead(out int parentIndent, out Lead lead)
    {
        if (_depth == 0)
        {
            if (_rootDone)
            {
                throw Misuse("A value was already written; one call writes one document.");
            }

            parentIndent = _embeddedParentIndent;
            lead = new Lead(false, 0, _embedding == Embedding.AfterKey ? (byte)1 : (byte)0);
            return Place.Root;
        }

        ref Frame f = ref _frames[_depth - 1];
        parentIndent = f.Indent;
        if (f.IsMapping)
        {
            if (f.KeyPending)
            {
                f.KeyPending = false;
                lead = new Lead(false, 0, 1);
                return Place.MapValue;
            }

            lead = default;
            return Place.MapKey;
        }

        lead = StartEntryLead(ref f, dash: true);
        return Place.SeqItem;
    }

    private void EndScalar(Place place)
    {
        if (place == Place.Root)
        {
            _rootDone = true;
        }
    }
}
