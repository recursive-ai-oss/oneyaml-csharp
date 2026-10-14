using System.Globalization;

namespace OneYaml.PublicBoundary;

/// <summary>Entry point: <c>publictext</c> and <c>secretscan</c> sub-commands.</summary>
internal static class Program
{
    private const string Usage = """
        usage:
          PublicBoundary publictext --tree
          PublicBoundary publictext --changed [base]     (default base: main)
          PublicBoundary publictext --commits <range>
          PublicBoundary publictext --files <path>...
          PublicBoundary secretscan --tree
          PublicBoundary secretscan --range <range>
        Run from inside the repository. Exit status 1 means findings, 2 means bad usage.
        """;

    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        try
        {
            string root = Git.Text(Directory.GetCurrentDirectory(), "rev-parse", "--show-toplevel").Trim();
            return args[0] switch
            {
                "publictext" => PublicText(root, args),
                "secretscan" => SecretScan(root, args),
                _ => Fail(),
            };
        }
        catch (InvalidOperationException e)
        {
            Console.Error.WriteLine(e.Message);
            return 2;
        }
    }

    private static int Fail()
    {
        Console.Error.WriteLine(Usage);
        return 2;
    }

    private static int PublicText(string root, string[] args)
    {
        var findings = new List<Finding>();
        int scanned;
        switch (args[1])
        {
            case "--tree":
                IReadOnlyList<string> files = PublicTextCheck.TreeFiles(root);
                scanned = files.Count;
                findings.AddRange(PublicTextCheck.CheckFiles(root, files));
                break;
            case "--changed":
                IReadOnlyList<string> changed = PublicTextCheck.ChangedFiles(root, args.Length > 2 ? args[2] : "main");
                scanned = changed.Count;
                findings.AddRange(PublicTextCheck.CheckFiles(root, changed));
                break;
            case "--commits" when args.Length > 2:
                scanned = 0;
                findings.AddRange(PublicTextCheck.CheckCommits(root, args[2]));
                break;
            case "--files":
                scanned = args.Length - 2;
                findings.AddRange(PublicTextCheck.CheckFiles(root, args[2..]));
                break;
            default:
                return Fail();
        }

        foreach (Finding f in findings)
        {
            Console.Error.WriteLine(f);
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"publictext: {findings.Count} finding(s), {scanned} file(s) checked"));
        return findings.Count == 0 ? 0 : 1;
    }

    private static int SecretScan(string root, string[] args)
    {
        IReadOnlyList<SecretFinding> findings;
        int blobs;
        switch (args[1])
        {
            case "--tree":
                (findings, blobs) = SecretScanCheck.ScanTree(root);
                break;
            case "--range" when args.Length > 2:
                (findings, blobs) = SecretScanCheck.ScanRange(root, args[2]);
                break;
            default:
                return Fail();
        }

        foreach (SecretFinding f in findings)
        {
            Console.Error.WriteLine(f);
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"secretscan: {findings.Count} finding(s), {blobs} blob(s) scanned"));
        return findings.Count == 0 ? 0 : 1;
    }
}
