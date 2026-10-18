using System;
using System.Text;

namespace OneYaml;

/// <summary>
/// The error every YAML operation reports: malformed text, malformed encoding, an exceeded
/// limit, or a failed binding or patch.
/// </summary>
public class YamlException : Exception
{
    /// <summary>Creates an exception with a default message and no position.</summary>
    public YamlException()
        : this("A YAML error occurred.", YamlErrorCategory.None, 0, 0, -1, library: false)
    {
    }

    /// <summary>Creates an exception with a message and no position.</summary>
    /// <param name="message">The message.</param>
    public YamlException(string message)
        : this(message, YamlErrorCategory.None, 0, 0, -1, library: false)
    {
    }

    /// <summary>Creates an exception with a message and an inner exception.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The exception that caused this one.</param>
    public YamlException(string message, Exception? innerException)
        : this(message, YamlErrorCategory.None, 0, 0, -1, null, innerException, library: false)
    {
    }

    // Every field at once. It stays internal so a new field never forces a new public overload; callers outside
    // the library set the init properties instead.
    internal YamlException(
        string message,
        YamlErrorCategory category,
        int line,
        int column,
        long offset,
        int? operationIndex = null,
        Exception? innerException = null,
        string? path = null,
        bool library = true)
        : base(message, innerException)
    {
        _path = path;
        _category = category;
        _line = line;
        _column = column;
        _offset = offset;
        _library = library;
        OperationIndex = operationIndex;
    }

    // Whether the error is a refused alias expansion (the alias budget), as opposed to the depth or size limits.
    // The code that refuses the expansion says so with AsAliasBudget; nothing infers it from the wording.
    internal bool IsAliasBudget { get; set; }

    // Marks this error as a refused alias expansion and returns it, for `throw Error(...).AsAliasBudget()`.
    internal YamlException AsAliasBudget()
    {
        IsAliasBudget = true;
        return this;
    }

    // Whether the library made this exception, with its position already worked out. One made by the public
    // constructors, in a converter for example, has none until the serializer fills it in.
    private readonly bool _library;
    private YamlErrorCategory _category;
    private int _line;
    private int _column;
    private long _offset;
    private string? _path;
    private string? _located;

    /// <summary>
    /// The kind of error. The public constructors leave it <see cref="YamlErrorCategory.None"/>; when the serializer
    /// catches such an exception from a converter it reports <see cref="YamlErrorCategory.Binding"/>.
    /// </summary>
    public YamlErrorCategory Category { get => _category; init => _category = value; }

    /// <summary>
    /// The 1-based line of the error, counting LF, CRLF and a lone CR as one break each, or 0 when
    /// the error has no position.
    /// </summary>
    public int Line { get => _line; init => _line = value; }

    /// <summary>The 1-based column of the error in decoded characters (a byte order mark is not counted), or 0.</summary>
    public int Column { get => _column; init => _column = value; }

    /// <summary>
    /// The byte offset of the error, or -1 when unknown. For text errors it counts bytes of the UTF-8
    /// text the parser read; for a malformed UTF-16 or UTF-32 input it counts bytes of the original input.
    /// </summary>
    public long Offset { get => _offset; init => _offset = value; }

    /// <summary>
    /// A JSON Pointer (RFC 6901) to the node the error is about, such as <c>/endpoints/1/weight</c>; the
    /// empty string is the root. It is <see langword="null"/> when the error has no node, for example a
    /// syntax error or an unreadable encoding, or when the node is under a mapping key that is a collection
    /// or an alias, which has no name to write.
    /// </summary>
    public string? Path { get => _path; init => _path = value; }

    /// <summary>
    /// The name of the input given in <see cref="YamlSerializerOptions.SourceName"/> or <see cref="YamlDocumentOptions.SourceName"/>,
    /// or <see langword="null"/>. It prefixes <see cref="Message"/>.
    /// </summary>
    public string? SourceName { get => _sourceName; init => _sourceName = value; }

    private string? _sourceName;

    /// <summary>
    /// A message that stands on its own: the source name when one is set, what went wrong, and the
    /// line, column and path when they are known. Text taken from the input (values, keys, paths) is cut
    /// to a short excerpt with control characters escaped; <see cref="Path"/> holds the full pointer.
    /// </summary>
    public override string Message => _sourceName is null ? _located ?? base.Message : _sourceName + ": " + (_located ?? base.Message);

    /// <summary>For a patch, the zero-based index of the operation that failed; otherwise <see langword="null"/>.</summary>
    public int? OperationIndex { get; init; }

    // The position is worked out here, when the error is built, so reading never tracks columns.
    internal static YamlException At(YamlErrorCategory category, string message, ReadOnlySpan<byte> utf8, int offset, string? path = null) =>
        Build(category, message, utf8, offset, path, describe: false);

    // Like At, with the line, column and path also in the message text, so a log that keeps only the message says where.
    internal static YamlException Located(YamlErrorCategory category, string message, ReadOnlySpan<byte> utf8, int offset, string? path = null) =>
        Build(category, message, utf8, offset, path, describe: true);

