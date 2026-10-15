using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OneYaml.Schema;
using Xunit;

namespace OneYaml.Tests.Schema;

/// <summary>
/// Reads the language-neutral schema cases from a checkout of the shared conformance kit
/// (the ONEYAML_KIT environment variable) and checks each plain-scalar expectation, so the
/// tables in <see cref="CoreSchemaTests"/> cannot drift from the kit. Skipped when the variable is unset.
/// </summary>
public sealed partial class CoreAgainstKitFilesTests
{
    // Flat "key: value" lines, with the value left plain.
    [GeneratedRegex(@"^([A-Za-z0-9]+):(?: (.*))?$")]
    private static partial Regex KeyValueLine();

    // An explicit core tag followed by an optionally quoted scalar, in a block or flow sequence.
    [GeneratedRegex(@"!!(str|int|float|bool|null) (""[^""]*""|[^,\]\s]*)")]
    private static partial Regex TaggedScalar();

    private static string? KitCases()
    {
        string? kit = Environment.GetEnvironmentVariable("ONEYAML_KIT");
        if (string.IsNullOrEmpty(kit))
        {
            return null;
        }

        string dir = Path.Combine(kit, "suites", "own", "cases", "schema");
        return Directory.Exists(dir) ? dir : null;
    }

    [Fact]
    public void Core_schema_cases_resolve_as_the_kit_expects()
    {
        string? dir = KitCases();
        if (dir is null)
        {
            Assert.Skip("ONEYAML_KIT is not set");
            return;
        }

        int casesChecked = 0;
        int valuesChecked = 0;
        var skipped = new List<string>();
        foreach (string caseDir in Directory.GetDirectories(dir).Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileName(caseDir);
            string inputPath = Path.Combine(caseDir, "input.yaml");
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(caseDir, "case.json")));
            JsonElement root = doc.RootElement;

            // A bounded case may also accept an exact value; the resolver must give that value, so it is checked too.
            JsonElement expect = root.GetProperty("expect");
            if (expect.GetProperty("kind").GetString() == "bounded" && expect.TryGetProperty("accept", out JsonElement accept)
                && accept.ValueKind == JsonValueKind.Object)
            {
                expect = accept;
            }

            if (!File.Exists(inputPath) || root.GetProperty("operation").GetProperty("type").GetString() != "value"
                || !expect.TryGetProperty("kind", out JsonElement expectKind) || expectKind.GetString() != "value")
            {
                skipped.Add(name + " (not a value case)");
                continue;
            }

