using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using OneYaml.Compat.YamlDotNet.Core;
using OneYaml.Documents;
using Oy = global::OneYaml;

namespace OneYaml.Compat.YamlDotNet.Internal;

// A parsed text: the lossless document plus what the compat layer needs to talk about positions.
internal sealed class Loaded : IDisposable
{
    private LineMap? _lines;

    private Loaded(Oy.YamlDocument document, string text, Oy.YamlLimits limits)
    {
        Limits = limits;
        Document = document;
        State = document.State;
        Text = text;
    }

    // The text as given. Marks are computed from it, so they stay available after the tree has been released.
    public string Text { get; }

    // The limits the text was parsed under; later work on the same text (binding, parsing again) keeps to them.
    public Oy.YamlLimits Limits { get; }

    public Oy.YamlDocument Document { get; }

    public DocState State { get; }

    public LineMap Lines => _lines ??= new LineMap(Text);

    // YamlDotNet accepts a repeated key (the last one wins), so the tree is built with duplicates allowed.
    public static Loaded Parse(string text, Oy.YamlLimits? limits = null)
    {
        limits ??= Oy.YamlLimits.Default;
        try
        {
            var options = new Oy.YamlDocumentOptions { AllowDuplicateKeys = true, Limits = limits };
            return new Loaded(Oy.YamlDocument.Parse(text, options), text, limits);
        }
        catch (Oy.YamlException ex)
        {
            throw ErrorMap.Translate(ex, null);
        }
    }

    public static Loaded Parse(TextReader reader, Oy.YamlLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        limits ??= Oy.YamlLimits.Default;
        string text;
        try
        {
            // Read as the core reads a reader: in chunks, counted in UTF-8 bytes, so an input over the limit is refused
            // whole and an endless reader is not buffered.
            text = global::OneYaml.Text.YamlInput.ReadToString(reader, limits);
        }
        catch (Oy.YamlException ex)
        {
            throw ErrorMap.Translate(ex, null);
        }

        return Parse(text, limits);
    }

    public Mark MarkAt(int offset) => Lines.MarkAt(offset);

    private List<Dictionary<string, TagPrefix>>? _tagDirectives;
    private bool _directivesScanned;

    private Dictionary<(int Document, string Raw), TagSlot>? _tagSlots;

    // The full tag of a node: its written form resolved against the %TAG directives of the document the node is in.
    public TagName TagOf(int node)
    {
        TagSlot? slot = TagSlotOf(node);
        return slot is null ? default : slot.Get();
    }

    // The tag of a node as a shared, lazily resolved slot. Resolving glues a directive's prefix to the suffix, and a
    // prefix may be as long as the input, so a node must not own that string: nodes that write the same tag in the
    // same document share one slot, and the string is built when somebody reads the tag.
    public TagSlot? TagSlotOf(int node)
    {
        ReadOnlySpan<byte> raw = State.RawTag(node);
        if (raw.IsEmpty)
        {
            return null;
        }

        if (!_directivesScanned)
        {
            _directivesScanned = true;
            if (State.Text.IndexOf("%TAG"u8) >= 0)
            {
                _tagDirectives = ScanTagDirectives(State.Text);
            }
        }

        int document = 0;
        Dictionary<string, TagPrefix>? handles = null;
        if (_tagDirectives is not null && _tagDirectives.Count == State.DocCount)
        {
            // Documents hold their nodes in one contiguous range each; find the one holding this node.
            int lo = 0;
            int hi = State.DocCount - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) >> 1;
                int root = State.Doc(mid).Root;
                if (root >= 0 && root <= node)
                {
                    lo = mid;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            document = lo;
            handles = _tagDirectives[lo];
        }

        _tagSlots ??= [];
        string text = Encoding.UTF8.GetString(raw);
        if (!_tagSlots.TryGetValue((document, text), out TagSlot? slot))
        {
            slot = new TagSlot(raw.ToArray(), handles);
            _tagSlots.Add((document, text), slot);
        }

        return slot;
    }

