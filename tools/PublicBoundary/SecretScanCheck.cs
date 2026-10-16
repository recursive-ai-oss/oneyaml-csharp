using System.Text;
using System.Text.RegularExpressions;

namespace OneYaml.PublicBoundary;

/// <summary>One secret-scan finding; never carries the matched value.</summary>
internal readonly record struct SecretFinding(string Path, string Id, string Kind)
{
    public override string ToString() => $"{Path} ({Id}): {Kind}";
}

/// <summary>
/// Scans blobs for known secret shapes, ELF binaries and core dumps, and blobs over
/// 1 MB. Only widely used key formats are recognised; values specific to a host are out of scope.
/// </summary>
internal static class SecretScanCheck
{
    public const int MaxBytes = 1_000_000;

    // The shapes are assembled from fragments so this file does not match itself.
    private static readonly (string Kind, Regex Shape)[] Shapes =
    [
        ("api key (sk- prefix)", Rx("sk-", "(?:ant-|proj-)[A-Za-z0-9_-]{20,}|sk-[A-Za-z0-9]{40,}")),
        ("aws access key id", Rx("(?:AK", "IA|AS", "IA)[0-9A-Z]{16}")),
        ("google api key", Rx("AI", "za[0-9A-Za-z_-]{35}")),
        ("github token", Rx("gh[pousr]", "_[A-Za-z0-9]{30,}|github", "_pat_[A-Za-z0-9_]{30,}")),
        ("gitlab token", Rx("gl", "pat-[A-Za-z0-9_-]{20,}")),
        ("slack token", Rx("xox[abprs]", "-[A-Za-z0-9-]{10,}")),
        ("nuget api key", Rx("oy2", "[a-z0-9]{43}")),
        ("crates.io token", Rx("ci", "o[A-Za-z0-9]{28,}")),
        ("private key", Rx("-----BEGIN ", "[A-Z ]*PRIVATE KEY-----")),
        ("secret assignment", Rx(@"\b[A-Z][A-Z0-9_]*(?:API_KEY|SECRET|PASSWORD|TOKEN)\s*=\s*['""]?[A-Za-z0-9/+_.-]{16,}")),
    ];

    private static Regex Rx(params string[] parts) =>
        new(string.Concat(parts), RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    /// <summary>Scans one blob's bytes.</summary>
    public static List<string> ScanBlob(byte[] body)
    {
        var kinds = new List<string>();
        if (body.Length > MaxBytes)
        {
            kinds.Add($"larger than 1 MB ({body.Length} bytes)");
            return kinds;
        }

        if (body.Length >= 4 && body[0] == 0x7f && body[1] == (byte)'E' && body[2] == (byte)'L' && body[3] == (byte)'F')
        {
            kinds.Add("ELF binary or core dump");
            return kinds;
        }

        // Latin-1 keeps one char per byte, so binary files scan without decoding errors.
        string text = Encoding.Latin1.GetString(body);
        foreach ((string kind, Regex shape) in Shapes)
        {
            if (shape.IsMatch(text))
            {
                kinds.Add(kind);
            }
        }

        return kinds;
    }

    /// <summary>Scans every blob reachable from a revision range (a single revision means its whole history).</summary>
    public static (IReadOnlyList<SecretFinding> Findings, int Blobs) ScanRange(string root, string range)
    {
        if (!Git.Succeeds(root, "rev-parse", "--verify", "--quiet", "HEAD"))
        {
            return ([], 0);
        }

        string[] lines = Git.Text(root, "rev-list", "--objects", range).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var paths = new Dictionary<string, string>();
        foreach (string line in lines)
        {
            int space = line.IndexOf(' ', StringComparison.Ordinal);
            string sha = space < 0 ? line : line[..space];
            paths.TryAdd(sha, space < 0 ? "?" : line[(space + 1)..]);
        }

        var findings = new List<SecretFinding>();
        int blobs = 0;
        var small = new List<string>();
        byte[] input = Encoding.ASCII.GetBytes(string.Join('\n', paths.Keys) + "\n");
        string[] checks = Encoding.UTF8.GetString(
            Git.Run(root, ["cat-file", "--batch-check=%(objectname) %(objecttype) %(objectsize)"], input))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (string c in checks)
        {
            string[] f = c.Split(' ');
            if (f.Length != 3 || f[1] != "blob")
            {
                continue;
            }

            blobs++;
            if (long.Parse(f[2], System.Globalization.CultureInfo.InvariantCulture) > MaxBytes)
            {
                findings.Add(new SecretFinding(paths[f[0]], f[0][..12], $"larger than 1 MB ({f[2]} bytes)"));
            }
            else
            {
                small.Add(f[0]);
            }
        }

        foreach (string sha in small)
        {
            foreach (string kind in ScanBlob(Git.Run(root, ["cat-file", "blob", sha])))
            {
                findings.Add(new SecretFinding(paths[sha], sha[..12], kind));
            }
        }

        return (findings, blobs);
    }

    /// <summary>Scans tracked and untracked, not-ignored files in the working tree.</summary>
    public static (IReadOnlyList<SecretFinding> Findings, int Blobs) ScanTree(string root)
    {
        var findings = new List<SecretFinding>();
        int count = 0;
        foreach (string f in PublicTextCheck.TreeFiles(root))
        {
            string full = Path.Combine(root, f);
            if (!File.Exists(full))
            {
                continue;
            }

            count++;
            foreach (string kind in ScanBlob(File.ReadAllBytes(full)))
            {
                findings.Add(new SecretFinding(f, "working tree", kind));
            }
        }

        return (findings, count);
    }
}
