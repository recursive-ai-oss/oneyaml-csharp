using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace DocSamples;

/// <summary>
/// Keeps the documentation and its samples in step. Every C# block in the Markdown documents is a sample region
/// (named by the <c>&lt;!-- sample: Name --&gt;</c> line before it), every XML documentation example is one too,
/// every region is shown somewhere, relative links point at files and headings that exist, and the markers of the
/// generated blocks are well formed.
/// </summary>
[Collection(nameof(DocSample))]
public partial class DocSyncTests
{
    private static readonly string[] GeneratedKinds = ["bench-hero", "bench-full", "conformance-line", "conformance-deviations"];

    private static string DocsRoot => Path.Combine(DocSample.Root, "repo");

    private static IEnumerable<string> MarkdownFiles() =>
        Directory.EnumerateFiles(DocsRoot, "*.md", SearchOption.AllDirectories).Order(StringComparer.Ordinal);

    private static string Rel(string path) => Path.GetRelativePath(DocsRoot, path).Replace('\\', '/');

    [Fact]
    public void Documents_are_present()
    {
        string[] expected = ["README.md", "CHANGELOG.md", "CONTRIBUTING.md", "SECURITY.md", "docs/editing.md", "src/OneYaml/README.md"];
        string[] found = MarkdownFiles().Select(Rel).ToArray();
        Assert.All(expected, e => Assert.Contains(e, found));
    }

    [Fact]
    public void Every_csharp_block_is_a_sample()
    {
        var errors = new List<string>();
        foreach (string file in MarkdownFiles())
        {
            foreach (CodeBlock block in CodeBlocks(file))
            {
                if (!IsCSharp(block.Language))
                {
                    continue;
                }

                if (block.Sample is null)
                {
                    errors.Add($"{Rel(file)}:{block.Line}: a C# block needs a '<!-- sample: Name -->' line before it.");
                }
                else if (!DocSample.Regions().TryGetValue(block.Sample, out DocSample.Region? region))
                {
                    errors.Add($"{Rel(file)}:{block.Line}: no sample region named '{block.Sample}'.");
                }
                else if (region.Text != block.Text)
                {
                    errors.Add($"{Rel(file)}:{block.Line}: the block differs from region '{block.Sample}' in {region.File}.\n--- document\n{block.Text}\n--- region\n{region.Text}");
                }
            }
        }

        Assert.True(errors.Count == 0, string.Join("\n\n", errors));
    }

    [Fact]
    public void Every_xml_example_is_a_sample()
    {
        var regions = DocSample.Regions().Values.ToLookup(r => r.Text);
        var errors = new List<string>();
        int examples = 0;
        foreach (string xml in Directory.EnumerateFiles(AppContext.BaseDirectory, "OneYaml*.xml"))
        {
            XDocument doc = XDocument.Load(xml);
            foreach (XElement member in doc.Descendants("member"))
            {
                foreach (XElement code in member.Elements("example").Elements("code"))
                {
                    examples++;
                    string text = Dedent(code.Value);
                    if (!regions.Contains(text))
                    {
                        errors.Add($"{Path.GetFileName(xml)} {member.Attribute("name")?.Value}: the example matches no sample region:\n{text}");
                    }
                }
            }
        }

        Assert.True(examples > 0, "No XML documentation examples were found next to the tests.");
        Assert.True(errors.Count == 0, string.Join("\n\n", errors));
    }

    [Fact]
    public void Every_sample_is_shown()
    {
        var shown = new HashSet<string>(StringComparer.Ordinal);
        foreach (string file in MarkdownFiles())
        {
            foreach (CodeBlock block in CodeBlocks(file))
            {
                if (block.Sample is not null)
                {
                    shown.Add(block.Sample);
                }
            }
        }

        var xmlTexts = new HashSet<string>(StringComparer.Ordinal);
        foreach (string xml in Directory.EnumerateFiles(AppContext.BaseDirectory, "OneYaml*.xml"))
        {
            foreach (XElement code in XDocument.Load(xml).Descendants("example").Elements("code"))
            {
                xmlTexts.Add(Dedent(code.Value));
            }
        }

        string[] unused = DocSample.Regions().Values
            .Where(r => !shown.Contains(r.Name) && !xmlTexts.Contains(r.Text))
            .Select(r => $"{r.File}: region '{r.Name}'")
            .ToArray();
        Assert.True(unused.Length == 0, "Sample regions no document shows:\n" + string.Join("\n", unused));
    }

