using System.Text;
using OneYaml.PublicBoundary;
using Xunit;

namespace OneYaml.Tools.Tests;

// The tests use a neutral word list built at run time, so this file holds no real banned term.
public class PublicTextTests
{
    private static readonly BannedTerms Terms = BannedTerms.FromPlain(
        ("s", "quasar"),
        ("w", "zebra"),
        ("w", "zebras"),
        ("h", "walrus"),
        ("n", "lot"),
        ("p", "stage"),
        ("f", "notes"),
        ("x", "BADWORD"),
        ("x", "Z9"),
        ("c", "Y7"),
        ("c", "w12"),
        ("d", "ZZ"),
        ("g", "vault/inner"),
        ("g", "/mnt/"));

    private static List<Finding> Check(string name, string content) =>
        PublicTextCheck.CheckFile(name, Encoding.UTF8.GetBytes(content), Terms);

    public static TheoryData<string, string> Leaks => new()
    {
        // A substring of a word, in any case.
        { "a.cs", "// written by the Quasar team" },
        { "a.cs", "var s = \"myquasarproject\";" },
        // A whole word, any case.
        { "a.cs", "/// The Zebra decides." },
        { "a.md", "Two zebras asked for it." },
        // A word that a hyphen before it does not excuse for the other kind.
        { "a.md", "The walrus decides." },
        // A word followed by a digit, with and without a separator.
        { "a.csproj", "<!-- the lot 3 settings -->" },
        { "a.md", "See lot-4." },
        { "a.md", "See lot5." },
        // A word, a space, a capital letter or digit.
        { "a.cs", "// stage B" },
        { "a.cs", "// Stage 2 of it" },
        // A file name.
        { "a.md", "See NOTES.md for details." },
        // Exact-case tokens and ID-shaped tokens.
        { "a.cs", "var x = BADWORD;" },
        { "a.md", "Decision Z9 applies." },
        { "a.md", "Tracked as ZZ-123." },
        // Prose-only ids: comments, Markdown and messages.
        { "a.cs", "// settled in Y7" },
        { "a.md", "Landed in w12." },
        // A path fragment and a directory name with its separators.
        { "a.cs", "var p = \"/srv/vault/inner/file\";" },
        { "a.cs", "var p = \"/mnt/user\";" },
        // A section reference and a commit hash.
        { "a.md", "See \u00a77." },
        { "a.md", "fixed in " + new string('a', 40) },
        { "a.cs", "// fixed in " + new string('b', 40) },
    };

    [Theory]
    [MemberData(nameof(Leaks))]
    public void Planted_leak_is_found(string name, string content)
    {
        Assert.NotEmpty(Check(name, content));
    }

    public static TheoryData<string, string> Clean => new()
    {
        { "a.cs", "public T1 Convert<T1, T2>(T2 value) => default!;" },
        { "a.cs", "var y7 = Y7(x);" },
        { "a.cs", "// Keep offsets, not strings: the scanner must not allocate per token." },
        // Not the whole word, a hyphen before the hyphen-excused word, or the wrong case.
        { "a.md", "A zebrafish, the User-walrus header and badword in lower case." },
        { "a.md", "The lotus and the lots; stage left; a stage one plan." },
        { "a.md", "The notes in this repository follow the notes.txt format." },
        { "a.md", "Mount points such as /mnt are plain; mntx/ and vault/outer too." },
        { "a.yml", "      - uses: actions/checkout@" + new string('c', 40) },
        { "a.cs", "var h = \"" + new string('d', 40) + "\";" },
        { "testdata/a.md", "hash " + new string('e', 40) },
        { "a.md", "Marked on purpose: quasar publictext:ok" },
    };

    [Theory]
    [MemberData(nameof(Clean))]
    public void Ordinary_text_passes(string name, string content)
    {
        Assert.Empty(Check(name, content));
    }

    [Fact]
    public void Prose_ids_are_ignored_in_code()
    {
        Assert.Empty(Check("a.cs", "var w12 = 1;"));
    }

    [Fact]
    public void Commit_message_leak_is_found()
    {
        Assert.NotEmpty(PublicTextCheck.CheckMessage("commit abc1234", "Add parser, per the quasar", Terms));
        Assert.Empty(PublicTextCheck.CheckMessage("commit abc1234", "Add the parser\n\nBody text.", Terms));
    }

    [Fact]
    public void Binary_and_license_files_are_skipped()
    {
        Assert.Empty(PublicTextCheck.CheckFile("a.bin", [0, (byte)'q', (byte)'u'], Terms));
        Assert.Empty(Check("LICENSE-MIT", "quasar"));
    }

    [Fact]
    public void List_file_round_trips_through_the_hash()
    {
        string line = "w " + BannedTerms.HashText("zebra");
        BannedTerms parsed = BannedTerms.Parse("# comment\n" + line + "\n");
        Assert.NotNull(parsed.FindGeneral("a zebra here"));
        Assert.Null(parsed.FindGeneral("a horse here"));
    }

    [Fact]
    public void Shipped_list_loads_and_leaves_ordinary_text_alone()
    {
        Assert.Null(BannedTerms.Default.FindGeneral("public sealed class YamlReader : IDisposable { }"));
        Assert.Null(BannedTerms.Default.FindProse("Keep offsets, not strings."));
    }
}
