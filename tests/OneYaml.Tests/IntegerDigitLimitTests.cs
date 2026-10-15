using System.Diagnostics;
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
        var watch = Stopwatch.StartNew();
        AssertLimit(() => YamlSerializer.Deserialize<BigInteger>(huge));
        AssertLimit(() => YamlSerializer.Deserialize<double>(huge));
        AssertLimit(() => YamlSerializer.Deserialize<decimal>(huge));
        AssertLimit(() => YamlSerializer.Deserialize<ulong>(huge));
        AssertLimit(() => YamlSerializer.Deserialize<object>(huge));
        AssertLimit(() => YamlSerializer.Deserialize<Dictionary<string, object?>>(doc));
        // An implicit key is at most 1,024 characters, so a long integer key needs a lower limit.
        AssertLimit(() => YamlSerializer.Deserialize<object>(Digits(200) + ": a\n", With(YamlLimits.Default with { MaxIntegerDigits = 100 })));
        watch.Stop();
        TestContext.Current.SendDiagnosticMessage($"seven refusals of a 1,000,000-digit integer, including reading it: {watch.Elapsed.TotalMilliseconds:F1} ms");

        // Each call also reads the megabyte; converting it would take over a second apiece.
        Assert.True(watch.ElapsedMilliseconds < 1000, $"took {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void The_check_itself_does_not_look_at_the_digits_of_a_decimal_integer()
    {
        byte[] huge = System.Text.Encoding.ASCII.GetBytes(Digits(1_000_000));
        var watch = new Stopwatch();
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool within = true;
        watch.Start();
        for (int i = 0; i < 1000; i++)
        {
            within &= OneYaml.Schema.CoreSchema.IsWithinIntegerDigits(huge, 4300);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        TestContext.Current.SendDiagnosticMessage($"the digit check on 1,000,000 digits: {watch.Elapsed.TotalMilliseconds * 1000 / 1000:F2} us per call");

        Assert.False(within);
        Assert.Equal(0, allocated);
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