    [Fact]
    public void Relative_links_resolve()
    {
        string[] manifest = File.ReadAllLines(Path.Combine(DocSample.Root, "manifest.txt"))
            .Select(l => l.Replace('\\', '/').Trim())
            .Where(l => l.Length > 0)
            .ToArray();
        var files = new HashSet<string>(manifest, StringComparer.Ordinal);
        var errors = new List<string>();
        foreach (string file in MarkdownFiles())
        {
            string rel = Rel(file);
            string dir = Path.GetDirectoryName(rel)!.Replace('\\', '/');
            string text = WithoutCode(File.ReadAllText(file).ReplaceLineEndings("\n"));
            foreach (Match m in Link().Matches(text))
            {
                string target = m.Groups[1].Value;
                if (target.Contains("://", StringComparison.Ordinal) || target.StartsWith("mailto:", StringComparison.Ordinal))
                {
                    continue;
                }

                string pathPart = target;
                string? anchor = null;
                int hash = target.IndexOf('#', StringComparison.Ordinal);
                if (hash >= 0)
                {
                    pathPart = target[..hash];
                    anchor = target[(hash + 1)..];
                }

                string resolved = pathPart.Length == 0 ? rel : Normalize(dir.Length == 0 ? pathPart : dir + "/" + pathPart);
                bool isDir = files.Any(f => f.StartsWith(resolved.TrimEnd('/') + "/", StringComparison.Ordinal));
                if (!files.Contains(resolved) && !isDir)
                {
                    errors.Add($"{rel}: link '{target}' points at '{resolved}', which is not in the repository.");
                    continue;
                }

                if (anchor is not null)
                {
                    string targetFile = Path.Combine(DocsRoot, resolved);
                    if (!resolved.EndsWith(".md", StringComparison.Ordinal) || !File.Exists(targetFile))
                    {
                        errors.Add($"{rel}: link '{target}' has an anchor into a file that is not a checked Markdown document.");
                    }
                    else if (!Anchors(targetFile).Contains(anchor))
                    {
                        errors.Add($"{rel}: link '{target}': '{resolved}' has no heading with the anchor '#{anchor}'.");
                    }
                }
            }
        }

        Assert.True(errors.Count == 0, string.Join("\n", errors));
    }

    [Fact]
    public void Generated_blocks_are_well_formed()
    {
        var errors = new List<string>();
        foreach (string file in MarkdownFiles())
        {
            string? open = null;
            int lineNo = 0;
            foreach (string line in File.ReadAllText(file).ReplaceLineEndings("\n").Split('\n'))
            {
                lineNo++;
                Match begin = GeneratedBegin().Match(line);
                if (begin.Success)
                {
                    string kind = begin.Groups[1].Value;
                    if (open is not null)
                    {
                        errors.Add($"{Rel(file)}:{lineNo}: block '{kind}' starts inside block '{open}'.");
                    }

                    if (!GeneratedKinds.Contains(kind))
                    {
                        errors.Add($"{Rel(file)}:{lineNo}: unknown generated block '{kind}'.");
                    }

                    open = kind;
                }
                else if (line.Trim() == "<!-- end generated -->")
                {
                    if (open is null)
                    {
                        errors.Add($"{Rel(file)}:{lineNo}: an end marker without a block.");
                    }

                    open = null;
                }
            }

            if (open is not null)
            {
                errors.Add($"{Rel(file)}: block '{open}' is not closed.");
            }
        }

        Assert.True(errors.Count == 0, string.Join("\n", errors));
    }

    private static bool IsCSharp(string language) => language is "csharp" or "cs" or "c#";

    private sealed record CodeBlock(int Line, string Language, string? Sample, string Text);

