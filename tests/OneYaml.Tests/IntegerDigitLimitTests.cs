using System.Numerics;
using OneYaml.Documents;
using Xunit;

namespace OneYaml.Tests;

/// <summary>
/// An integer with a huge number of digits is refused before it is converted: converting one costs
/// more than linear time, so a megabyte of digits would otherwise hold a thread for seconds.
/// </summary>
public sealed class IntegerDigitLimitTests
{
    private static string Digits(int count) => "1" + new string('0', count - 1);

    private static YamlSerializerOptions With(YamlLimits limits) => new() { Limits = limits };

    private static void AssertLimit(Action action)
    {
        YamlException e = Assert.ThrowsAny<YamlException>(action);
        Assert.True(YamlErrorCategory.Limit == e.Category, e.Category + ": " + e.Message);
    }

    [Fact]
    public void The_default_is_4300_digits_in_default_and_untrusted_and_unlimited_in_none()
    {
        Assert.Equal(4300, YamlLimits.Default.MaxIntegerDigits);
        Assert.Equal(4300, YamlLimits.Untrusted.MaxIntegerDigits);
        Assert.Equal(int.MaxValue, YamlLimits.None.MaxIntegerDigits);
    }

    [Fact]
    public void A_million_digits_end_in_a_limit_error_quickly_on_every_typed_and_generic_path()
    {
        string huge = Digits(1_000_000);
        string doc = "v: " + huge + "\n";

        // What reading the megabyte costs on this machine right now: the same text read as a string goes through
        // the same decoding, scanning and parsing, and converts nothing. A refusal must cost about that much.
        // Converting a million digits to a number takes 90 ms on .NET 10 and over a second on .NET 8, far above that.
        TimeSpan reading = SteadyState.Fastest(() => YamlSerializer.Deserialize<string>(huge), 5);
        var refusals = new (string Name, Action Run)[]
        {
            ("BigInteger", () => AssertLimit(() => YamlSerializer.Deserialize<BigInteger>(huge))),
            ("double", () => AssertLimit(() => YamlSerializer.Deserialize<double>(huge))),
            ("decimal", () => AssertLimit(() => YamlSerializer.Deserialize<decimal>(huge))),
            ("ulong", () => AssertLimit(() => YamlSerializer.Deserialize<ulong>(huge))),
            ("object", () => AssertLimit(() => YamlSerializer.Deserialize<object>(huge))),
            ("mapping value", () => AssertLimit(() => YamlSerializer.Deserialize<Dictionary<string, object?>>(doc))),

            // An implicit key is at most 1,024 characters, so a long integer key needs a lower limit.
            ("key", () => AssertLimit(() => YamlSerializer.Deserialize<object>(Digits(200) + ": a\n", With(YamlLimits.Default with { MaxIntegerDigits = 100 })))),
        };

        TimeSpan bound = (reading * 10) + TimeSpan.FromMilliseconds(20);
        foreach ((string name, Action run) in refusals)
        {
            TimeSpan refusal = SteadyState.Fastest(run, 3);
            TestContext.Current.SendDiagnosticMessage($"refusing a 1,000,000-digit {name}: {refusal.TotalMilliseconds:F1} ms; reading it as a string: {reading.TotalMilliseconds:F1} ms");
            Assert.True(refusal <= bound, $"{name}: {refusal.TotalMilliseconds:F1} ms to refuse; reading the same text takes {reading.TotalMilliseconds:F1} ms");
        }
    }

    [Fact]
    public void The_check_itself_does_not_look_at_the_digits_of_a_decimal_integer()
    {
        byte[] huge = System.Text.Encoding.ASCII.GetBytes(Digits(1_000_000));
        bool within = true;
        SteadyState.AssertAllocatesNothing(() => within &= OneYaml.Schema.CoreSchema.IsWithinIntegerDigits(huge, 4300), 1000);
        Assert.False(within);
    }

    [Fact]
    public void The_limit_is_the_number_of_digits_and_can_be_raised_or_removed()
    {
        Assert.Equal(BigInteger.Pow(10, 4299), YamlSerializer.Deserialize<BigInteger>(Digits(4300)));
        AssertLimit(() => YamlSerializer.Deserialize<BigInteger>(Digits(4301)));
        AssertLimit(() => YamlSerializer.Deserialize<BigInteger>("-" + Digits(4301)));
        Assert.Equal(BigInteger.Pow(10, 4300), YamlSerializer.Deserialize<BigInteger>(Digits(4301), With(YamlLimits.Default with { MaxIntegerDigits = 5000 })));
        Assert.Equal(BigInteger.Pow(10, 9999), YamlSerializer.Deserialize<BigInteger>(Digits(10_000), With(YamlLimits.None)));
        AssertLimit(() => YamlSerializer.Deserialize<BigInteger>(Digits(201), With(YamlLimits.Default with { MaxIntegerDigits = 200 })));
    }

    [Fact]
    public void Hexadecimal_and_octal_digits_count_as_the_decimal_digits_they_stand_for()
    {
        // 3,500 hex digits are 14,000 bits (4,215 decimal digits); 3,600 are 14,400 bits (4,334).
        Assert.True(YamlSerializer.Deserialize<BigInteger>("0x1" + new string('0', 3499)) > 0);
        AssertLimit(() => YamlSerializer.Deserialize<BigInteger>("0x1" + new string('0', 3599)));
        Assert.True(YamlSerializer.Deserialize<BigInteger>("0o1" + new string('0', 4699)) > 0);
        AssertLimit(() => YamlSerializer.Deserialize<BigInteger>("0o1" + new string('0', 4800)));
    }

    [Fact]
    public void Integers_that_fit_in_64_bits_are_not_counted()
    {
        Assert.Equal(7L, YamlSerializer.Deserialize<long>(new string('0', 5000) + "7"));
    }

    [Fact]
    public void A_yaml_document_refuses_long_integers_when_a_value_is_read()
    {
        string text = "a: " + Digits(5000) + "\nb: 1\n";
        YamlDocument doc = YamlDocument.Parse(text);
        Assert.Equal(1, doc.Root["b"].GetValue<int>());
        AssertLimit(() => doc.Root["a"].GetValue<BigInteger>());
        AssertLimit(() => doc.Root["a"].GetValue<double>());
        AssertLimit(() => doc.Root["a"].GetValue<object>());
        AssertLimit(() => doc.Root["a"].TryGetValue(out BigInteger _));
        AssertLimit(() => doc.Get<BigInteger>("/a"));
        AssertLimit(() => doc.TryGet("/a", out BigInteger _));

        // The text is still there, byte for byte.
        Assert.Equal(text, doc.ToString());

        YamlDocument open = YamlDocument.Parse(text, new YamlDocumentOptions { Limits = YamlLimits.None });
        Assert.Equal(BigInteger.Pow(10, 4999), open.Root["a"].GetValue<BigInteger>());
    }
}
