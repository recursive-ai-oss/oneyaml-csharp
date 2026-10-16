using System;
using System.Text;

namespace OneYaml;

public sealed partial class Utf8YamlEmitter
{
    // A tag or an anchor in a name or tag is cut off here so one call cannot ask for an unbounded buffer.
    private const int MaxPropertyChars = 4096;

    private static readonly UTF8Encoding s_strictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // The tag and anchor written for the next value, ready to copy out: "!tag &anchor" with no trailing space.
    private byte[]? _props;
    private int _propsLength;
    private bool _hasTag;
    private bool _hasAnchor;

    /// <summary>
    /// Gives the next value a tag. Call it before the value: a scalar, or the start of a mapping or sequence. The tag is the
    /// full, resolved form that <see cref="Utf8YamlReader.Tag"/> returns, and is written in the shortest form that reads back
    /// as the same tag: <c>tag:yaml.org,2002:str</c> as <c>!!str</c>, a local tag such as <c>!Sub</c> as it is, and any other
    /// tag as a verbatim <c>!&lt;...&gt;</c> tag. Characters that a tag cannot hold are percent-encoded.
    /// </summary>
    /// <param name="tag">The resolved tag.</param>
    /// <exception cref="ArgumentNullException"><paramref name="tag"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="tag"/> is empty, too long, has an unpaired surrogate, or is a tag as written in a source (<c>!&lt;...&gt;</c>) rather than a resolved one.</exception>
    /// <exception cref="YamlException">The next value already has a tag.</exception>
    /// <remarks>The secondary shorthand is resolved for you: <c>!!str</c> is taken as <c>tag:yaml.org,2002:str</c>. A named handle such as
    /// <c>!e!widget</c> cannot be resolved without its <c>%TAG</c> line, so pass the resolved tag (<see cref="YamlNode.Tag"/>) instead.
    /// A tag applies to a value, not to a mapping key; writing a key after it, or ending a collection, is an error.</remarks>
    public void WriteTag(string tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        if (tag.Length == 0 || tag.Length > MaxPropertyChars)
        {
            throw new ArgumentException($"A tag must have between 1 and {MaxPropertyChars} characters.", nameof(tag));
        }

        // Shorthand forms would otherwise be percent-encoded into a different tag, silently.
        if (tag.StartsWith("!!", StringComparison.Ordinal))
        {
            if (tag.Length == 2)
            {
                throw new ArgumentException("The tag '!!' names nothing.", nameof(tag));
            }

            tag = string.Concat("tag:yaml.org,2002:", tag.AsSpan(2));
        }
        else if (tag.StartsWith("!<", StringComparison.Ordinal) && tag.EndsWith('>'))
        {
            throw new ArgumentException("The tag is written in its verbatim form; pass the resolved tag instead.", nameof(tag));
        }
        else if (IsNamedHandleShorthand(tag))
        {
            throw new ArgumentException("The tag uses a named handle, which needs its %TAG directive to resolve; pass the resolved tag instead.", nameof(tag));
        }

        if (tag.Length == 0 || tag.Length > MaxPropertyChars)
        {
            throw new ArgumentException($"A tag must have between 1 and {MaxPropertyChars} characters.", nameof(tag));
        }

        if (_hasTag)
        {
            throw Misuse("The next value already has a tag.");
        }

        byte[] utf8;
        try
        {
            utf8 = s_strictUtf8.GetBytes(tag);
        }
        catch (EncoderFallbackException)
        {
            throw new ArgumentException("The tag has an unpaired surrogate, which has no UTF-8 form.", nameof(tag));
        }

        // The anchor, when it was written first, is moved behind the tag so the tag always leads.
        byte[]? anchor = _hasAnchor ? _props.AsSpan(0, _propsLength).ToArray() : null;
        _propsLength = 0;
        const string yamlPrefix = "tag:yaml.org,2002:";
        if (tag.Length > yamlPrefix.Length && tag.StartsWith(yamlPrefix, StringComparison.Ordinal))
        {
            AppendProps("!!"u8);
            AppendEscaped(utf8.AsSpan(yamlPrefix.Length), verbatim: false);
        }
        else if (tag[0] == '!')
        {
            AppendProps("!"u8);
            AppendEscaped(utf8.AsSpan(1), verbatim: false);
        }
        else
        {
            AppendProps("!<"u8);
            AppendEscaped(utf8, verbatim: true);
            AppendProps(">"u8);
        }

        if (anchor is not null)
        {
            AppendProps(" "u8);
            AppendProps(anchor);
        }

        _hasTag = true;
    }

