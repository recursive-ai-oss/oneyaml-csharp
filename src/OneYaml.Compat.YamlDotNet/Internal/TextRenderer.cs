using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using OneYaml.Compat.YamlDotNet.Core;
using OneYaml.Emitting;

namespace OneYaml.Compat.YamlDotNet.Internal;

// Writes a buffer of events as YAML text the way YamlDotNet lays it out: two-space indentation, block sequences level
// with their key, "key: value" on one line, anchors named o0, o1 and so on, and flow style where it is asked for.
// The scalar quoting rules come from the shared writer, so a string that would read back as something else is quoted.
internal sealed class TextRenderer
{
    private struct Frame
    {
        public bool IsMapping;
        public bool Flow;
        public int Indent;
        public int Count;
        public bool InlineFirst;
        public bool ExpectKey;
        public bool ExplicitKey;
    }

    private readonly Settings _settings;
    private readonly ArrayBufferWriter<byte> _out = new(1024);
    private readonly Utf8YamlWriter _w;
    private readonly Dictionary<int, string> _anchorNames = [];
    private Frame[] _frames = new Frame[16];
    private int _depth;
    private bool _lineOpen;
    private bool _pendingDocumentStart;

    public TextRenderer(Settings settings)
    {
        _settings = settings;
        _w = new Utf8YamlWriter(_out);
    }

    public ReadOnlySpan<byte> Written => _out.WrittenSpan;

    // Renders events [from, to) as one document.
    public void Document(EventBuffer buffer, int from, int to, bool explicitStart)
    {
        _depth = 0;
        _lineOpen = false;
        _anchorNames.Clear();
        // Names the caller gave stay as they are; the generated ones skip any name already taken, so an alias
        // can never resolve to a different node than the one it pointed at.
        HashSet<string> taken = [];
        for (int i = from; i < to; i++)
        {
            if (buffer[i].Anchor is { } used)
            {
                taken.Add(used);
            }
            else if (buffer[i].Kind == EvKind.Alias && buffer[i].Target < 0 && buffer[i].Text is { } unresolved)
            {
                taken.Add(unresolved);
            }
        }

        int named = 0;
        for (int i = from; i < to; i++)
        {
            if (buffer[i].NeedsAnchor)
            {
                string auto;
                if (buffer[i].Anchor is { } given0)
                {
                    auto = given0;
                }
                else
                {
                    do
                    {
                        auto = "o" + named++.ToString(CultureInfo.InvariantCulture);
                    }
                    while (taken.Contains(auto));
                }

                _anchorNames[i] = auto;
            }
            else if (buffer[i].Anchor is { } given)
            {
                _anchorNames[i] = given;
            }
        }

        _pendingDocumentStart = explicitStart;
        int index = from;
        while (index < to)
        {
            ref Ev e = ref buffer[index];
            if (e.Kind is EvKind.SequenceEnd or EvKind.MappingEnd)
            {
                Close();
                index++;
            }
            else
            {
                index = Node(buffer, index);
            }
        }

        if (_pendingDocumentStart)
        {
            Raw("--- ");
            _lineOpen = true;
        }

        EndLine();
    }

    private void Raw(string text) => _w.WriteRaw(Encoding.UTF8.GetBytes(text));

    private void EndLine()
    {
        if (_lineOpen)
        {
            _w.WriteRaw("\n"u8);
            _lineOpen = false;
        }
    }

    private void Push(Frame f)
    {
        if (_depth == _frames.Length)
        {
            Array.Resize(ref _frames, _depth * 2);
        }

        _frames[_depth++] = f;
    }

    private void Close()
    {
        if (_depth == 0)
        {
            return;
        }

        Frame f = _frames[--_depth];
        if (f.Flow)
        {
            _w.WriteByte(f.IsMapping ? (byte)'}' : (byte)']');
            _lineOpen = true;
        }
    }