    // %TAG directives per document. A directive belongs to the document that the next --- opens; they are only valid at the start
    // of the stream or after a document end marker, which is what makes a line scan safe.
    private static List<Dictionary<string, TagPrefix>> ScanTagDirectives(ReadOnlySpan<byte> text)
    {
        var docs = new List<Dictionary<string, TagPrefix>>();
        var pending = new Dictionary<string, TagPrefix>(StringComparer.Ordinal);
        bool header = true;
        int pos = 0;
        while (pos < text.Length)
        {
            int eol = text[pos..].IndexOfAny((byte)'\n', (byte)'\r');
            ReadOnlySpan<byte> line = eol < 0 ? text[pos..] : text.Slice(pos, eol);
            pos = eol < 0 ? text.Length : pos + eol + 1;
            if (line.IsEmpty || line[0] == (byte)'#')
            {
                continue;
            }

            bool marker = line.Length >= 3 && (line[..3].SequenceEqual("---"u8) || line[..3].SequenceEqual("..."u8)) && (line.Length == 3 || line[3] is (byte)' ' or (byte)'\t');
            if (marker && line[0] == (byte)'.')
            {
                header = true;
                pending = new Dictionary<string, TagPrefix>(StringComparer.Ordinal);
                continue;
            }

            if (header && line[0] == (byte)'%')
            {
                string[] parts = Encoding.UTF8.GetString(line).Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3 && parts[0] == "%TAG")
                {
                    pending[parts[1]] = new TagPrefix(parts[2]);
                }

                continue;
            }

            if (header)
            {
                docs.Add(pending);
                pending = new Dictionary<string, TagPrefix>(StringComparer.Ordinal);
                header = false;
            }
            else if (marker)
            {
                docs.Add(new Dictionary<string, TagPrefix>(StringComparer.Ordinal));
            }
        }

        return docs;
    }

    // Returns the pooled tree arrays; only Text and Lines may be used afterwards.
    public void Dispose() => Document.Dispose();
}

// The prefix a %TAG directive gives a handle. It is shared by every tag written with the handle, so the work that
// depends only on the prefix (hashing it, which can cost as much as the input) is done once.
internal sealed class TagPrefix
{
    public TagPrefix(string text)
    {
        Text = text;
        State = TagText.Extend(TagText.Seed, text);
    }

    public string Text { get; }

    // The hash state after the prefix, to be continued over the suffix: a tag hashes the same whichever way it was split.
    // Computed here, once, so no reader sees a half-published value.
    public long State { get; }
}

// Hashing and comparing tags as prefix + suffix pairs, without building the joined text. The hash is polynomial modulo
// 2^61 - 1 with a base drawn at random per process, so tags cannot be crafted to collide ahead of time.
internal static class TagText
{
    private const ulong Modulus = (1UL << 61) - 1;
    private static readonly ulong Base = DrawBase();

    public const long Seed = 0;

    private static ulong DrawBase()
    {
        Span<byte> bytes = stackalloc byte[8];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return (System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes) % (Modulus - 1000)) + 500;
    }

    // Continues a hash state over more text.
    public static long Extend(long state, ReadOnlySpan<char> text)
    {
        ulong h = (ulong)state;
        foreach (char c in text)
        {
            ulong high = Math.BigMul(h, Base, out ulong low);
            ulong r = (low & Modulus) + ((low >> 61) | (high << 3));
            r += c + 1UL;
            r = (r & Modulus) + (r >> 61);
            if (r >= Modulus)
            {
                r -= Modulus;
            }

            h = r;
        }

        return (long)h;
    }

    public static int Finish(long state) => unchecked((int)state ^ (int)(state >> 32));

    public static int Hash(long state, ReadOnlySpan<char> text) => Finish(Extend(state, text));

    // Whether prefix1 + suffix1 is the same text as prefix2 + suffix2.
    public static bool Equal(ReadOnlySpan<char> prefix1, ReadOnlySpan<char> suffix1, ReadOnlySpan<char> prefix2, ReadOnlySpan<char> suffix2)
    {
        if (prefix1.Length + suffix1.Length != prefix2.Length + suffix2.Length)
        {
            return false;
        }

        if (prefix1.Length > prefix2.Length)
        {
            return Equal(prefix2, suffix2, prefix1, suffix1);
        }

        // prefix1 is the shorter: it must match the start of prefix2, and the rest of prefix2 plus suffix2 must match suffix1.
        int head = prefix1.Length;
        int tail = prefix2.Length - head;
        return prefix1.SequenceEqual(prefix2[..head])
            && suffix1[..tail].SequenceEqual(prefix2[head..])
            && suffix1[tail..].SequenceEqual(suffix2);
    }
}

// A written tag and the directives it is resolved with. The tag is kept as a prefix (shared) and a suffix: the joined
// text is built only when somebody reads the tag, and hashing and comparing never build it.
internal sealed class TagSlot
{
    private readonly TagPrefix? _prefix;
    private readonly string _suffix;
    private TagName _value;
    private bool _resolved;

