using System.Text;
using OneYaml.PublicBoundary;
using Xunit;

namespace OneYaml.Tools.Tests;

// Every planted hit is assembled from fragments, so this file passes the check it tests.
public class PublicTextTests
{
    private static List<Finding> Check(string name, string content) =>
        PublicTextCheck.CheckFile(name, Encoding.UTF8.GetBytes(content));

    public static TheoryData<string, string> Leaks => new()
    {
        { "a.cs", "// written by " + "al" + "era" },
        { "a.cs", "var p = \"/x/" + "al" + "era/file\";" },
        { "a.cs", "var p = \"/ho" + "me/user\";" },
        { "a.cs", "/// The " + "age" + "nt decides." },
        { "a.md", "The " + "archi" + "tect asked for it." },
        { "a.md", "A " + "revi" + "ewer found it." },
        { "a.md", "As in the " + "rul" + "ing." },
        { "a.md", "See " + "PL" + "AN.md for details." },
        { "a.md", "See \u00a7" + "7." },
        { "a.md", "Decision " + "Q" + "6 applies." },
        { "a.md", "Requirement " + "R" + "3 applies." },
        { "a.md", "Done in " + "L" + "2." },
        { "a.md", "Gate " + "T" + "1." },
        { "a.md", "Decision " + "D" + "4." },
        { "a.md", "Landed in " + "c" + "0." },
        { "a.cs", "// from " + "c" + "12 onwards" },
        { "a.cs", "// " + "phase" + " B" },
        { "a.csproj", "<!-- the " + "unit" + " 3 settings -->" },
        { "a.yml", "# built by the " + "age" + "nt" },
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
        { "a.cs", "var c0 = L2(x);" },
        { "a.cs", "// Keep offsets, not strings: the scanner must not allocate per token." },
        { "a.cs", "client.DefaultRequestHeaders.Add(\"User-Agent\", \"x\");" },
        { "a.md", "The standards in this repository follow the plan of the spec." },
        { "a.yml", "      - uses: actions/checkout@" + new string('c', 40) },
        { "a.cs", "var h = \"" + new string('d', 40) + "\";" },
        { "testdata/a.md", "hash " + new string('e', 40) },
        { "a.md", "Marked on purpose: " + "al" + "era publictext:" + "ok" },
    };

    [Theory]
    [MemberData(nameof(Clean))]
    public void Ordinary_text_passes(string name, string content)
    {
        Assert.Empty(Check(name, content));
    }

    [Fact]
    public void Commit_message_leak_is_found()
    {
        Assert.NotEmpty(PublicTextCheck.CheckMessage("commit abc1234", "Add parser, per the " + "rul" + "ing"));
        Assert.Empty(PublicTextCheck.CheckMessage("commit abc1234", "Add the parser\n\nBody text."));
    }

    [Fact]
    public void Binary_and_license_files_are_skipped()
    {
        Assert.Empty(PublicTextCheck.CheckFile("a.bin", [0, (byte)'a', (byte)'l']));
        Assert.Empty(Check("LICENSE-MIT", "al" + "era"));
    }
}
