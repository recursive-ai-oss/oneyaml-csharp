using System;

namespace OneYaml.Compat.SharpYaml;

// SharpYaml reads the first document of a text and leaves the rest alone, errors in it included. The engine refuses a
// text with several documents, and finding that out by failing would cost a second read. So a text that has a
// document marker at the start of a line is searched for the place where its second document begins, and only the
// part before it is read. The search is lexical: a document marker at the start of a line always is one in YAML,
// even inside a quoted scalar, where it is an error.
internal static class Documents
{
    // Where the first document ends when a second follows (the length of the part to read), or -1 when the text has one document.
    public static int FirstDocumentEnd(ReadOnlySpan<char> yaml)
    {
        // The common case, a text with no marker after its first line, costs four vectorised searches.
        if (yaml.IndexOf("\n---".AsSpan()) < 0 && yaml.IndexOf("\r---".AsSpan()) < 0 && yaml.IndexOf("\n...".AsSpan()) < 0 && yaml.IndexOf("\r...".AsSpan()) < 0)
        {
            return -1;
        }

        bool started = false;
        int pos = 0;
        while (pos < yaml.Length)
        {
            int eol = yaml[pos..].IndexOfAny('\n', '\r');
            int lineEnd = eol < 0 ? yaml.Length : pos + eol;
            ReadOnlySpan<char> line = yaml[pos..lineEnd];
            int next = lineEnd;
            if (next < yaml.Length)
            {
                next += yaml[next] == '\r' && next + 1 < yaml.Length && yaml[next + 1] == '\n' ? 2 : 1;
            }

            if (IsMarker(line, "---"))
            {
                if (started)
                {
                    return BeforeDirectives(yaml, pos);
                }

                started = true;
            }
            else if (IsMarker(line, "..."))
            {
                if (started)
                {
                    return HasContent(yaml[next..]) ? next : -1;
                }
            }
            else if (!(line.IsWhiteSpace() || line[0] is '#' or '%'))
            {
                started = true;
            }

            pos = next;
        }

        return -1;
    }

    private static bool IsMarker(ReadOnlySpan<char> line, string marker) =>
        line.StartsWith(marker.AsSpan(), StringComparison.Ordinal) && (line.Length == 3 || line[3] is ' ' or '\t');

    private static bool HasContent(ReadOnlySpan<char> rest)
    {
        while (!rest.IsEmpty)
        {
            int eol = rest.IndexOfAny('\n', '\r');
            ReadOnlySpan<char> line = eol < 0 ? rest : rest[..eol];
            if (!line.IsWhiteSpace() && line[0] != '#')
            {
                return true;
            }

            rest = eol < 0 ? default : rest[(eol + 1)..];
        }

        return false;
    }

    // The directives and comments just above a document marker belong to the document it starts.
    private static int BeforeDirectives(ReadOnlySpan<char> yaml, int marker)
    {
        int cut = marker;
        int p = marker;
        while (p > 0)
        {
            int end = p - 1;
            if (end > 0 && yaml[end] == '\n' && yaml[end - 1] == '\r')
            {
                end--;
            }

            int start = yaml[..end].LastIndexOfAny('\n', '\r') + 1;
            ReadOnlySpan<char> line = yaml[start..end];
            if (!line.IsEmpty && line[0] == '%')
            {
                cut = start;
            }
            else if (!(line.IsWhiteSpace() || line[0] == '#'))
            {
                break;
            }

            p = start;
        }

        return cut;
    }
}