    private static YamlException Build(YamlErrorCategory category, string message, ReadOnlySpan<byte> utf8, int offset, string? path, bool describe)
    {
        if (offset < 0)
        {
            offset = 0;
        }

        if (offset > utf8.Length)
        {
            offset = utf8.Length;
        }

        ReadOnlySpan<byte> before = utf8[..offset];
        int line = 1;
        int lineStart = 0;
        for (int i = 0; i < before.Length; i++)
        {
            byte b = before[i];
            if (b == (byte)'\n')
            {
                line++;
                lineStart = i + 1;
            }
            else if (b == (byte)'\r')
            {
                bool crlf = i + 1 < utf8.Length && utf8[i + 1] == (byte)'\n';
                if (!crlf)
                {
                    line++;
                    lineStart = i + 1;
                }
            }
        }

        // A byte order mark at the start of a line is not counted.
        if (lineStart + 2 < utf8.Length && utf8[lineStart] == 0xEF && utf8[lineStart + 1] == 0xBB && utf8[lineStart + 2] == 0xBF)
        {
            lineStart += 3;
        }

        int column = 1;
        for (int i = lineStart; i < before.Length; i++)
        {
            // Count characters, not bytes: skip UTF-8 continuation bytes.
            if ((before[i] & 0xC0) != 0x80)
            {
                column++;
            }
        }

        return new YamlException(describe ? Describe(message, line, column, path) : message, category, line, column, offset, path: path);
    }

    // An error about the key that starts at offset. The template holds {0} where the key goes; the key is the
    // last step of the path, which is only worked out here, when the error is built.
    internal static YamlException AtKey(YamlErrorCategory category, string template, ReadOnlySpan<byte> utf8, int offset)
    {
        string? path = ErrorPath.Find(utf8, offset, atCollection: false);
        string key = string.Empty;
        if (path is { Length: > 0 })
        {
            key = Excerpt(path[(path.LastIndexOf('/') + 1)..].Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal), 40);
        }

        return Located(category, template.Replace("{0}", key, StringComparison.Ordinal), utf8, offset, path);
    }

    // How much input text a message may carry; the full text stays in Path.
    internal const int MessageExcerpt = 200;

    // Input text as it may appear in a message: cut to max characters, with control characters (line breaks
    // included) escaped so a hostile key cannot forge log lines.
    internal static string Excerpt(string text, int max)
    {
        bool cut = text.Length > max;
        int length = cut ? max : text.Length;
        if (cut && length > 0 && char.IsHighSurrogate(text[length - 1]))
        {
            length--;
        }

        var sb = new StringBuilder(length + 8);
        for (int i = 0; i < length; i++)
        {
            char c = text[i];
            if (c < ' ' || (c >= '\u007F' && c <= '\u009F') || c is '\u2028' or '\u2029')
            {
                sb.Append(c switch
                {
                    '\n' => "\\n",
                    '\r' => "\\r",
                    '\t' => "\\t",
                    _ => "\\u" + ((int)c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture),
                });
            }
            else
            {
                sb.Append(c);
            }
        }

        if (cut)
        {
            sb.Append('\u2026');
        }

        return sb.ToString();
    }

    // Puts the position in the text so a log line that keeps only the message still says where.
    private static string Describe(string message, int line, int column, string? path)
    {
        return path is { Length: > 0 }
            ? $"{message} (line {line}, column {column}, path {Excerpt(path, MessageExcerpt)})"
            : $"{message} (line {line}, column {column})";
    }

    // Gives an exception that a converter threw the position of the reader, as System.Text.Json does for a JsonException.
    // Used in an exception filter, so it never catches. An exception that has a position, a path or a library origin is left alone.
    internal bool Locate(ReadOnlySpan<byte> utf8, int offset, bool atCollection, bool quiet)
    {
        if (_library || _line != 0 || _path is not null)
        {
            return false;
        }

        if (_category == YamlErrorCategory.None)
        {
            _category = YamlErrorCategory.Binding;
        }

        if (quiet)
        {
            // A try-read never shows the error, so the line and path lookup is skipped.
            _offset = offset;
            return false;
        }

        YamlException at = Located(_category, base.Message, utf8, offset, ErrorPath.Find(utf8, offset, atCollection));
        _line = at._line;
        _column = at._column;
        _offset = at._offset;
        _path = at._path;
        _located = at.Message;
        return false;
    }

    // Marks an exception with the input's name while it passes; used in an exception filter, so it never catches.
    internal static bool Tag(YamlException e, string? sourceName)
    {
        if (sourceName is not null && e._sourceName is null)
        {
            e._sourceName = sourceName;
        }

        return false;
    }

    // The error is at the end of a valid UTF-8 prefix of the text (the text after it is not available). Line and column
    // are what the char overload gives for the same place: a line break ends a line, and a column counts code points.
    internal static YamlException LocatedAtEnd(YamlErrorCategory category, string message, ReadOnlySpan<byte> prefix)
    {
        int line = 1;
        int lineStart = 0;
        for (int i = 0; i < prefix.Length; i++)
        {
            byte b = prefix[i];
            if (b == (byte)'\n' || (b == (byte)'\r' && !(i + 1 < prefix.Length && prefix[i + 1] == (byte)'\n')))
            {
                line++;
                lineStart = i + 1;
            }
        }

        int column = 1;
        for (int i = lineStart; i < prefix.Length; i++)
        {
            if ((prefix[i] & 0xC0) != 0x80)
            {
                column++;
            }
        }

        return new YamlException(Describe(message, line, column, null), category, line, column, prefix.Length);
    }

    internal static YamlException Located(YamlErrorCategory category, string message, ReadOnlySpan<char> text, int charIndex)
    {
        if (charIndex < 0)
        {
            charIndex = 0;
        }

        if (charIndex > text.Length)
        {
            charIndex = text.Length;
        }

        int line = 1;
        int lineStart = 0;
        for (int i = 0; i < charIndex; i++)
        {
            char c = text[i];
            if (c == '\n' || (c == '\r' && !(i + 1 < text.Length && text[i + 1] == '\n')))
            {
                line++;
                lineStart = i + 1;
            }
        }

        int column = 1;
        for (int i = lineStart; i < charIndex; i++)
        {
            if (!char.IsLowSurrogate(text[i]))
            {
                column++;
            }
        }

        return new YamlException(Describe(message, line, column, null), category, line, column, Encoding.UTF8.GetByteCount(text[..charIndex]));
    }
}
