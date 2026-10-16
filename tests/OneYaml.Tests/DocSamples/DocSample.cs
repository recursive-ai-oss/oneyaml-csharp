using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace DocSamples;

/// <summary>
/// Runs the code samples of the documentation. A sample is the text between <c>#region Name</c> and
/// <c>#endregion</c> in a file of a <c>DocSamples</c> folder; the README, the guides and the XML documentation
/// show exactly that text. The comment lines that follow a <c>Console.Write</c> statement in the sample are the
/// output the documentation promises, and <see cref="Run"/> checks that the sample prints it.
/// </summary>
internal static partial class DocSample
{
    /// <summary>The folder, under the test output, that holds the sample sources and the documents that show them.</summary>
    public const string Folder = "docsync";

    /// <summary>Runs a sample and checks what it prints against the output its comments show.</summary>
    public static void Run(string name, Action body)
    {
        string expected = ExpectedOutput(name);
        string actual = Capture(body);
        Assert.Equal(expected, actual);
    }

    /// <summary>Runs an asynchronous sample and checks what it prints against the output its comments show.</summary>
    public static async Task RunAsync(string name, Func<Task> body)
    {
        string expected = ExpectedOutput(name);
        TextWriter original = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            await body();
        }
        finally
        {
            Console.SetOut(original);
        }

        Assert.Equal(expected, Normalize(writer.ToString()));
    }

    private static string Capture(Action body)
    {
        TextWriter original = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            body();
        }
        finally
        {
            Console.SetOut(original);
        }

        return Normalize(writer.ToString());
    }

    private static string Normalize(string text) => text.ReplaceLineEndings("\n").TrimEnd('\n');

    /// <summary>The output a sample's comments promise.</summary>
    public static string ExpectedOutput(string name)
    {
        if (!Regions().TryGetValue(name, out Region? region))
        {
            throw new InvalidOperationException($"No sample region named '{name}'.");
        }

        var lines = new List<string>();
        bool inOutput = false;
        foreach (string line in region.Text.Split('\n'))
        {
            string trimmed = line.TrimStart();
            if (inOutput && (trimmed == "//" || trimmed.StartsWith("// ", StringComparison.Ordinal)))
            {
                lines.Add(trimmed.Length <= 3 ? "" : trimmed[3..]);
                continue;
            }

            inOutput = false;
            if (line.Contains("Console.Write", StringComparison.Ordinal))
            {
                inOutput = true;
                Match trailing = TrailingComment().Match(line);
                if (trailing.Success)
                {
                    lines.Add(trailing.Groups[1].Value);
                }
            }
        }

        return string.Join("\n", lines);
    }

    /// <summary>A named sample: its text, and the file it comes from.</summary>
    public sealed record Region(string Name, string File, string Text);

    private static Dictionary<string, Region>? s_regions;

    /// <summary>Every sample region of every sample file, by name.</summary>
    public static Dictionary<string, Region> Regions()
    {
        if (s_regions is not null)
        {
            return s_regions;
        }

        var parts = new Dictionary<string, (string File, List<string> Texts)>(StringComparer.Ordinal);
        foreach (string file in SampleFiles())
        {
            string rel = Path.GetRelativePath(Root, file).Replace('\\', '/');
            string[] lines = File.ReadAllText(file).ReplaceLineEndings("\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                Match start = RegionStart().Match(lines[i]);
                if (!start.Success)
                {
                    continue;
                }

                string name = start.Groups[2].Value;
                int indent = start.Groups[1].Value.Length;
                var body = new StringBuilder();
                int j = i + 1;
                for (; j < lines.Length && !RegionEnd().IsMatch(lines[j]); j++)
                {
                    string l = lines[j];
                    string head = l.Length >= indent ? l[..indent] : l;
                    if (head.Trim().Length != 0)
                    {
                        throw new InvalidOperationException($"{rel}:{j + 1}: a line of region '{name}' is indented less than its #region line.");
                    }

                    body.Append(l.Length >= indent ? l[indent..] : "").Append('\n');
                }

                if (j == lines.Length)
                {
                    throw new InvalidOperationException($"{rel}:{i + 1}: region '{name}' has no #endregion.");
                }

                if (parts.TryGetValue(name, out var existing))
                {
                    if (existing.File != rel)
                    {
                        throw new InvalidOperationException($"Region '{name}' is in both {existing.File} and {rel}.");
                    }

                    existing.Texts.Add(body.ToString().TrimEnd('\n'));
                }
                else
                {
                    parts[name] = (rel, [body.ToString().TrimEnd('\n')]);
                }

                i = j;
            }
        }

        s_regions = parts.ToDictionary(p => p.Key, p => new Region(p.Key, p.Value.File, string.Join("\n\n", p.Value.Texts)), StringComparer.Ordinal);
        return s_regions;
    }

    /// <summary>The folder the documents and samples are copied to.</summary>
    public static string Root => Path.Combine(AppContext.BaseDirectory, Folder);

    private static IEnumerable<string> SampleFiles() =>
        Directory.EnumerateFiles(Path.Combine(Root, "samples"), "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal);

    [GeneratedRegex(@"^(\s*)#region\s+(\S+)\s*$")]
    private static partial Regex RegionStart();

    [GeneratedRegex(@"^\s*#endregion\b")]
    private static partial Regex RegionEnd();

    [GeneratedRegex(@"Console\.Write.*;\s*// (.*)$")]
    private static partial Regex TrailingComment();
}

/// <summary>The samples write to the console, so they run one at a time, apart from the other tests.</summary>
[CollectionDefinition(nameof(DocSample), DisableParallelization = true)]
public sealed class DocSampleGroup;