    // "!name!suffix": a handle declared by a %TAG directive. Handle characters are letters, digits and '-'.
    private static bool IsNamedHandleShorthand(string tag)
    {
        int close = tag.IndexOf('!', 1);
        if (close < 2)
        {
            return false;
        }

        for (int i = 1; i < close; i++)
        {
            char c = tag[i];
            if (!(char.IsAsciiLetterOrDigit(c) || c == '-'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Gives the next value an anchor, so that <see cref="WriteAlias(string)"/> can refer to it later. Call it before the value.
    /// The emitter does not check that an alias names an anchor written earlier.
    /// </summary>
    /// <param name="name">The anchor name, without the <c>&amp;</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The name is empty, too long, or has a space, a control character, a flow indicator (<c>, [ ] { }</c>) or an unpaired surrogate.</exception>
    /// <exception cref="YamlException">The next value already has an anchor.</exception>
    public void WriteAnchor(string name)
    {
        byte[] utf8 = EncodeAnchorName(name);
        if (_hasAnchor)
        {
            throw Misuse("The next value already has an anchor.");
        }

        if (_propsLength != 0)
        {
            AppendProps(" "u8);
        }

        AppendProps("&"u8);
        AppendProps(utf8);
        _hasAnchor = true;
    }

    /// <summary>Writes an alias, a reference to a value that was given an anchor earlier, as a value of its own.</summary>
    /// <param name="name">The anchor name, without the <c>*</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The name is empty, too long, or has a space, a control character, a flow indicator or an unpaired surrogate.</exception>
    /// <exception cref="YamlException">A value is not allowed here, a tag or anchor is waiting for its value (an alias takes none), or a mapping expects a key (an alias cannot be one).</exception>
    public void WriteAlias(string name)
    {
        byte[] utf8 = EncodeAnchorName(name);
        if (_propsLength != 0)
        {
            ClearProperties();
            throw Misuse("An alias cannot have a tag or an anchor.");
        }

        Place place = BeginScalarLead(out _, out Lead lead);
        if (place == Place.MapKey)
        {
            throw Misuse("An alias cannot be a mapping key.");
        }

        WriteLead(lead);
        _w.WriteByte((byte)'*');
        _w.WriteRaw(utf8);
        _w.WriteByte((byte)'\n');
        EndScalar(place);
    }

    private static byte[] EncodeAnchorName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length == 0 || name.Length > MaxPropertyChars)
        {
            throw new ArgumentException($"An anchor name must have between 1 and {MaxPropertyChars} characters.", nameof(name));
        }

        foreach (char c in name)
        {
            if (c <= ' ' || c == 0x7F || c is ',' or '[' or ']' or '{' or '}' || (c >= 0x80 && c <= 0x9F) || c == '\u2028' || c == '\u2029' || c == '\uFEFF')
            {
                throw new ArgumentException("An anchor name cannot hold a space, a control character or one of , [ ] { }.", nameof(name));
            }
        }

        try
        {
            return s_strictUtf8.GetBytes(name);
        }
        catch (EncoderFallbackException)
        {
            throw new ArgumentException("The anchor name has an unpaired surrogate, which has no UTF-8 form.", nameof(name));
        }
    }

    private void AppendProps(ReadOnlySpan<byte> bytes)
    {
        byte[] buffer = _props ??= new byte[64];
        if (_propsLength + bytes.Length > buffer.Length)
        {
            Array.Resize(ref buffer, Math.Max(buffer.Length * 2, _propsLength + bytes.Length));
            _props = buffer;
        }

        bytes.CopyTo(buffer.AsSpan(_propsLength));
        _propsLength += bytes.Length;
    }

    // Copies tag text, percent-encoding what a tag cannot hold: anything but the URI characters, and the percent sign
    // itself, because the reader decodes percent escapes and the text given here is already decoded.
    private void AppendEscaped(ReadOnlySpan<byte> utf8, bool verbatim)
    {
        ReadOnlySpan<byte> hex = "0123456789ABCDEF"u8;
        Span<byte> escape = stackalloc byte[3];
        escape[0] = (byte)'%';
        foreach (byte b in utf8)
        {
            if (IsTagByte(b, verbatim))
            {
                escape[0] = b;
                AppendProps(escape.Slice(0, 1));
                escape[0] = (byte)'%';
            }
            else
            {
                escape[1] = hex[b >> 4];
                escape[2] = hex[b & 15];
                AppendProps(escape);
            }
        }
    }

    private static bool IsTagByte(byte b, bool verbatim) =>
        b is >= (byte)'0' and <= (byte)'9' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z'
        || b is (byte)'-' or (byte)'#' or (byte)';' or (byte)'/' or (byte)'?' or (byte)':' or (byte)'@' or (byte)'&' or (byte)'='
            or (byte)'+' or (byte)'$' or (byte)'_' or (byte)'.' or (byte)'~' or (byte)'*' or (byte)'\'' or (byte)'(' or (byte)')'
        || (verbatim && b is (byte)',' or (byte)'[' or (byte)']' or (byte)'!');

    private void ClearProperties()
    {
        _propsLength = 0;
        _hasTag = false;
        _hasAnchor = false;
    }

    private YamlException PendingProperties()
    {
        ClearProperties();
        return Misuse("A tag or anchor was written and no value followed it.");
    }

    // A collection with a tag or anchor: they follow the key's colon or the dash on the same line, and the entries start on the next.
    private void WriteCollectionProperties(ref byte open)
    {
        if (open == OpenAfterKey)
        {
            _w.WriteByte((byte)' ');
        }

        _w.WriteRaw(_props.AsSpan(0, _propsLength));
        ClearProperties();
        open = OpenAfterKey;
    }

    // A scalar with a tag or anchor: they come after what leads the scalar and before its text.
    private void WriteScalarProperties(Place place, ref Lead lead)
    {
        if (place == Place.MapKey)
        {
            ClearProperties();
            throw Misuse("A tag or anchor applies to a value, not to a mapping key.");
        }

        WriteLead(lead);
        lead = default;
        _w.WriteRaw(_props.AsSpan(0, _propsLength));
        _w.WriteByte((byte)' ');
        ClearProperties();
    }
}