    // Writes the node at i (a scalar, an alias, or the start of a collection) and returns the index to continue from.
    private int Node(EventBuffer buffer, int i)
    {
        ref Ev e = ref buffer[i];
        bool root = _depth == 0;
        Frame parent = root ? default : _frames[_depth - 1];
        bool isKey = !root && parent.IsMapping && parent.ExpectKey;
        bool collection = e.Kind is EvKind.SequenceStart or EvKind.MappingStart;
        bool isMapping = e.Kind == EvKind.MappingStart;
        bool empty = collection && i + 1 < buffer.Count && buffer[i + 1].Kind is EvKind.SequenceEnd or EvKind.MappingEnd;
        bool flow = _settings.JsonCompatible || e.Flow || (!root && parent.Flow);
        bool explicitScalarKey = isKey && e.Kind == EvKind.Scalar && NeedsExplicitKey(ref e, parent.Flow || flow);
        string? anchor = _anchorNames.TryGetValue(i, out string? name) ? name : null;
        string props = Properties(anchor, e.Tag);

        // The slot the node sits in decides what comes before it.
        int parentIndent = root ? -1 : parent.Indent;
        int contentIndent = root ? 2 : parent.Indent + 2;
        if (root)
        {
            if (_pendingDocumentStart)
            {
                _pendingDocumentStart = false;
                Raw("---");
                _lineOpen = true;
                if (collection && !empty && !flow)
                {
                    EndLine();
                }
                else
                {
                    Raw(" ");
                }
            }
            else if (e.Kind == EvKind.Scalar && string.IsNullOrEmpty(e.Text) && props.Length == 0 && !flow)
            {
                Raw("--- ");
            }
        }
        else if (parent.Flow || flow)
        {
            if (parent.Count > 0 && (!parent.IsMapping || isKey))
            {
                Raw(", ");
            }

            if (explicitScalarKey)
            {
                Raw("? ");
            }
        }
        else if (parent.IsMapping)
        {
            if (isKey)
            {
                if (!(parent.InlineFirst && parent.Count == 0))
                {
                    EndLine();
                    _w.WriteSpaces(parent.Indent);
                }

                if (collection || explicitScalarKey)
                {
                    Raw("? ");
                    _frames[_depth - 1].ExplicitKey = true;
                    parent.ExplicitKey = true;
                    flow = flow || collection;
                }
            }
            else if (parent.ExplicitKey)
            {
                EndLine();
                _w.WriteSpaces(parent.Indent);
                Raw(":");
                _frames[_depth - 1].ExplicitKey = false;
            }
        }
        else
        {
            if (!(parent.InlineFirst && parent.Count == 0))
            {
                EndLine();
                _w.WriteSpaces(parent.Indent);
            }

            Raw("-");
        }

        // The space between the slot marker ("key:" or "-") and the content. A block collection under a key starts on the next line.
        bool block = collection && !empty && !flow;
        if (!root && !isKey && !parent.Flow && !(parent.IsMapping && block && props.Length == 0))
        {
            Raw(" ");
        }

        if (props.Length > 0)
        {
            Raw(props);
        }

        if (e.Kind == EvKind.Alias)
        {
            if (props.Length > 0)
            {
                Raw(" ");
            }

            Raw("*" + (e.Target >= 0 ? _anchorNames[e.Target] : e.Text));
            After(isKey, parent, root, flow);
            return i + 1;
        }

        if (!collection)
        {
            if (props.Length > 0 && !string.IsNullOrEmpty(e.Text))
            {
                Raw(" ");
            }

            WriteScalar(ref e, parent.Flow || flow, isKey, parentIndent, contentIndent, explicitScalarKey);
            After(isKey, parent, root, flow);
            return i + 1;
        }

        if (empty)
        {
            if (props.Length > 0)
            {
                Raw(" ");
            }

            Raw(isMapping ? "{}" : "[]");
            After(isKey, parent, root, flow);
            return i + 2;
        }

        if (flow)
        {
            if (props.Length > 0)
            {
                Raw(" ");
            }

            _w.WriteByte(isMapping ? (byte)'{' : (byte)'[');
            _lineOpen = true;
            CountChild(isKey, root);
            Push(new Frame { IsMapping = isMapping, Flow = true, ExpectKey = isMapping });
            return i + 1;
        }

        int indent;
        bool inlineFirst = false;
        if (root)
        {
            indent = 0;
            if (props.Length > 0)
            {
                _lineOpen = true;
            }
        }
        else if (parent.IsMapping)
        {
            indent = !isMapping && !_settings.IndentedSequences ? parent.Indent : parent.Indent + 2;
            _lineOpen = true;
        }
        else
        {
            indent = parent.Indent + 2;
            if (props.Length > 0)
            {
                _lineOpen = true;
            }
            else
            {
                inlineFirst = true;
            }
        }

        CountChild(isKey, root);
        Push(new Frame { IsMapping = isMapping, Indent = indent, InlineFirst = inlineFirst, ExpectKey = isMapping });
        return i + 1;
    }