    private static List<CodeBlock> CodeBlocks(string file)
    {
        string[] lines = File.ReadAllText(file).ReplaceLineEndings("\n").Split('\n');
        var blocks = new List<CodeBlock>();
        for (int i = 0; i < lines.Length; i++)
        {
            Match fence = FenceStart().Match(lines[i]);
            if (!fence.Success)
            {
                continue;
            }

            string marker = fence.Groups[1].Value;
            string language = fence.Groups[2].Value.Trim();
            int start = i;
            var body = new StringBuilder();
            i++;
            while (i < lines.Length && lines[i].TrimEnd() != marker)
            {
                body.Append(lines[i]).Append('\n');
                i++;
            }

            string? sample = null;
            for (int k = start - 1; k >= 0; k--)
            {
                if (lines[k].Trim().Length == 0)
                {
                    continue;
                }

                Match m = SampleMarker().Match(lines[k]);
                sample = m.Success ? m.Groups[1].Value : null;
                break;
            }

            blocks.Add(new CodeBlock(start + 1, language, sample, body.ToString().TrimEnd('\n')));
        }

        return blocks;
    }

    // Code blocks and code spans can hold text that looks like a link; links are only looked for outside them.
    private static string WithoutCode(string text)
    {
        var result = new StringBuilder();
        bool inFence = false;
        foreach (string line in text.Split('\n'))
        {
            if (FenceStart().IsMatch(line) || (inFence && line.TrimStart().StartsWith("```", StringComparison.Ordinal)))
            {
                inFence = !inFence;
                result.Append('\n');
                continue;
            }

            result.Append(inFence ? "" : CodeSpan().Replace(line, "")).Append('\n');
        }

        return result.ToString();
    }

    private static string Normalize(string path)
    {
        var parts = new List<string>();
        foreach (string p in path.Split('/'))
        {
            if (p == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }
            }
            else if (p != "." && p.Length > 0)
            {
                parts.Add(p);
            }
        }

        return string.Join("/", parts);
    }

    // GitHub's heading anchors: lower case, punctuation dropped, spaces to hyphens, repeats numbered.
    private static HashSet<string> Anchors(string file)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var anchors = new HashSet<string>(StringComparer.Ordinal);
        bool inFence = false;
        foreach (string line in File.ReadAllText(file).ReplaceLineEndings("\n").Split('\n'))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            Match h = Heading().Match(line);
            if (inFence || !h.Success)
            {
                continue;
            }

            string text = LinkText().Replace(h.Groups[1].Value, "$1").ToLowerInvariant();
            string slug = NotSlug().Replace(text, "").Replace(' ', '-');
            int n = seen.TryGetValue(slug, out int c) ? c + 1 : 0;
            seen[slug] = n;
            anchors.Add(n == 0 ? slug : $"{slug}-{n}");
        }

        return anchors;
    }

    internal static string Dedent(string code)
    {
        string[] lines = code.ReplaceLineEndings("\n").Split('\n');
        int first = 0;
        while (first < lines.Length && lines[first].Trim().Length == 0)
        {
            first++;
        }

        int last = lines.Length - 1;
        while (last >= first && lines[last].Trim().Length == 0)
        {
            last--;
        }

        string[] body = lines[first..(last + 1)];
        int indent = body.Where(l => l.Trim().Length > 0).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
        return string.Join("\n", body.Select(l => (l.Length >= indent ? l[indent..] : l.TrimStart()).TrimEnd()));
    }

    [GeneratedRegex(@"^(```+)\s*([^`]*)$")]
    private static partial Regex FenceStart();

    [GeneratedRegex(@"^\s*<!--\s*sample:\s*(\S+)\s*-->\s*$")]
    private static partial Regex SampleMarker();

    [GeneratedRegex(@"^\s*<!--\s*generated:(\S+)\s*-->\s*$")]
    private static partial Regex GeneratedBegin();

    [GeneratedRegex(@"\]\(([^)\s]+)\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"`[^`]*`")]
    private static partial Regex CodeSpan();

    [GeneratedRegex(@"^#{1,6}\s+(.*?)\s*#*\s*$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex LinkText();

    [GeneratedRegex(@"[^\p{L}\p{Mn}\p{Nd}\p{Pc} -]")]
    private static partial Regex NotSlug();
}
