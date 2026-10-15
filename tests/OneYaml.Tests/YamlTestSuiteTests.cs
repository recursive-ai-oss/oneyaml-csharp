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
    [Theory]
    [InlineData("4H7K")]
    [InlineData("BS4K")]
    [InlineData("EB22")]
    [InlineData("MUS6/01")]
    [InlineData("Y79Y/004")]
    [InlineData("Y79Y/005")]
    [InlineData("Y79Y/006")]
    [InlineData("Y79Y/007")]
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

    // The reference reads one token further than this reader before it reports these errors, so
    // it stops earlier. The events here are the reference's, then at most the next scalar, which
    // is a real part of the input: never an event the text does not contain.
    [Theory]
    [InlineData("T833", "=VAL :1 bar")]
    [InlineData("Y79Y/008", "=VAL :key")]
    [InlineData("Y79Y/009", "=VAL :key")]
    public void A_corrected_case_reports_the_reference_events_and_at_most_one_more_scalar(string id, string extra)
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
        Assert.Equal([.. want, extra], result.Events.ToArray());
    }
}