    public TagSlot(byte[] raw, Dictionary<string, TagPrefix>? handles)
    {
        (_prefix, _suffix) = Tags.Split(raw, handles);
    }

    public ReadOnlySpan<char> Prefix => _prefix is null ? default : _prefix.Text;

    public ReadOnlySpan<char> Suffix => _suffix;

    // The full tag, kept: the tag of a node a caller asks for is a string the caller holds.
    public TagName Get()
    {
        if (!_resolved)
        {
            _value = new TagName(Text());
            _resolved = true;
        }

        return _value;
    }

    // The full tag text, made again each time, for the callers that use it once.
    public string Text() => _prefix is null ? _suffix : string.Concat(_prefix.Text, _suffix);

    public int Hash() => TagText.Hash(_prefix?.State ?? TagText.Seed, _suffix);

    public static bool Same(TagSlot a, TagSlot b) =>
        ReferenceEquals(a, b) || (ReferenceEquals(a._prefix, b._prefix) ? a._suffix == b._suffix : TagText.Equal(a.Prefix, a.Suffix, b.Prefix, b.Suffix));
}

// Line starts of a text, so a UTF-8 byte offset (what the engine reports) becomes a mark without rescanning the text.
internal sealed class LineMap
{
    private readonly string _text;
    private readonly List<int> _lineByte = [0];
    private readonly List<int> _lineChar = [0];
    private readonly int _byteLength;

    public LineMap(string text)
    {
        _text = text;
        int bytes = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            bytes += ByteCount(text, i);
            if (ch == '\n' || (ch == '\r' && !(i + 1 < text.Length && text[i + 1] == '\n')))
            {
                _lineByte.Add(bytes);
                _lineChar.Add(i + 1);
            }
        }

        _byteLength = bytes;
    }

    // UTF-8 length of the character at i; the low half of a surrogate pair counts for nothing, its high half for the pair.
    private static int ByteCount(string text, int i)
    {
        char ch = text[i];
        if (ch < 0x80)
        {
            return 1;
        }

        if (ch < 0x800)
        {
            return 2;
        }

        if (char.IsHighSurrogate(ch) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
        {
            return 4;
        }

        return char.IsLowSurrogate(ch) && i > 0 && char.IsHighSurrogate(text[i - 1]) ? 0 : 3;
    }

    public Mark MarkAt(int offset)
    {
        offset = Math.Clamp(offset, 0, _byteLength);
        int lo = 0;
        int hi = _lineByte.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (_lineByte[mid] <= offset)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        int bytes = _lineByte[lo];
        int chars = _lineChar[lo];
        while (bytes < offset && chars < _text.Length)
        {
            bytes += ByteCount(_text, chars);
            chars++;
        }

        // An offset after the lead of a four-byte character lands after the whole surrogate pair.
        if (chars < _text.Length && chars > 0 && char.IsLowSurrogate(_text[chars]) && char.IsHighSurrogate(_text[chars - 1]))
        {
            chars++;
        }

        return new Mark(chars, lo + 1, chars - _lineChar[lo] + 1);
    }
}

// Turns the errors of the engine into the exception types YamlDotNet code catches.
internal static class ErrorMap
{
    public static Core.YamlException Translate(Oy.YamlException error, LineMap? lines)
    {
        Mark at = lines is not null && error.Offset >= 0
            ? lines.MarkAt((int)Math.Min(error.Offset, int.MaxValue))
            : error.Line >= 1 ? new Mark(Math.Max(error.Offset, 0), error.Line, Math.Max(error.Column, 1)) : Mark.Empty;
        string message = error.Message;
        return error.Category switch
        {
            // The engine marks a refused alias expansion on the error itself; depth and size limits are not marked.
            Oy.YamlErrorCategory.Limit when error.IsAliasBudget => new AliasExpansionLimitExceededException(at, at, message),
            Oy.YamlErrorCategory.Limit => new MaximumRecursionLevelReachedException(at, at, message),
            Oy.YamlErrorCategory.UnresolvedTag => new SemanticErrorException(at, at, message),
            Oy.YamlErrorCategory.Syntax when message.Contains("alias", StringComparison.OrdinalIgnoreCase) && message.Contains("anchor", StringComparison.OrdinalIgnoreCase) => new AnchorNotFoundException(at, at, message),
            Oy.YamlErrorCategory.Syntax or Oy.YamlErrorCategory.Encoding => new SyntaxErrorException(at, at, message),
            _ => new Core.YamlException(at, at, message),
        };
    }
}
