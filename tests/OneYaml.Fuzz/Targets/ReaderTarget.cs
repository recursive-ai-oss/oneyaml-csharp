using OneYaml.Tests.Fuzzing;

namespace OneYaml.Fuzzing;

/// <summary>The pull reader: well-formed events, offsets inside the input, only YamlException escapes, and the same text in another encoding reads the same.</summary>
internal sealed class ReaderTarget : FuzzTarget
{
    private long _encodingChecks;

    public override string Name => "reader";

    public override bool CapAlways => true;

    public override long AllocationFactor => 200;

    public override long AllocationFloor => 4L << 20;

    public override bool Run(byte[] input, int hint, YamlLimits limits)
    {
        ReaderInvariants.Outcome outcome = ReaderInvariants.Check(input, limits);
        Random rng = Seeded(input, hint);
        if (limits == YamlLimits.Default && rng.Next(8) == 0)
        {
            int variant = rng.Next(8);
            byte[]? other = Mutator.Transcode(input, variant);
            bool detectable = (variant & 1) == 0 || (input.Length > 0 && input[0] is > 0 and < 0x80);
            if (other is not null && detectable && !input.Contains((byte)0)
                && !(input.Length >= 3 && input[0] == 0xEF && input[1] == 0xBB && input[2] == 0xBF))
            {
                ReaderInvariants.Outcome again = ReaderInvariants.Check(other, limits);
                _encodingChecks++;
                if (again.Accepted != outcome.Accepted || (outcome.Accepted && again.Signature != outcome.Signature + (Plant(input) ? 1 : 0)))
                {
                    throw new FuzzFailure("encoding-variant", $"encoding variant {variant} reads differently (accepted {outcome.Accepted} vs {again.Accepted})");
                }
            }
        }

        return outcome.Accepted;
    }

    public override string Stats() => $"encodingChecks={_encodingChecks}";
}
