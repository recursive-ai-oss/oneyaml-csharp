using System;
using System.Collections.Generic;
using Oy = global::OneYaml;

namespace OneYaml.Compat.SharpYaml;

// Turns the errors of the engine into the exception types SharpYaml code catches.
internal static class ErrorMap
{
    private const string MoreThanOneDocument = "The stream holds more than one document";

    // The engine refuses a stream with several documents; SharpYaml reads the first and ignores the rest.
    public static bool IsMultiDocument(Oy.YamlException error) =>
        error.Category == Oy.YamlErrorCategory.Syntax && error.Message.StartsWith(MoreThanOneDocument, StringComparison.Ordinal);

    public static YamlException Translate(Oy.YamlException error, ReadOnlySpan<char> text, string? sourceName)
    {
        Mark at = MarkOf(error, text);
        string message = error.Message;
        return error.Category switch
        {
            Oy.YamlErrorCategory.Syntax or Oy.YamlErrorCategory.Encoding => new SyntaxErrorException(sourceName, at, at, message),
            _ => new YamlException(sourceName, at, at, message, null),
        };
    }

    // The index in the text of the place the engine names, or -1 when it names none.
    public static int CharIndexOf(Oy.YamlException error, ReadOnlySpan<char> text) =>
        error.Offset < 0 ? -1 : MarkOf(error, text).Index;

    // Marks count characters of the text and start at zero; the engine reports a UTF-8 offset.
    private static Mark MarkOf(Oy.YamlException error, ReadOnlySpan<char> text)
    {
        if (error.Offset < 0 || text.IsEmpty)
        {
            return error.Line >= 1 ? new Mark(0, error.Line - 1, Math.Max(error.Column - 1, 0)) : Mark.Empty;
        }

        long bytes = 0;
        int line = 0;
        int lineStart = 0;
        int i = 0;
        while (i < text.Length && bytes < error.Offset)
        {
            char ch = text[i];
            if (ch < 0x80)
            {
                bytes++;
            }
            else if (ch < 0x800)
            {
                bytes += 2;
            }
            else if (char.IsHighSurrogate(ch) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                bytes += 4;
                i++;
            }
            else
            {
                bytes += 3;
            }

            i++;
            if (ch == '\n' || (ch == '\r' && !(i < text.Length && text[i] == '\n')))
            {
                line++;
                lineStart = i;
            }
        }

        return new Mark(i, line, i - lineStart);
    }
}
