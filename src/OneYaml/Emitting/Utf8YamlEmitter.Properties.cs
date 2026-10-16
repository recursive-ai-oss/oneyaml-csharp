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
    /// full, resolved form that <see cref="Utf8YamlReader.Tag"/> returns, and it is written so that it reads back as exactly that
    /// tag: <c>tag:yaml.org,2002:str</c> as <c>!!str</c>, a local tag such as <c>!Sub</c> as it is, and any other tag as a
    /// verbatim <c>!&lt;...&gt;</c> tag. Characters that a tag cannot hold are percent-encoded, so the local tag <c>!!foo</c>
    /// (written <c>!%21foo</c> in a source) is written as <c>!%21foo</c> and is not taken for <c>tag:yaml.org,2002:foo</c>.
    /// </summary>
    /// <param name="tag">The resolved tag, exactly as <see cref="Utf8YamlReader.Tag"/> or <see cref="YamlNode.Tag"/> gives it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="tag"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="tag"/> is empty, too long, or has an unpaired surrogate.</exception>
    /// <exception cref="InvalidOperationException">The next value already has a tag.</exception>
    /// <remarks>The tag is never resolved or rewritten: a shorthand such as <c>!!str</c> or <c>!e!widget</c> names a local tag
    /// here, because that is what a reader returns for it. To write the core string tag, pass its full form,
    /// <c>tag:yaml.org,2002:str</c>, not <c>!!str</c>: the text <c>!!str</c> is a local tag with two bangs and is written as <c>!%21str</c>.
    /// A tag applies to a value, not to a mapping key; writing a key after it, or ending a collection, is an error.</remarks>
    /// <example>
    /// <code language="csharp">
    /// var buffer = new ArrayBufferWriter&lt;byte&gt;();
    /// var emitter = new Utf8YamlEmitter(buffer);
    /// emitter.WriteStartMapping();
    /// emitter.WritePropertyName("id");
    /// emitter.WriteTag("tag:yaml.org,2002:str");
    /// emitter.WriteStringValue("1");
    /// emitter.WriteEndMapping();
    /// Console.Write(Encoding.UTF8.GetString(buffer.WrittenSpan));
    /// // id: !!str '1'
    /// </code>
    /// </example>
    public void WriteTag(string tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        if (tag.Length == 0 || tag.Length > MaxPropertyChars)
        {
            throw new ArgumentException($"A tag must have between 1 and {MaxPropertyChars} characters.", nameof(tag));
        }

        if (tag.Length == 0 || tag.Length > MaxPropertyChars)
        {
            throw new ArgumentException($"A tag must have between 1 and {MaxPropertyChars} characters.", nameof(tag));
        }

        if (_hasTag)
        {
            throw State("The next value already has a tag.");
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

    /// <summary>
    /// Gives the next value an anchor, so that <see cref="WriteAlias(string)"/> can refer to it later. Call it before the value.
    /// The emitter does not check that an alias names an anchor written earlier.
    /// </summary>
    /// <param name="name">The anchor name, without the <c>&amp;</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The name is empty, too long, or has a space, a control character, a flow indicator (<c>, [ ] { }</c>) or an unpaired surrogate.</exception>
    /// <exception cref="InvalidOperationException">The next value already has an anchor.</exception>
    public void WriteAnchor(string name)
    {
        byte[] utf8 = EncodeAnchorName(name);
        if (_hasAnchor)
        {
            throw State("The next value already has an anchor.");
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
    /// <exception cref="InvalidOperationException">A value is not allowed here, a tag or anchor is waiting for its value (an alias takes none), or a mapping expects a key (an alias cannot be one).</exception>
    public void WriteAlias(string name)
    {
        byte[] utf8 = EncodeAnchorName(name);
        if (_propsLength != 0)
        {
            ClearProperties();
            throw State("An alias cannot have a tag or an anchor.");
        }

        Place place = BeginScalarLead(out _, out Lead lead);
        if (place == Place.MapKey)
        {
            throw KeyState("An alias cannot be a mapping key.");
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

    private InvalidOperationException PendingProperties()
    {
        ClearProperties();
        return State("A tag or anchor was written and no value followed it.");
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
            throw KeyState("A tag or anchor applies to a value, not to a mapping key.");
        }

        WriteLead(lead);
        lead = default;
        _w.WriteRaw(_props.AsSpan(0, _propsLength));
        _w.WriteByte((byte)' ');
        ClearProperties();
    }
}
