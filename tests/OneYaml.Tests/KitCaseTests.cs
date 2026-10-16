using System.Text.Json;
using Xunit;

namespace OneYaml.Tests;

/// <summary>Runs the kit's parse, safety and diagnostics cases that ask for events.</summary>
public class KitCaseTests
{
    public static IEnumerable<object[]> Cases() => Corpus.Rows(Corpus.Tier(Corpus.KitEventCaseIds("parse", "safety", "diagnostics"), large: false), Corpus.KitVariable);

    public static IEnumerable<object[]> LargeCases() => Corpus.Rows(Corpus.Tier(Corpus.KitEventCaseIds("parse", "safety", "diagnostics"), large: true), Corpus.KitVariable);

    [Theory]
    [MemberData(nameof(Cases))]
    public void Case_gives_the_expected_events_or_error(string id) => Run(id);

    [Theory]
    [Trait("Category", "Slow")]
    [MemberData(nameof(LargeCases))]
    public void Large_generated_case_gives_the_expected_events_or_error(string id) => Run(id);

    private static void Run(string id)
    {
        if (id.Length == 0)
        {
            Assert.Skip($"{Corpus.KitVariable} is not set");
        }

        string dir = Corpus.KitCaseDirectory(id);
        using JsonDocument doc = Corpus.ReadJson(Path.Combine(dir, "case.json"));
        JsonElement root = doc.RootElement;
        byte[] input = KitInput.Read(dir, root);
        JsonElement expect = root.GetProperty("expect");
        string kind = expect.GetProperty("kind").GetString()!;
        switch (kind)
        {
            case "events":
                {
                    EventText.Result result = EventText.Read(input);
                    Assert.True(result.Error is null, $"unexpected error: {result.Error?.Message}");
                    Assert.Equal(ExpectedEvents(dir, expect), result.Events.ToArray());
                    break;
                }

            case "error":
                {
                    EventText.Result result = EventText.Read(input);
                    Assert.True(result.Error is not null, "an error is expected");
                    AssertError(result.Error!, expect);
                    if (expect.TryGetProperty("events_file", out JsonElement eventsFile))
                    {
                        Assert.Equal(ReadEvents(Path.Combine(dir, eventsFile.GetString()!)), result.Events.ToArray());
                    }

                    break;
                }

            case "bounded":
                Bounded(input, expect, dir);
                break;
            default:
                Assert.Fail("unknown expectation kind " + kind);
                break;
        }
    }

    // An events expectation names a file or describes the text of one with a recipe.
    private static string[] ExpectedEvents(string dir, JsonElement expectation)
    {
        if (expectation.TryGetProperty("recipe", out JsonElement recipe))
        {
            return KitInput.BuildText(recipe).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }

        return ReadEvents(Path.Combine(dir, expectation.GetProperty("file").GetString()!));
    }

    private static string[] ReadEvents(string path) => File.ReadAllText(path).Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static void AssertError(YamlException error, JsonElement expect)
    {
        string category = expect.TryGetProperty("category", out JsonElement c) ? c.GetString()! : "any";
        if (category != "any")
        {
            YamlErrorCategory want = category switch
            {
                "syntax" => YamlErrorCategory.Syntax,
                "encoding" => YamlErrorCategory.Encoding,
                "limit" => YamlErrorCategory.Limit,
                _ => throw new InvalidOperationException("category " + category + " is not a reader error"),
            };
            Assert.Equal(want, error.Category);
        }

        if (expect.TryGetProperty("line", out JsonElement line))
        {
            Assert.Equal(line.GetInt32(), error.Line);
        }

        if (expect.TryGetProperty("column", out JsonElement column))
        {
            Assert.Equal(column.GetInt32(), error.Column);
        }
    }

    private static void Bounded(byte[] input, JsonElement expect, string dir)
    {
        double megabytes = expect.GetProperty("memory_mb").GetDouble();
        long before = GC.GetAllocatedBytesForCurrentThread();
        EventText.Result result = EventText.Read(input);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated <= megabytes * 1024 * 1024, $"allocated {allocated} bytes, bound {megabytes} MB");

        if (result.Error is not null)
        {
            // The allowed errors are one category name or an array of names.
            var allowed = new List<string>();
            if (expect.TryGetProperty("error", out JsonElement e))
            {
                if (e.ValueKind == JsonValueKind.Array)
                {
                    allowed.AddRange(e.EnumerateArray().Select(x => x.GetString()!));
                }
                else
                {
                    allowed.Add(e.GetString()!);
                }
            }

            string got = result.Error.Category == YamlErrorCategory.Limit ? "limit" : result.Error.Category == YamlErrorCategory.Syntax ? "syntax" : result.Error.Category.ToString();
            Assert.True(allowed.Contains(got),
                $"error {result.Error.Category} ({result.Error.Message}) is not an allowed outcome");
            return;
        }

        Assert.True(expect.TryGetProperty("accept", out JsonElement accept), "the input is accepted but the case allows only an error");
        if (accept.GetProperty("kind").GetString() == "events")
        {
            Assert.Equal(ExpectedEvents(dir, accept), result.Events.ToArray());
        }
    }
}
