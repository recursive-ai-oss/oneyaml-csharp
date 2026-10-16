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

    private Loaded(Oy.YamlDocument document, string text)
    {
        Document = document;
        State = document.State;
        Text = text;
    }

    // The text as given. Marks are computed from it, so they stay available after the tree has been released.
    public string Text { get; }

    public Oy.YamlDocument Document { get; }

    public DocState State { get; }

    public LineMap Lines => _lines ??= new LineMap(Text);

    // YamlDotNet accepts a repeated key (the last one wins), so the tree is built with duplicates allowed.
    private static readonly Oy.YamlDocumentOptions s_options = new() { AllowDuplicateKeys = true };

    public static Loaded Parse(string text)
    {
        try
        {
            return new Loaded(Oy.YamlDocument.Parse(text, s_options), text);
        }
        catch (Oy.YamlException ex)
        {
            throw ErrorMap.Translate(ex, null);
        }
    }

    public static Loaded Parse(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return Parse(reader.ReadToEnd());
    }

    public Mark MarkAt(int offset) => Lines.MarkAt(offset);

    private List<Dictionary<string, string>>? _tagDirectives;
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
        Dictionary<string, string>? handles = null;
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
    private static List<Dictionary<string, string>> ScanTagDirectives(ReadOnlySpan<byte> text)
    {
        var docs = new List<Dictionary<string, string>>();
        var pending = new Dictionary<string, string>(StringComparer.Ordinal);
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
                pending = new Dictionary<string, string>(StringComparer.Ordinal);
                continue;
            }

            if (header && line[0] == (byte)'%')
            {
                string[] parts = Encoding.UTF8.GetString(line).Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3 && parts[0] == "%TAG")
                {
                    pending[parts[1]] = parts[2];
                }

                continue;
            }

            if (header)
            {
                docs.Add(pending);
                pending = new Dictionary<string, string>(StringComparer.Ordinal);
                header = false;
            }
            else if (marker)
            {
                docs.Add(new Dictionary<string, string>(StringComparer.Ordinal));
            }
        }

        return docs;
    }

    // Returns the pooled tree arrays; only Text and Lines may be used afterwards.
    public void Dispose() => Document.Dispose();
}

// A written tag and the directives it is resolved with; the full tag is built once, on first use.
internal sealed class TagSlot(byte[] raw, Dictionary<string, string>? handles)
{
    private TagName _value;
    private bool _resolved;

    public TagName Get()
    {
        if (!_resolved)
        {
            _value = handles is null ? Tags.FromRaw(raw) : Tags.Resolve(raw, handles);
            _resolved = true;
        }

        return _value;
    }
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
            Oy.YamlErrorCategory.Limit => new MaximumRecursionLevelReachedException(at, at, message),
            Oy.YamlErrorCategory.UnresolvedTag => new SemanticErrorException(at, at, message),
            Oy.YamlErrorCategory.Syntax when message.Contains("alias", StringComparison.OrdinalIgnoreCase) && message.Contains("anchor", StringComparison.OrdinalIgnoreCase) => new AnchorNotFoundException(at, at, message),
            Oy.YamlErrorCategory.Syntax or Oy.YamlErrorCategory.Encoding => new SyntaxErrorException(at, at, message),
            _ => new Core.YamlException(at, at, message),
        };
    }
}