            // The expected value is inline or in a file beside the case.
            using JsonDocument? valueFile = expect.TryGetProperty("file", out JsonElement file)
                ? JsonDocument.Parse(File.ReadAllText(Path.Combine(caseDir, file.GetString()!)))
                : null;
            JsonElement values = valueFile?.RootElement ?? expect.GetProperty("value");
            JsonElement expected = values[0];
            string[] lines = File.ReadAllLines(inputPath);
            if (expected.ValueKind == JsonValueKind.Object && !expected.TryGetProperty("$map", out _) && lines.All(l => KeyValueLine().IsMatch(l)))
            {
                var expectedByKey = expected.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
                if (!lines.All(l => IsPlainValue(KeyValueLine().Match(l).Groups[2].Value)))
                {
                    skipped.Add(name + " (values are not all plain)");
                    continue;
                }

                Assert.True(expectedByKey.Count == lines.Length, name);
                foreach (string line in lines)
                {
                    Match m = KeyValueLine().Match(line);
                    byte[] text = Encoding.UTF8.GetBytes(StripComment(m.Groups[2].Value));
                    CheckPlain(name + ":" + m.Groups[1].Value, text, expectedByKey[m.Groups[1].Value]);
                    valuesChecked++;
                }

                casesChecked++;
            }
            else if (expected.ValueKind == JsonValueKind.Array && TaggedScalar().IsMatch(File.ReadAllText(inputPath)))
            {
                MatchCollection tagged = TaggedScalar().Matches(File.ReadAllText(inputPath));
                Assert.True(expected.GetArrayLength() == tagged.Count, name);
                int index = 0;
                foreach (Match m in tagged)
                {
                    string raw = m.Groups[2].Value;
                    bool quoted = raw.StartsWith('"');
                    byte[] text = Encoding.UTF8.GetBytes(quoted ? raw[1..^1] : raw);
                    CoreTag tag = CoreSchema.ParseResolvedTag(Encoding.UTF8.GetBytes("tag:yaml.org,2002:" + m.Groups[1].Value));
                    Assert.True(CoreSchema.TryResolveTagged(tag, text, out ScalarKind kind), name + ": " + m.Value);
                    CheckKind(name + "#" + index, kind, text, expected[index], tagged: true);
                    index++;
                    valuesChecked++;
                }

                casesChecked++;
            }
            else
            {
                skipped.Add(name + " (needs the parser)");
            }
        }

        // The counts are those of the kit revision this was written against (4115afb); a kit change that
        // adds or moves cases must change them here on purpose, and a layout change that makes the
        // reader match nothing fails loudly.
        TestContext.Current.SendDiagnosticMessage($"{casesChecked} kit schema cases, {valuesChecked} values; skipped {skipped.Count}");
        Assert.Equal(16, casesChecked);
        Assert.Equal(128, valuesChecked);
        Assert.NotEmpty(skipped);
    }

    private static bool IsPlainValue(string v) =>
        v.Length == 0 || !(v[0] is '"' or '\'' or '!' or '&' or '*' or '[' or '{' or '|' or '>');

    private static string StripComment(string v)
    {
        int at = v.IndexOf(" #", StringComparison.Ordinal);
        return (at < 0 ? v : v[..at]).TrimEnd();
    }

    private static void CheckPlain(string label, byte[] text, JsonElement expected) =>
        CheckKind(label, CoreSchema.ResolvePlain(text), text, expected, tagged: false);

    private static void CheckKind(string label, ScalarKind kind, byte[] text, JsonElement expected, bool tagged)
    {
        string shown = $"{label} '{Encoding.UTF8.GetString(text)}'";
        switch (expected.ValueKind)
        {
            case JsonValueKind.Null:
                Assert.True(kind == ScalarKind.Null, shown);
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                Assert.True(kind == ScalarKind.Bool, shown);
                Assert.True(CoreSchema.TryParseBool(text, out bool b), shown);
                Assert.Equal(expected.GetBoolean(), b);
                break;
            case JsonValueKind.String:
                Assert.True(kind == ScalarKind.String, shown);
                break;
            case JsonValueKind.Number:
                string raw = expected.GetRawText();
                bool floatSpelled = raw.AsSpan().IndexOfAny('.', 'e', 'E') >= 0;
                if (floatSpelled)
                {
                    Assert.True(kind == ScalarKind.Float, shown);
                    Assert.True(CoreSchema.TryParseDouble(text, out double d), shown);
                    Assert.Equal(double.Parse(raw, CultureInfo.InvariantCulture), d);
                }
                else if (kind == ScalarKind.Float && tagged)
                {
                    // A float tag on an integer spelling: the value is the same number.
                    Assert.True(CoreSchema.TryParseDouble(text, out double d), shown);
                    Assert.Equal((double)BigInteger.Parse(raw, CultureInfo.InvariantCulture), d);
                }
                else
                {
                    Assert.True(kind == ScalarKind.Int, shown);
                    Assert.True(CoreSchema.TryParseBigInteger(text, out BigInteger big), shown);
                    Assert.Equal(BigInteger.Parse(raw, CultureInfo.InvariantCulture), big);
                    if (CoreSchema.TryParseInt64(text, out long l))
                    {
                        Assert.Equal(big, new BigInteger(l));
                    }
                }

                break;
            case JsonValueKind.Object:
                // {"$float": "inf" | "-inf" | "nan"}
                Assert.True(kind == ScalarKind.Float, shown);
                Assert.True(CoreSchema.TryParseDouble(text, out double special), shown);
                string which = expected.GetProperty("$float").GetString()!;
                Assert.True(which switch
                {
                    "inf" => double.IsPositiveInfinity(special),
                    "-inf" => double.IsNegativeInfinity(special),
                    _ => double.IsNaN(special),
                }, shown);
                break;
            default:
                Assert.Fail("unexpected expectation " + expected.GetRawText() + " for " + shown);
                break;
        }
    }
}
