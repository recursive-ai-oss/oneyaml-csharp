using System.Text.Json;
using Xunit;

namespace OneYaml.Tests;

/// <summary>Runs every yaml-test-suite case through the reader.</summary>
public class YamlTestSuiteTests
{
    public static IEnumerable<object[]> Cases() => Corpus.Rows(Corpus.SuiteCaseIds());

    [Theory]
    [MemberData(nameof(Cases))]
    public void Case_is_accepted_or_rejected_and_events_match(string id)
    {
        if (id.Length == 0)
        {
            Assert.Skip($"{Corpus.SuiteVariable} is not set");
        }

        string dir = Path.Combine(Corpus.SuiteDirectory!, id.Replace('/', Path.DirectorySeparatorChar));
        byte[] input = File.ReadAllBytes(Path.Combine(dir, "in.yaml"));
        bool expectError = File.Exists(Path.Combine(dir, "error"));
        EventText.Result result = EventText.Read(input);
        if (expectError)
        {
            Assert.True(result.Error is not null, "the suite expects this input to be rejected");
            return;
        }

        Assert.True(result.Error is null, $"the suite expects this input to be accepted, but: {result.Error?.Message} at {result.Error?.Line}:{result.Error?.Column}");
        string[] expected = File.ReadAllText(Path.Combine(dir, "test.event")).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(expected, result.Events.ToArray());
    }

    // A document ends only at its end marker, the next start marker or the end of the stream, so
    // an error found while it is open comes without a document end event (the kit's corrections).
    // Other corrected cases (T833 and Y79Y/004 to 009) differ only in how far the reader looked
    // ahead before it reported the error; they are rejected, which the test above checks.
    [Theory]
    [InlineData("4H7K")]
    [InlineData("BS4K")]
    [InlineData("EB22")]
    [InlineData("MUS6/01")]
    public void A_document_is_not_ended_before_an_error(string id)
    {
        if (Corpus.SuiteDirectory is null)
        {
            Assert.Skip($"{Corpus.SuiteVariable} is not set");
        }

        string corrections = Path.Combine(Corpus.SuiteDirectory!, "..", "corrections.json");
        using JsonDocument doc = Corpus.ReadJson(corrections);
        JsonElement entry = doc.RootElement.GetProperty("entries").EnumerateArray()
            .First(e => e.GetProperty("case").GetString() == "yts:" + id);
        string[] want = entry.GetProperty("events").EnumerateArray().Select(e => e.GetString()!).ToArray();
        string dir = Path.Combine(Corpus.SuiteDirectory!, id.Replace('/', Path.DirectorySeparatorChar));
        EventText.Result result = EventText.Read(File.ReadAllBytes(Path.Combine(dir, "in.yaml")));
        Assert.NotNull(result.Error);
        Assert.Equal(want, result.Events.ToArray());
    }
}