    private static string Properties(string? anchor, string? tag)
    {
        if (anchor is null && string.IsNullOrEmpty(tag))
        {
            return string.Empty;
        }

        string t = string.IsNullOrEmpty(tag) ? string.Empty : Tags.ToText(new TagName(tag));
        return anchor is null ? t : t.Length == 0 ? "&" + anchor : "&" + anchor + " " + t;
    }

    // After a scalar-like node: a key gets its colon, and the parent counts the child.
    private void After(bool isKey, in Frame parent, bool root, bool flow)
    {
        if (isKey && !parent.ExplicitKey)
        {
            Raw(parent.Flow || flow ? ": " : ":");
        }

        if (!_blockEnded)
        {
            _lineOpen = true;
        }

        _blockEnded = false;
        CountChild(isKey, root);
    }

    private void CountChild(bool isKey, bool root)
    {
        if (root)
        {
            return;
        }

        ref Frame parent = ref _frames[_depth - 1];
        if (parent.IsMapping)
        {
            if (isKey)
            {
                parent.ExpectKey = false;
            }
            else
            {
                parent.ExpectKey = true;
                parent.Count++;
            }
        }
        else
        {
            parent.Count++;
        }
    }

    private bool _blockEnded;

    // The style a string is asked for: the one it has, else folded for text with line breaks and plain for the rest.
    private ScalarWriteStyle PreferredStyle(byte[] bytes, string text, ScalarStyle style, bool flow, bool isKey, EmitContext context, int parentIndent, int contentIndent)
    {
        ScalarWriteStyle preferred = style switch
        {
            ScalarStyle.SingleQuoted => ScalarWriteStyle.SingleQuoted,
            ScalarStyle.DoubleQuoted => ScalarWriteStyle.DoubleQuoted,
            ScalarStyle.Literal => ScalarWriteStyle.Literal,
            ScalarStyle.Folded => ScalarWriteStyle.Folded,
            _ => text.Contains('\n', StringComparison.Ordinal) && !flow && !isKey ? ScalarWriteStyle.Folded : ScalarWriteStyle.Plain,
        };
        // With quoting of necessary strings on, the quotes a string needs are double quotes.
        if (_settings.QuoteNecessaryStrings && preferred == ScalarWriteStyle.Plain
            && Utf8YamlWriter.ChooseStyle(bytes, preferred, context, StyleMode.Dump, parentIndent, contentIndent) == ScalarWriteStyle.SingleQuoted)
        {
            preferred = ScalarWriteStyle.DoubleQuoted;
        }

        return preferred;
    }

    // Tells whether a key must be written after "? " because it is longer than an implicit key may be.
    private bool NeedsExplicitKey(ref Ev e, bool flow)
    {
        string text = e.Text ?? string.Empty;
        if (text.Length == 0 && !e.IsString)
        {
            return false;
        }

        ScalarStyle style = _settings.JsonCompatible && e.IsString ? ScalarStyle.DoubleQuoted : e.Style;
        if (text.Length <= 150)
        {
            return false;
        }

        EmitContext context = flow ? EmitContext.FlowKey : EmitContext.BlockKey;
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        ScalarWriteStyle preferred = e.IsString
            ? PreferredStyle(bytes, text, style, flow, true, context, 0, 2)
            : style switch
            {
                ScalarStyle.SingleQuoted => ScalarWriteStyle.SingleQuoted,
                ScalarStyle.DoubleQuoted => ScalarWriteStyle.DoubleQuoted,
                _ => ScalarWriteStyle.Plain,
            };
        return Utf8YamlWriter.ChooseStyle(bytes, preferred, context, StyleMode.Dump) == ScalarWriteStyle.ExplicitKeyRequired;
    }

