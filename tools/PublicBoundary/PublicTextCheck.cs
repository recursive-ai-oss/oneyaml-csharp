using System.Text;
using System.Text.RegularExpressions;

namespace OneYaml.PublicBoundary;

/// <summary>One public-text match.</summary>
internal readonly record struct Finding(string Path, int Line, string Match, string Text)
{
    public override string ToString() => $"{Path}:{Line}: \"{Match}\": {Text}";
}

/// <summary>
/// Checks that files and commit messages of a public repository carry no internal
/// process text: private names and paths, internal rule, decision and work-item
/// identifiers, or commit hashes outside a provenance line. The terms are compared as
/// hashes (see <see cref="BannedTerms"/>), so the list does not publish what it guards.
/// A line that must keep a match on purpose can carry the marker "publictext:ok".
/// </summary>
internal static class PublicTextCheck
{
    // A section sign followed by a digit is a reference into a private document.
    private static readonly Regex Section = new("\u00a7[0-9]", RegexOptions.CultureInvariant);

    private static readonly Regex HashRe = new(@"\b[0-9a-f]{40}\b", RegexOptions.CultureInvariant);

    // A line that states where something came from may name a commit.
    private static readonly Regex ProvenanceRe = new(
        "revision|provenance|uses:|reverts commit", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private const string OkMarker = "publictext:ok";
    private const long MaxFileSize = 1 << 20;

    // Vendored third-party trees are not ours to edit.
    private static readonly string[] SkipContains = ["/yaml-test-suite/"];
    private static readonly string[] SkipNames = ["LICENSE-APACHE", "LICENSE-MIT", "packages.lock.json"];

    private enum Style
    {
        Other,
        CSharp,
        Hash,
        Xml,
        Prose,
    }

    private static Style StyleOf(string name)
    {
        string ext = Path.GetExtension(name).ToUpperInvariant();
        string file = Path.GetFileName(name);
        switch (ext)
        {
            case ".CS":
                return Style.CSharp;
            case ".YML" or ".YAML" or ".SH" or ".TOML" or ".EDITORCONFIG":
                return Style.Hash;
            case ".CSPROJ" or ".PROPS" or ".TARGETS" or ".SLNX" or ".XML" or ".CONFIG":
                return Style.Xml;
            case ".MD" or ".TXT":
                return Style.Prose;
        }

        if (file.StartsWith('.') || ext.Length == 0)
        {
            return Style.Hash; // dot files and scripts without an extension
        }

        return Style.Other;
    }

    // Returns the comment text of a line, tracking block comments in inBlock.
    private static string CommentPart(string line, Style style, ref bool inBlock)
    {
        switch (style)
        {
            case Style.Prose:
                return line;
            case Style.Hash:
                int hash = line.IndexOf('#', StringComparison.Ordinal);
                return hash >= 0 ? line[hash..] : string.Empty;
            case Style.CSharp:
                return BlockOrLineComment(line, "//", "/*", "*/", ref inBlock);
            case Style.Xml:
                return BlockOrLineComment(line, null, "<!--", "-->", ref inBlock);
            default:
                return string.Empty;
        }
    }

    private static string BlockOrLineComment(string line, string? lineStart, string open, string close, ref bool inBlock)
    {
        if (inBlock)
        {
            int end = line.IndexOf(close, StringComparison.Ordinal);
            if (end >= 0)
            {
                inBlock = false;
                return line[..end];
            }

            return line;
        }

        int block = line.IndexOf(open, StringComparison.Ordinal);
        int single = lineStart is null ? -1 : line.IndexOf(lineStart, StringComparison.Ordinal);
        // A // after a colon is a URL, not a comment.
        if (single > 0 && line[single - 1] == ':')
        {
            single = -1;
        }

        if (single >= 0 && (block < 0 || single < block))
        {
            return line[single..];
        }

        if (block >= 0)
        {
            string rest = line[(block + open.Length)..];
            int end = rest.IndexOf(close, StringComparison.Ordinal);
            if (end >= 0)
            {
                return rest[..end];
            }

            inBlock = true;
            return rest;
        }

        return string.Empty;
    }

    private static List<Finding> CheckText(string label, string text, Style style, bool withHash, BannedTerms terms)
    {
        var result = new List<Finding>();
        bool inBlock = false;
        string[] lines = text.Split('\n');
        for (int n = 0; n < lines.Length; n++)
        {
            string line = lines[n].TrimEnd('\r');
            string comment = CommentPart(line, style, ref inBlock);
            if (line.Contains(OkMarker, StringComparison.Ordinal))
            {
                continue;
            }

            Match section = Section.Match(line);
            string? match = terms.FindGeneral(line) ?? (section.Success ? section.Value : null) ?? terms.FindProse(comment);
            if (match is null && withHash && !ProvenanceRe.IsMatch(line))
            {
                string target = style == Style.Other ? line : comment;
                if (HashRe.IsMatch(target))
                {
                    match = "commit hash outside a provenance line";
                }
            }

            if (match is not null)
            {
                string t = line.Trim();
                result.Add(new Finding(label, n + 1, match, t.Length > 100 ? t[..100] : t));
            }
        }

        return result;
    }

    /// <summary>Checks one file's content; name is the slash-separated path from the repository root.</summary>
    public static List<Finding> CheckFile(string name, byte[] data, BannedTerms? terms = null)
    {
        string file = Path.GetFileName(name);
        string slashed = "/" + name;
        if (data.Length > MaxFileSize || Array.IndexOf(data, (byte)0) >= 0
            || Array.IndexOf(SkipNames, file) >= 0
            || SkipContains.Any(s => slashed.Contains(s, StringComparison.Ordinal)))
        {
            return [];
        }

        // Test data holds arbitrary hex, so the hash rule does not apply to it.
        bool withHash = !slashed.Contains("/testdata/", StringComparison.Ordinal);
        return CheckText(name, Encoding.UTF8.GetString(data), StyleOf(name), withHash, terms ?? BannedTerms.Default);
    }

    /// <summary>Checks a commit message; label names the commit in findings.</summary>
    public static List<Finding> CheckMessage(string label, string message, BannedTerms? terms = null) =>
        CheckText(label, message, Style.Prose, true, terms ?? BannedTerms.Default);

    public static List<Finding> CheckFiles(string root, IEnumerable<string> files)
    {
        var result = new List<Finding>();
        foreach (string f in files)
        {
            string full = Path.Combine(root, f);
            if (File.Exists(full))
            {
                result.AddRange(CheckFile(f.Replace('\\', '/'), File.ReadAllBytes(full)));
            }
        }

        return result;
    }

    /// <summary>Tracked files plus untracked, not-ignored files.</summary>
    public static IReadOnlyList<string> TreeFiles(string root) =>
        Git.SplitZ(Git.Run(root, ["ls-files", "-z", "--cached", "--others", "--exclude-standard"]))
            .Distinct().ToList();

    /// <summary>
    /// Files that differ from the merge base of <paramref name="baseRev"/> and HEAD plus
    /// untracked files. Before the first commit nothing is old, so the whole tree counts.
    /// </summary>
    public static IReadOnlyList<string> ChangedFiles(string root, string baseRev)
    {
        if (!Git.Succeeds(root, "rev-parse", "--verify", "--quiet", "HEAD"))
        {
            return TreeFiles(root);
        }

        string rev = baseRev;
        if (!Git.Succeeds(root, "rev-parse", "--verify", "--quiet", rev + "^{commit}"))
        {
            rev = "origin/" + baseRev;
            if (!Git.Succeeds(root, "rev-parse", "--verify", "--quiet", rev + "^{commit}"))
            {
                throw new InvalidOperationException($"base \"{baseRev}\" not found");
            }
        }

        string from = rev;
        if (Git.Succeeds(root, "merge-base", rev, "HEAD"))
        {
            from = Git.Text(root, "merge-base", rev, "HEAD").Trim();
        }

        var diff = Git.SplitZ(Git.Run(root, ["diff", "--name-only", "-z", "--diff-filter=d", from]));
        var others = Git.SplitZ(Git.Run(root, ["ls-files", "-z", "--others", "--exclude-standard"]));
        return diff.Concat(others).Distinct().ToList();
    }

    /// <summary>Checks the messages of the commits in a revision range such as "a..b".</summary>
    public static List<Finding> CheckCommits(string root, string range)
    {
        var result = new List<Finding>();
        if (!Git.Succeeds(root, "rev-parse", "--verify", "--quiet", "HEAD"))
        {
            return result; // no commits yet
        }

        string log = Encoding.UTF8.GetString(Git.Run(root, ["log", "--format=%H%x00%B%x00", range]));
        string[] parts = log.Split('\0');
        for (int i = 0; i + 1 < parts.Length; i += 2)
        {
            string sha = parts[i].Trim();
            if (sha.Length > 0)
            {
                result.AddRange(CheckMessage("commit " + sha[..7], parts[i + 1]));
            }
        }

        return result;
    }
}
