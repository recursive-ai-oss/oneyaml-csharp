using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using OneYaml.Documents;
using OneYaml.Text;

namespace OneYaml;

/// <summary>
/// A YAML stream held with all its text, comments and layout. Writing it back returns the input
/// byte for byte; an edit changes only the text of the value it touches. A document is not
/// thread-safe. A <see cref="YamlNode"/> view stays valid across edits, the way a <c>JsonNode</c> does: it
/// follows its node when other entries are added or removed, and throws only once its node is gone.
/// </summary>
/// <remarks>
/// Pointer methods address a value with an RFC 6901 JSON Pointer. A pointer token names a mapping
/// key by the key's text (so <c>/80</c> reaches the key <c>80</c>, <c>"80"</c> and <c>0x50</c> alike
/// only when their text is the same), and a sequence item by its index. Reads follow aliases;
/// writes through an alias are refused.
/// </remarks>
public sealed class YamlDocument : IDisposable
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    private DocState? _state;
    private readonly YamlLimits _limits;
    private int _editCount;
    private WeakReference<EditEpoch>? _head;
    private DocumentList? _documents;
    private AliasBudget _walk;
    private YamlSerializerOptions? _bindOptions;

    private readonly string? _sourceName;

    private YamlDocument(DocState state, YamlLimits limits, string? sourceName)
    {
        _state = state;
        _limits = limits;
        _sourceName = sourceName;
        ResetWalk();
    }

    /// <summary>The documents of the stream, in order. A stream with no document (an empty text, or only comments) has none.</summary>
    public IReadOnlyList<YamlNode> Documents => _documents ??= new DocumentList(this);

    /// <summary>
    /// The root of the first document. A stream with no document has an implicit empty one: it reads as
    /// null, and the first write to it makes the document.
    /// </summary>
    /// <remarks>
    /// Every call starts a new alias budget (the nodes and bytes that reading through aliases may expand to),
    /// shared by all views reached from the root it returns. A reader that lives long should take the root
    /// again for each operation instead of reusing one view, or its reads add up against a single budget.
    /// </remarks>
    public YamlNode Root
    {
        get
        {
            DocState s = State;
            ResetWalk();
            return new YamlNode(this, s.Doc(0).Root, Track(), false);
        }
    }

    internal YamlLimits Limits => _limits;

    internal string? SourceName => _sourceName;

    // The alias budget of the walk that started at the last root taken, shared by every view derived from it.
    internal ref AliasBudget Walk => ref _walk;

    internal void ResetWalk() => _walk = new AliasBudget(State, _limits);

    // Options for reading scalars through the binder's converters, carrying this document's limits.
    internal YamlSerializerOptions BindOptions => _bindOptions ??= ReferenceEquals(_limits, YamlLimits.Default) ? YamlSerializerOptions.Default : new YamlSerializerOptions { Limits = _limits, SourceName = _sourceName };

    // Options a caller passed, made no looser than the limits this document was opened with: a document opened for untrusted
    // input must not be bound with limits the caller reached for without thinking. The result is kept per options object, so the
    // type metadata made for it is made once.
    private System.Runtime.CompilerServices.ConditionalWeakTable<YamlSerializerOptions, YamlSerializerOptions>? _strictOptions;

    internal YamlSerializerOptions StricterOptions(YamlSerializerOptions options)
    {
        YamlLimits stricter = YamlLimits.Stricter(_limits, options.Limits);
        if (stricter.Equals(options.Limits))
        {
            return options;
        }

        _strictOptions ??= new();
        return _strictOptions.GetValue(options, o => new YamlSerializerOptions(o) { Limits = stricter });
    }

    // How many edits the document has had. A view whose epoch has a lower number follows the edits made since.
    internal int EditCount => _editCount;

    // Called when a view is handed out; returns the epoch it starts in. While any view is alive, edits note
    // what they did to the nodes. The document holds the latest epoch only weakly: views hold it (and, through
    // it, every later one), so once the last view is gone the epoch is collected and edits stop noting anything.
    // A document that is only edited by pointer never gets here and pays nothing.
    internal EditEpoch Track()
    {
        if (_head is not null && _head.TryGetTarget(out EditEpoch? epoch))
        {
            return epoch;
        }

        epoch = new EditEpoch { Number = _editCount };
        if (_head is null)
        {
            _head = new WeakReference<EditEpoch>(epoch);
        }
        else
        {
            _head.SetTarget(epoch);
        }

        return epoch;
    }

    private bool ViewsMayExist(out EditEpoch? head)
    {
        head = null;
        return _head is not null && _head.TryGetTarget(out head);
    }

    internal DocState State
    {
        get
        {
            DocState s = _state ?? throw new ObjectDisposedException(nameof(YamlDocument));
            return s.Poisoned ? throw new InvalidOperationException("The document could not be restored after a failed edit and cannot be used.") : s;
        }
    }

    // ---- Reading ----

    /// <summary>Parses YAML text.</summary>
    /// <param name="text">The YAML text. A leading byte order mark (U+FEFF) is kept and written back.</param>
    /// <param name="options">Options, or <see langword="null"/> for the defaults.</param>
    /// <returns>The document.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    /// <exception cref="YamlException">The text is not valid YAML, or exceeds a limit.</exception>
    /// <example>
    /// <code language="csharp">
    /// using var doc = YamlDocument.Parse("name: web   # the public site\nreplicas: 2\n");
    /// doc.Root["replicas"] = 3;
    /// Console.Write(doc);
    /// // name: web   # the public site
    /// // replicas: 3
    /// </code>
    /// </example>
    public static YamlDocument Parse(string text, YamlDocumentOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Parse(text.AsSpan(), options);
    }

    /// <summary>Parses YAML text.</summary>
    /// <param name="text">The YAML text.</param>
    /// <param name="options">Options, or <see langword="null"/> for the defaults.</param>
    /// <returns>The document.</returns>
    /// <exception cref="YamlException">The text is not valid YAML, or exceeds a limit.</exception>
    public static YamlDocument Parse(ReadOnlySpan<char> text, YamlDocumentOptions? options = null)
    {
        YamlLimits limits = options?.Limits ?? YamlLimits.Default;
        try
        {
            using YamlInput input = YamlInput.FromChars(text, limits);
            return new YamlDocument(DocState.Build(input.Utf8Text, input.Encoding, input.HasByteOrderMark, limits, options?.AllowDuplicateKeys ?? false, options?.QuoteYaml11Lookalikes ?? false), limits, options?.SourceName);
        }
        catch (YamlException e) when (YamlException.Tag(e, options?.SourceName))
        {
            throw;
        }
    }

    /// <summary>Parses YAML bytes: UTF-8, UTF-16 or UTF-32, detected from a byte order mark or the first characters.</summary>
    /// <param name="bytes">The bytes.</param>
    /// <param name="options">Options, or <see langword="null"/> for the defaults.</param>
    /// <returns>The document. Writing it back uses the same encoding.</returns>
    /// <exception cref="YamlException">The bytes are not valid text or YAML, or exceed a limit.</exception>
    public static YamlDocument Parse(ReadOnlySpan<byte> bytes, YamlDocumentOptions? options = null)
    {
        YamlLimits limits = options?.Limits ?? YamlLimits.Default;
        try
        {
            using YamlInput input = YamlInput.FromBytes(bytes, limits);
            return new YamlDocument(DocState.Build(input.Utf8Text, input.Encoding, input.HasByteOrderMark, limits, options?.AllowDuplicateKeys ?? false, options?.QuoteYaml11Lookalikes ?? false), limits, options?.SourceName);
        }
        catch (YamlException e) when (YamlException.Tag(e, options?.SourceName))
        {
            throw;
        }
    }

    /// <summary>Reads a stream to its end and parses it.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="options">Options, or <see langword="null"/> for the defaults.</param>
    /// <returns>The document.</returns>
    /// <exception cref="YamlException">The stream is not valid text or YAML, or exceeds a limit.</exception>
    public static YamlDocument Parse(Stream stream, YamlDocumentOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        long max = (options?.Limits ?? YamlLimits.Default).MaxInputBytes;
        CheckRemaining(stream, max, options?.SourceName);
        using var buffer = new ByteBuffer(4096);
        while (true)
        {
            int n = stream.Read(buffer.GetSpan(4096));
            if (n <= 0)
            {
                break;
            }

            buffer.Advance(n);
            CheckSize(buffer.Count, max, options?.SourceName);
        }

        return Parse(buffer.WrittenSpan, options);
    }

    /// <summary>Reads a stream to its end asynchronously and parses it. This is <see cref="Parse(Stream, YamlDocumentOptions?)"/> without blocking.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="options">Options, or <see langword="null"/> for the defaults.</param>
    /// <param name="cancellationToken">A token to cancel reading.</param>
    /// <returns>The document.</returns>
    /// <exception cref="YamlException">The stream is not valid text or YAML, or exceeds a limit.</exception>
    public static async Task<YamlDocument> ParseAsync(Stream stream, YamlDocumentOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        long max = (options?.Limits ?? YamlLimits.Default).MaxInputBytes;
        CheckRemaining(stream, max, options?.SourceName);
        using var buffer = new ByteBuffer(4096);
        while (true)
        {
            int n = await stream.ReadAsync(buffer.GetMemory(4096), cancellationToken).ConfigureAwait(false);
            if (n <= 0)
            {
                break;
            }

            buffer.Advance(n);
            CheckSize(buffer.Count, max, options?.SourceName);
        }

        return Parse(buffer.WrittenSpan, options);
    }

    // A stream that knows its length is refused before anything is read or rented.
    private static void CheckRemaining(Stream stream, long max, string? sourceName)
    {
        if (stream.CanSeek && stream.Length - stream.Position > max)
        {
            throw new YamlException("The input is larger than the configured limit.", YamlErrorCategory.Limit, 0, 0, 0) { SourceName = sourceName };
        }
    }

    // Stops reading as soon as the input passes the limit, so an endless stream cannot fill memory first.
    private static void CheckSize(int length, long max, string? sourceName)
    {
        if (length > max)
        {
            throw new YamlException("The input is larger than the configured limit.", YamlErrorCategory.Limit, 0, 0, 0) { SourceName = sourceName };
        }
    }

    /// <summary>Returns the pooled memory the document holds. Using the document or a view of it afterwards throws <see cref="ObjectDisposedException"/>.</summary>
    public void Dispose()
    {
        _state?.Dispose();
        _state = null;
    }

    // ---- Writing ----

    /// <summary>The text of the stream, exactly as it was read plus any edits.</summary>
    /// <returns>The text. A byte order mark appears as U+FEFF.</returns>
    public override string ToString()
    {
        DocState s = State;
        string text = Encoding.UTF8.GetString(s.Text);
        return s.HasByteOrderMark ? "\uFEFF" + text : text;
    }

    /// <summary>The stream as UTF-8 bytes, with a byte order mark when the input was UTF-8 and had one.</summary>
    /// <returns>The bytes.</returns>
    public byte[] ToUtf8Bytes()
    {
        DocState s = State;
        bool bom = s.HasByteOrderMark && s.Encoding == YamlEncoding.Utf8;
        // Every byte is written below, so the array need not be zeroed first: for a large document the
        // clearing costs about as much as the copy.
        byte[] result = GC.AllocateUninitializedArray<byte>(s.Length + (bom ? 3 : 0));
        if (bom)
        {
            result[0] = 0xEF;
            result[1] = 0xBB;
            result[2] = 0xBF;
        }

        s.Text.CopyTo(result.AsSpan(bom ? 3 : 0));
        return result;
    }

    /// <summary>The stream in the encoding (and with the byte order mark) it was read in.</summary>
    /// <returns>The bytes: for an unedited document, the input. A UTF-16 or UTF-32 stream without a byte order mark gets one if an edit made its first character non-ASCII, as it could not be detected otherwise.</returns>
    public byte[] ToBytes()
    {
        DocState s = State;
        if (s.Encoding == YamlEncoding.Utf8)
        {
            return ToUtf8Bytes();
        }

        Encoding enc = s.Encoding switch
        {
            YamlEncoding.Utf16LittleEndian => new UnicodeEncoding(false, true, true),
            YamlEncoding.Utf16BigEndian => new UnicodeEncoding(true, true, true),
            YamlEncoding.Utf32LittleEndian => new UTF32Encoding(false, true, true),
            _ => new UTF32Encoding(true, true, true),
        };
        string text = Encoding.UTF8.GetString(s.Text);
        // Without a mark, UTF-16 and UTF-32 are recognised by an ASCII first character. If an edit made
        // the first character something else, a mark keeps the output readable in the same encoding.
        bool needMark = s.HasByteOrderMark || (text.Length > 0 && text[0] >= 0x80);
        byte[] preamble = needMark ? enc.GetPreamble() : [];
        byte[] body = enc.GetBytes(text);
        byte[] result = new byte[preamble.Length + body.Length];
        preamble.CopyTo(result, 0);
        body.CopyTo(result, preamble.Length);
        return result;
    }

    /// <summary>Writes the stream to a stream, in the encoding it was read in.</summary>
    /// <param name="stream">The destination.</param>
    public void WriteTo(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        DocState s = State;
        if (s.Encoding == YamlEncoding.Utf8)
        {
            if (s.HasByteOrderMark)
            {
                stream.Write(Utf8Bom);
            }

            stream.Write(s.Text);
            return;
        }

        stream.Write(ToBytes());
    }

    /// <summary>Writes the stream to a stream asynchronously, in the encoding it was read in.</summary>
    /// <param name="stream">The destination.</param>
    /// <param name="cancellationToken">A token to cancel writing.</param>
    /// <returns>A task that completes when everything is written.</returns>
    public async Task WriteToAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        DocState s = State;
        if (s.Encoding == YamlEncoding.Utf8)
        {
            // The text lives in pooled memory the next edit may give back, so it is copied for the awaited write.
            byte[] bytes = ToUtf8Bytes();
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            return;
        }

        await stream.WriteAsync(ToBytes(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes the stream to a buffer writer, in the encoding it was read in.</summary>
    /// <param name="writer">The destination.</param>
    public void WriteTo(IBufferWriter<byte> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        DocState s = State;
        if (s.Encoding == YamlEncoding.Utf8)
        {
            if (s.HasByteOrderMark)
            {
                writer.Write((ReadOnlySpan<byte>)Utf8Bom);
            }

            writer.Write(s.Text);
            return;
        }

        writer.Write(ToBytes());
    }

    /// <summary>Writes the text of the stream to a text writer.</summary>
    /// <param name="writer">The destination.</param>
    public void WriteTo(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.Write(ToString());
    }

    // ---- Pointer methods ----

    private DocState Current(int document)
    {
        DocState s = State;
        PointerOps.CheckDocument(s, document);
        return s;
    }

    // Notes the edits of the operation that is about to run, when views exist that have to follow them.
    internal void BeginEdit(DocState s)
    {
        // An edit made in place hands the same state back, so the trace keeps the node count the edit began with.
        s.Trace = ViewsMayExist(out _) ? new EditTrace { NodesBefore = s.NodeCount } : null;
        s.TakeChangedInPlace();
    }

    // Begins an edit that works on the document's own state instead of a copy of it, which for a large document is
    // what the edit costs. The state is the one the edit reads and changes; if the edit fails it is put back as it was.
    // Use it in a using declaration, so that the state is shared by readers again however the edit ends.
    internal EditScope BeginInPlaceEdit(DocState s)
    {
        BeginEdit(s);
        s.BeginSingleEdit();
        return new EditScope(s);
    }

    internal readonly struct EditScope : IDisposable
    {
        private readonly DocState _state;

        internal EditScope(DocState state)
        {
            _state = state;
        }

        public void Dispose() => _state.EndSingleEdit();
    }

    // Patches report a document index outside the stream as a patch error, like every other way a patch can fail.
    private DocState CurrentForPatch(int document)
    {
        DocState s = State;
        if ((uint)document >= (uint)s.DocCount)
        {
            throw PointerOps.NoSuchDocumentForPatch(document, s.NoRealDocument ? 0 : s.DocCount);
        }

        return s;
    }

    internal void Swap(DocState next)
    {
        DocState? old = _state;
        bool inPlace = ReferenceEquals(next, old);
        if (inPlace && (old is null || !old.TakeChangedInPlace()))
        {
            // The operation changed nothing.
            return;
        }

        _editCount++;
        if (old is not null && ViewsMayExist(out EditEpoch? head))
        {
            // The edits the operation noted must account for every node that appeared or went; if they do
            // not (or nothing was noted), views cannot be followed and are told so.
            EditTrace? trace = old.Trace;
            NodeEdit[]? edits = trace is not null && trace.NetDelta == next.NodeCount - (inPlace ? trace.NodesBefore : old.NodeCount) ? trace.ToArray() : null;
            var epoch = new EditEpoch { Edits = edits, Number = _editCount };
            head!.Next = epoch;
            _head!.SetTarget(epoch);
        }

        if (!inPlace)
        {
            old?.Dispose();
        }

        next.Trace = null;
        _state = next;
        ResetWalk();
    }

    /// <summary>
    /// Sets the value a pointer names. An existing value is replaced, keeping its style where it
    /// can hold the new value; a missing mapping key is added at the end of its mapping; the
    /// token <c>-</c> (or the sequence length) appends to a sequence.
    /// On a stream with no document (empty, or only comments) the first write makes the document: a first
    /// token of <c>-</c> or <c>0</c> starts a sequence, any other token a mapping.
    /// </summary>
    /// <param name="path">An RFC 6901 pointer. The empty pointer replaces the whole document's root.</param>
    /// <param name="value">null, a string, a boolean, a number, a <see cref="YamlNode"/>, or a dictionary or sequence of these.</param>
    /// <param name="document">The index of the document in the stream. An index outside the stream throws <see cref="ArgumentOutOfRangeException"/>, as <see cref="Documents"/> does.</param>
    /// <exception cref="ArgumentException">The value contains a type that has no YAML form, or one outside the plain value model while reflection is turned off (the default in trimmed and ahead-of-time applications); use the overload that takes a <see cref="YamlTypeInfo{T}"/> there.</exception>
    /// <exception cref="YamlException">The pointer names nothing (<see cref="YamlErrorCategory.PatchPath"/>), matches two keys, or goes through an alias. The document is unchanged.</exception>
    /// <example>
    /// <code language="csharp">
    /// using var doc = YamlDocument.Parse("spec:\n  image: web:1.4   # pinned\n");
    /// doc.Set("/spec/image", "web:1.5");
    /// doc.Set("/spec/ports", new[] { 80, 443 });
    /// Console.Write(doc);
    /// // spec:
    /// //   image: web:1.5   # pinned
    /// //   ports:
    /// //     - 80
    /// //     - 443
    /// </code>
    /// </example>
    public void Set(string path, object? value, int document = 0)
    {
        Write(path, value, document, PointerOps.Mode.Set);
    }

    /// <summary>
    /// Adds a value at a pointer as RFC 6902 <c>add</c> does: an existing mapping key is replaced,
    /// a new key goes at the end of its mapping, and a sequence index inserts before the item there.
    /// On a stream with no document (empty, or only comments) the first write makes the document: a first
    /// token of <c>-</c> or <c>0</c> starts a sequence, any other token a mapping.
    /// </summary>
    /// <param name="path">An RFC 6901 pointer.</param>
    /// <param name="value">The value, as for <see cref="Set"/>.</param>
    /// <param name="document">The index of the document in the stream. An index outside the stream throws <see cref="ArgumentOutOfRangeException"/>, as <see cref="Documents"/> does.</param>
    /// <exception cref="ArgumentException">The value contains a type that has no YAML form, or one outside the plain value model while reflection is turned off (the default in trimmed and ahead-of-time applications); use the overload that takes a <see cref="YamlTypeInfo{T}"/> there.</exception>
    /// <exception cref="YamlException">The pointer is invalid or its parent does not exist, or it goes through an alias. The document is unchanged.</exception>
    public void Add(string path, object? value, int document = 0)
    {
        Write(path, value, document, PointerOps.Mode.Add);
    }

    internal void Write(string path, object? value, int document, PointerOps.Mode mode)
    {
        Pointer p = Pointer.Parse(path);

        // Build the value before taking the state: a value that reads this document while it is built
        // could otherwise leave the edit working on a state that has been replaced.
        ValueTree v = ValueTree.FromObject(value, _limits.MaxDepth, _limits);
        Write(p, v, document, mode);
    }

    private void Write(in Pointer p, ValueTree v, int document, PointerOps.Mode mode)
    {
        DocState s = Current(document);
        using EditScope scope = BeginInPlaceEdit(s);
        Swap(PointerOps.Write(s, _limits, document, p, v, mode));
    }

    /// <summary>
    /// Sets the value a pointer names to a value of any type <paramref name="typeInfo"/> describes, written the way
    /// <see cref="YamlSerializer"/> writes it. Placement and styling are those of <see cref="Set(string, object?, int)"/>.
    /// </summary>
    /// <typeparam name="T">The type of the value.</typeparam>
    /// <param name="path">An RFC 6901 pointer.</param>
    /// <param name="value">The value.</param>
    /// <param name="typeInfo">Metadata for <typeparamref name="T"/>, usually from a generated <see cref="YamlSerializerContext"/>.</param>
    /// <param name="document">The index of the document in the stream. An index outside the stream throws <see cref="ArgumentOutOfRangeException"/>, as <see cref="Documents"/> does.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="document"/> is outside the stream.</exception>
    /// <exception cref="ArgumentException">The value has no YAML form.</exception>
    /// <exception cref="YamlException">The pointer names nothing, matches two keys, or goes through an alias. The document is unchanged.</exception>
    public void Set<T>(string path, T value, YamlTypeInfo<T> typeInfo, int document = 0)
        => Write(Pointer.Parse(path), TypedValues.Of(value, typeInfo, _limits), document, PointerOps.Mode.Set);

    /// <summary>Sets the value a pointer names to a value written by the reflection-based serializer.</summary>
    /// <typeparam name="T">The type of the value.</typeparam>
    /// <param name="path">An RFC 6901 pointer.</param>
    /// <param name="value">The value.</param>
    /// <param name="options">Options for writing the value; <see langword="null"/> means <see cref="YamlSerializerOptions.Default"/>.</param>
    /// <param name="document">The index of the document in the stream. An index outside the stream throws <see cref="ArgumentOutOfRangeException"/>, as <see cref="Documents"/> does.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="document"/> is outside the stream.</exception>
    /// <exception cref="ArgumentException">The value has no YAML form.</exception>
    /// <exception cref="YamlException">The pointer names nothing, matches two keys, or goes through an alias. The document is unchanged.</exception>
    [RequiresUnreferencedCode("Reflection-based serialization reads members that trimming can remove. Pass a YamlTypeInfo from a YamlSerializerContext instead.")]
    [RequiresDynamicCode("Reflection-based serialization makes generic types at run time. Pass a YamlTypeInfo from a YamlSerializerContext instead.")]
    public void Set<T>(string path, T value, YamlSerializerOptions? options, int document = 0)
        => Write(Pointer.Parse(path), TypedValues.Of(value, options, _limits), document, PointerOps.Mode.Set);

    /// <summary>
    /// Adds a value of any type <paramref name="typeInfo"/> describes at a pointer, as <see cref="Add(string, object?, int)"/> does.
    /// </summary>
    /// <typeparam name="T">The type of the value.</typeparam>
    /// <param name="path">An RFC 6901 pointer.</param>
    /// <param name="value">The value.</param>
    /// <param name="typeInfo">Metadata for <typeparamref name="T"/>, usually from a generated <see cref="YamlSerializerContext"/>.</param>
    /// <param name="document">The index of the document in the stream. An index outside the stream throws <see cref="ArgumentOutOfRangeException"/>, as <see cref="Documents"/> does.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="document"/> is outside the stream.</exception>
    /// <exception cref="ArgumentException">The value has no YAML form.</exception>
    /// <exception cref="YamlException">The pointer is invalid or its parent does not exist, or it goes through an alias. The document is unchanged.</exception>
    public void Add<T>(string path, T value, YamlTypeInfo<T> typeInfo, int document = 0)
        => Write(Pointer.Parse(path), TypedValues.Of(value, typeInfo, _limits), document, PointerOps.Mode.Add);

    /// <summary>Adds a value written by the reflection-based serializer at a pointer, as <see cref="Add(string, object?, int)"/> does.</summary>
    /// <typeparam name="T">The type of the value.</typeparam>
    /// <param name="path">An RFC 6901 pointer.</param>
    /// <param name="value">The value.</param>
    /// <param name="options">Options for writing the value; <see langword="null"/> means <see cref="YamlSerializerOptions.Default"/>.</param>
    /// <param name="document">The index of the document in the stream. An index outside the stream throws <see cref="ArgumentOutOfRangeException"/>, as <see cref="Documents"/> does.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="document"/> is outside the stream.</exception>
    /// <exception cref="ArgumentException">The value has no YAML form.</exception>
    /// <exception cref="YamlException">The pointer is invalid or its parent does not exist, or it goes through an alias. The document is unchanged.</exception>
    [RequiresUnreferencedCode("Reflection-based serialization reads members that trimming can remove. Pass a YamlTypeInfo from a YamlSerializerContext instead.")]
    [RequiresDynamicCode("Reflection-based serialization makes generic types at run time. Pass a YamlTypeInfo from a YamlSerializerContext instead.")]
    public void Add<T>(string path, T value, YamlSerializerOptions? options, int document = 0)
        => Write(Pointer.Parse(path), TypedValues.Of(value, options, _limits), document, PointerOps.Mode.Add);

    /// <summary>
    /// Applies an RFC 6902 JSON Patch. The patch is atomic: if any operation fails, the document is
    /// left exactly as it was. A <c>test</c> compares by the YAML data model under the Core schema.
    /// On a stream with no document (empty, or only comments) the first <c>add</c> makes the document: a first
    /// path token of <c>-</c> or <c>0</c> starts a sequence, any other token a mapping.
    /// </summary>
    /// <param name="patch">A JSON array of operations.</param>
    /// <param name="document">The index of the document in the stream. An index outside the stream is a <see cref="YamlErrorCategory.PatchPath"/> error, as for any other path the patch cannot reach.</param>
    /// <exception cref="ArgumentNullException"><paramref name="patch"/> is <see langword="null"/>.</exception>
    /// <exception cref="YamlException">The patch is malformed (<see cref="YamlErrorCategory.PatchInvalid"/>) or an operation failed; <see cref="YamlException.OperationIndex"/> names it.</exception>
    /// <example>
    /// <code language="csharp">
    /// using var doc = YamlDocument.Parse("name: web  # site\nreplicas: 2\n");
    /// doc.ApplyPatch("""[{ "op": "replace", "path": "/replicas", "value": 3 }]""");
    /// Console.Write(doc);
    /// // name: web  # site
    /// // replicas: 3
    /// </code>
    /// </example>
    public void ApplyPatch(string patch, int document = 0)
    {
        ArgumentNullException.ThrowIfNull(patch);
        Apply(PatchEngine.ReadOperations(PatchEngine.ParseJson(patch, _limits), _limits), document);
    }

    /// <summary>Applies an RFC 6902 JSON Patch given as UTF-8 JSON. See <see cref="ApplyPatch(string, int)"/>.</summary>
    /// <param name="utf8Patch">A JSON array of operations.</param>
    /// <param name="document">The index of the document in the stream. An index outside the stream is a <see cref="YamlErrorCategory.PatchPath"/> error, as for any other path the patch cannot reach.</param>
    /// <exception cref="YamlException">The patch is malformed or an operation failed. The document is unchanged.</exception>
    public void ApplyPatch(ReadOnlySpan<byte> utf8Patch, int document = 0)
        => Apply(PatchEngine.ReadOperations(PatchEngine.ParseJson(utf8Patch, _limits), _limits), document);

    /// <summary>Applies an RFC 6902 JSON Patch given as a JSON node tree. See <see cref="ApplyPatch(string, int)"/>.</summary>
    /// <param name="patch">A <see cref="JsonArray"/> of operations.</param>
    /// <param name="document">The index of the document in the stream. An index outside the stream is a <see cref="YamlErrorCategory.PatchPath"/> error, as for any other path the patch cannot reach.</param>
    /// <exception cref="YamlException">The patch is malformed or an operation failed. The document is unchanged.</exception>
    public void ApplyPatch(JsonNode? patch, int document = 0)
        => Apply(PatchEngine.ReadOperations(patch, _limits), document);

    /// <summary>Applies a patch built with <see cref="YamlPatch"/>. See <see cref="ApplyPatch(string, int)"/>.</summary>
    /// <param name="patch">The patch.</param>
    /// <param name="document">The index of the document in the stream. An index outside the stream is a <see cref="YamlErrorCategory.PatchPath"/> error, as for any other path the patch cannot reach.</param>
    /// <exception cref="ArgumentNullException"><paramref name="patch"/> is <see langword="null"/>.</exception>
    /// <exception cref="YamlException">An operation failed. The document is unchanged.</exception>
    public void ApplyPatch(YamlPatch patch, int document = 0)
    {
        ArgumentNullException.ThrowIfNull(patch);
        Apply(patch.Steps, document);
    }

    private void Apply(IReadOnlyList<PatchStep> steps, int document)
    {
        DocState s = CurrentForPatch(document);
        BeginEdit(s);
        Swap(PatchEngine.Apply(s, _limits, document, steps));
    }

    /// <summary>
    /// Applies an RFC 7396 JSON Merge Patch: an object merges member by member, <c>null</c> deletes a
    /// member, and any other value (an array too) replaces what is there. The patch is atomic.
    /// Deletions in a mapping run after its other members. Merging an object into an alias is refused.
    /// </summary>
    /// <param name="patch">The patch as JSON text.</param>
    /// <param name="document">The index of the document in the stream. An index outside the stream is a <see cref="YamlErrorCategory.PatchPath"/> error, as for any other path the patch cannot reach.</param>
    /// <exception cref="ArgumentNullException"><paramref name="patch"/> is <see langword="null"/>.</exception>
    /// <exception cref="YamlException">The patch is not valid JSON, or it cannot be applied. The document is unchanged.</exception>
    /// <example>
    /// <code language="csharp">
    /// using var doc = YamlDocument.Parse("name: web\nlabels:\n  tier: frontend\n  legacy: true\n");
    /// doc.ApplyMergePatch("""{ "labels": { "legacy": null }, "replicas": 3 }""");
    /// Console.Write(doc);
    /// // name: web
    /// // labels:
    /// //   tier: frontend
    /// // replicas: 3
    /// </code>
    /// </example>
    public void ApplyMergePatch(string patch, int document = 0)
    {
        ArgumentNullException.ThrowIfNull(patch);
        Merge(PatchEngine.ParseJson(patch, _limits), document);
    }

    /// <summary>Applies an RFC 7396 JSON Merge Patch given as UTF-8 JSON. See <see cref="ApplyMergePatch(string, int)"/>.</summary>
    /// <param name="utf8Patch">The patch as JSON bytes.</param>
    /// <param name="document">The index of the document in the stream. An index outside the stream is a <see cref="YamlErrorCategory.PatchPath"/> error, as for any other path the patch cannot reach.</param>
    /// <exception cref="YamlException">The patch is not valid JSON, or it cannot be applied. The document is unchanged.</exception>
    public void ApplyMergePatch(ReadOnlySpan<byte> utf8Patch, int document = 0)
        => Merge(PatchEngine.ParseJson(utf8Patch, _limits), document);

    /// <summary>Applies an RFC 7396 JSON Merge Patch given as a JSON node tree. See <see cref="ApplyMergePatch(string, int)"/>.</summary>
    /// <param name="patch">The patch; <see langword="null"/> is the JSON value null.</param>
    /// <param name="document">The index of the document in the stream. An index outside the stream is a <see cref="YamlErrorCategory.PatchPath"/> error, as for any other path the patch cannot reach.</param>
    /// <exception cref="YamlException">The patch cannot be applied. The document is unchanged.</exception>
    public void ApplyMergePatch(JsonNode? patch, int document = 0) => Merge(patch, document);

    private void Merge(JsonNode? patch, int document)
    {
        DocState s = CurrentForPatch(document);
        BeginEdit(s);
        Swap(PatchEngine.ApplyMerge(s, _limits, document, patch));
    }

    /// <summary>Removes the entry or item a pointer names.</summary>
    /// <param name="path">An RFC 6901 pointer. The empty pointer is refused.</param>
    /// <param name="document">The index of the document in the stream. An index outside the stream throws <see cref="ArgumentOutOfRangeException"/>, as <see cref="Documents"/> does.</param>
    /// <exception cref="YamlException">The pointer names nothing, goes through an alias, or the entry holds an anchor that aliases still use. The document is unchanged.</exception>
    public void Remove(string path, int document = 0)
    {
        Pointer p = Pointer.Parse(path);
        DocState s = Current(document);
        using EditScope scope = BeginInPlaceEdit(s);
        Swap(PointerOps.Remove(s, _limits, document, p));
    }

    /// <summary>Tells whether a pointer names a value. Aliases are followed.</summary>
    /// <param name="path">An RFC 6901 pointer.</param>
    /// <param name="document">The index of the document in the stream. An index outside the stream throws <see cref="ArgumentOutOfRangeException"/>, as <see cref="Documents"/> does.</param>
    /// <returns><see langword="true"/> if there is a value.</returns>
    /// <exception cref="YamlException">The pointer is invalid or ambiguous.</exception>
    public bool Contains(string path, int document = 0)
    {
        Pointer p = Pointer.Parse(path);
        DocState s = Current(document);
        return PointerOps.Find(s, _limits, document, p) >= 0;
    }

    /// <summary>Returns the node a pointer names. Aliases are followed.</summary>
    /// <param name="path">An RFC 6901 pointer.</param>
    /// <param name="document">The index of the document in the stream. An index outside the stream throws <see cref="ArgumentOutOfRangeException"/>, as <see cref="Documents"/> does.</param>
    /// <returns>A view of the node.</returns>
    /// <exception cref="YamlException">The pointer names nothing (<see cref="YamlErrorCategory.PatchPath"/>) or is ambiguous.</exception>
    public YamlNode GetNode(string path, int document = 0)
    {
        Pointer p = Pointer.Parse(path);
        DocState s = Current(document);
        int node = PointerOps.Find(s, _limits, document, p, out int failedAt, out int failedIn);
        if (node < 0)
        {
            throw PointerOps.Unresolved(s, p, failedAt, failedIn);
        }

        ResetWalk();
        return new YamlNode(this, node, Track(), false);
    }

    /// <summary>Reads the node a pointer names as a CLR value. <see cref="object"/> gives the generic shapes of <see cref="YamlSerializer"/>: a mapping reads as a dictionary and a sequence as a list.</summary>
    /// <typeparam name="T">The type to read: a scalar type, an enum, a class, a collection or <see cref="object"/>.</typeparam>
    /// <param name="path">An RFC 6901 pointer.</param>
    /// <param name="document">The index of the document in the stream. An index outside the stream throws <see cref="ArgumentOutOfRangeException"/>, as <see cref="Documents"/> does.</param>
    /// <returns>The value.</returns>
    /// <exception cref="YamlException">The pointer names nothing, or the node is not of that type (<see cref="YamlErrorCategory.Binding"/>).</exception>
    /// <example>
    /// <code language="csharp">
    /// using var doc = YamlDocument.Parse("spec:\n  replicas: 2\n  ports: [80, 443]\n");
    /// int replicas = doc.Get&lt;int&gt;("/spec/replicas");
    /// int https = doc.Get&lt;int&gt;("/spec/ports/1");
    /// Console.WriteLine($"{replicas} {https}"); // 2 443
    /// </code>
    /// </example>
    public T Get<T>(string path, int document = 0) => GetNode(path, document).GetValue<T>();

    /// <summary>Reads the value a pointer names as a type using generated metadata. It reads whatever <see cref="Set{T}(string, T, YamlTypeInfo{T}, int)"/> can write.</summary>
    /// <typeparam name="T">The type to read.</typeparam>
    /// <param name="path">An RFC 6901 pointer.</param>
    /// <param name="typeInfo">The metadata for <typeparamref name="T"/>.</param>
    /// <param name="document">The index of the document in the stream.</param>
    /// <returns>The value.</returns>
    /// <exception cref="YamlException">The pointer names nothing, or the node does not fit the type.</exception>
    public T Get<T>(string path, YamlTypeInfo<T> typeInfo, int document = 0) => GetNode(path, document).GetValue(typeInfo);

    /// <summary>Reads the node a pointer names as a CLR value, if it is there and has that type; <see cref="object"/> reads a mapping or sequence as <see cref="Get{T}(string, int)"/> does.</summary>
    /// <typeparam name="T">The type to read: a scalar type, an enum, a class, a collection or <see cref="object"/>.</typeparam>
    /// <param name="path">An RFC 6901 pointer.</param>
    /// <param name="value">The value, or the default.</param>
    /// <param name="document">The index of the document in the stream. An index outside the stream throws <see cref="ArgumentOutOfRangeException"/>, as <see cref="Documents"/> does.</param>
    /// <returns><see langword="true"/> if the pointer names a node of that type.</returns>
    /// <exception cref="YamlException">The pointer is invalid or ambiguous.</exception>
    public bool TryGet<T>(string path, out T value, int document = 0)
    {
        Pointer p = Pointer.Parse(path);
        DocState s = Current(document);
        int node = PointerOps.Find(s, _limits, document, p);
        if (node >= 0 && (typeof(T) == typeof(object) || !NodeValues.IsLeafType(typeof(T))))
        {
            ResetWalk();
            return new YamlNode(this, node, Track(), false).TryGetValue(out value);
        }

        if (node >= 0 && NodeValues.TryGet(s, node, BindOptions, out value, out _, wantError: false))
        {
            return true;
        }

        value = default!;
        return false;
    }

    // ---- Node access used by YamlNode ----

    internal sealed class DocumentList : IReadOnlyList<YamlNode>
    {
        private readonly YamlDocument _doc;

        public DocumentList(YamlDocument doc)
        {
            _doc = doc;
        }

        public int Count => _doc.State.NoRealDocument ? 0 : _doc.State.DocCount;

        public YamlNode this[int index]
        {
            get
            {
                DocState s = _doc.State;
                int count = Count;
                if ((uint)index >= (uint)count)
                {
                    throw PointerOps.NoSuchDocument(index, count);
                }

                _doc.ResetWalk();
                return new YamlNode(_doc, s.Doc(index).Root, _doc.Track(), false);
            }
        }

        public IEnumerator<YamlNode> GetEnumerator()
        {
            for (int i = 0; i < Count; i++)
            {
                yield return this[i];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