    private void WriteScalar(ref Ev e, bool flow, bool isKey, int parentIndent, int contentIndent, bool explicitKey)
    {
        string text = e.Text ?? string.Empty;
        ScalarStyle style = e.Style;
        if (_settings.JsonCompatible && e.IsString)
        {
            style = ScalarStyle.DoubleQuoted;
        }

        if (text.Length == 0 && !e.IsString)
        {
            if (flow)
            {
                Raw("null");
            }
            else if (style != ScalarStyle.Plain)
            {
                Raw("''");
            }

            return;
        }

        EmitContext context = flow ? (isKey ? EmitContext.FlowKey : EmitContext.FlowValue) : (isKey ? EmitContext.BlockKey : EmitContext.BlockValue);
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        if (explicitKey && (e.IsString || !RawKeyReadsBack(text, style, flow)))
        {
            // A key too long to be implicit: after "? " the text may be as long as it needs to be. A key that is not a
            // string (a long number, say) is written in its raw form below: quoting it would read back as a string.
            _w.WriteExplicitKeyText(bytes, flow);
            return;
        }

        if (!e.IsString)
        {
            WriteRawScalar(bytes, text, style, flow, parentIndent, contentIndent, context);
            return;
        }

        ScalarWriteStyle preferred = PreferredStyle(bytes, text, style, flow, isKey, context, parentIndent, contentIndent);
        ScalarWriteStyle used = _w.WriteString(bytes, preferred, context, StyleMode.Dump, parentIndent, contentIndent);
        if (used is ScalarWriteStyle.Literal or ScalarWriteStyle.Folded)
        {
            _blockEnded = true;
            _lineOpen = false;
        }
    }

    // A key that is not a string (a long number, say) is written in its raw form after "? " when that form is a plain
    // scalar: quoting it would read back as a string.
    private static bool RawKeyReadsBack(string text, ScalarStyle style, bool flow) =>
        style is ScalarStyle.Any or ScalarStyle.Plain && !text.Contains('\n', StringComparison.Ordinal) && ScalarRules.IsPlainSyntax(text, flow);

    private void WriteRawScalar(byte[] bytes, string text, ScalarStyle style, bool flow, int parentIndent, int contentIndent, EmitContext context)
    {
        switch (style)
        {
            case ScalarStyle.SingleQuoted when _w.WriteSingleQuoted(bytes):
                return;
            case ScalarStyle.DoubleQuoted:
                _w.WriteDoubleQuoted(bytes);
                return;
            case ScalarStyle.Literal or ScalarStyle.Folded:
                ScalarWriteStyle used = _w.WriteString(bytes, style == ScalarStyle.Literal ? ScalarWriteStyle.Literal : ScalarWriteStyle.Folded, context, StyleMode.Dump, parentIndent, contentIndent);
                if (used is ScalarWriteStyle.Literal or ScalarWriteStyle.Folded)
                {
                    _blockEnded = true;
                    _lineOpen = false;
                }

                return;
        }

        if ((style is ScalarStyle.Any or ScalarStyle.Plain) && text.Contains('\n', StringComparison.Ordinal) && !flow)
        {
            ScalarWriteStyle used = _w.WriteString(bytes, ScalarWriteStyle.Literal, context, StyleMode.Dump, parentIndent, contentIndent);
            if (used is ScalarWriteStyle.Literal or ScalarWriteStyle.Folded)
            {
                _blockEnded = true;
                _lineOpen = false;
            }

            return;
        }

        if (ScalarRules.IsPlainSyntax(text, flow))
        {
            _w.WriteRaw(bytes);
        }
        else if (!_w.WriteSingleQuoted(bytes))
        {
            _w.WriteDoubleQuoted(bytes);
        }
    }
}
