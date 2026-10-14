using System.Text.Unicode;
using OneYaml.Parsing;
using OneYaml.Text;

namespace OneYaml.Tests.Fuzzing;

/// <summary>Raised when the reader's output breaks a rule that holds for every input.</summary>
internal sealed class InvariantViolationException : Exception
{
    public InvariantViolationException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Reads an input to the end and checks what must hold for any text: only <see cref="YamlException"/>
/// escapes, events are well formed and balanced, offsets stay inside the input, limits hold, and
/// every scalar and name can be read.
/// </summary>
internal static class ReaderInvariants
{
    internal readonly record struct Outcome(bool Accepted, int Events, int Signature, YamlException? Error);

    internal static Outcome Check(byte[] input, YamlLimits limits)
    {
        var signature = new HashCode();
        int events = 0;
        try
        {
            using YamlInput text = YamlInput.FromBytes(input, limits);
            using var reader = new YamlReader(text.Utf8Text, new YamlReaderOptions { Limits = limits, RecordComments = input.Length % 2 == 0 });
            int length = text.Utf8Text.Length;
            int open = 0;
            bool inDocument = false;
            bool sawStart = false;
            byte[] scratch = new byte[256];
            while (reader.Read())
            {
                events++;
                YamlTokenType type = reader.TokenType;
                if (events == 1 != (type == YamlTokenType.StreamStart))
                {
                    throw new InvariantViolationException("StreamStart must be the first event and only the first");
                }

                if (sawStart && type == YamlTokenType.StreamStart)
                {
                    throw new InvariantViolationException("second StreamStart");
                }

                sawStart = true;
                if (reader.Start < 0 || reader.End < reader.Start || reader.End > length)
                {
                    throw new InvariantViolationException($"event {type} has the span {reader.Start}..{reader.End} in {length} bytes");
                }

                if (reader.Depth < 0 || reader.Depth > limits.MaxDepth)
                {
                    throw new InvariantViolationException($"depth {reader.Depth} is outside 0..{limits.MaxDepth}");
                }

                signature.Add((int)type);
                signature.Add(reader.Start);
                signature.Add(reader.End);
                switch (type)
                {
                    case YamlTokenType.DocumentStart:
                        if (inDocument)
                        {
                            throw new InvariantViolationException("document started inside a document");
                        }

                        inDocument = true;
                        signature.Add(reader.IsImplicit);
                        break;
                    case YamlTokenType.DocumentEnd:
                        if (!inDocument || open != 0)
                        {
                            throw new InvariantViolationException("document ended with open collections or without a start");
                        }

                        inDocument = false;
                        break;
                    case YamlTokenType.MappingStart:
                    case YamlTokenType.SequenceStart:
                        if (!inDocument || reader.Depth != open)
                        {
                            throw new InvariantViolationException("collection start at the wrong depth");
                        }

                        open++;
                        CheckProps(in reader, length, ref signature);
                        break;
                    case YamlTokenType.MappingEnd:
                    case YamlTokenType.SequenceEnd:
                        open--;
                        if (open < 0 || reader.Depth != open)
                        {
                            throw new InvariantViolationException("collection end at the wrong depth");
                        }

                        break;
                    case YamlTokenType.Scalar:
                        CheckProps(in reader, length, ref signature);
                        CheckScalar(in reader, ref scratch, ref signature);
                        break;
                    case YamlTokenType.Alias:
                        if (!reader.HasAnchor)
                        {
                            throw new InvariantViolationException("alias without a name");
                        }

                        signature.AddBytes(reader.Anchor);
                        break;
                }
            }

            if (events == 0 || inDocument || open != 0)
            {
                throw new InvariantViolationException("the stream ended with an open document or collection");
            }

            if (reader.TokenType != YamlTokenType.None)
            {
                throw new InvariantViolationException("Read returned false but the token type was not reset");
            }

            return new Outcome(true, events, signature.ToHashCode(), null);
        }
        catch (YamlException ex)
        {
            return new Outcome(false, events, 0, ex);
        }
    }

    private static void CheckProps(in YamlReader reader, int length, ref HashCode signature)
    {
        if (reader.HasAnchor)
        {
            if (reader.AnchorStart < 0 || reader.AnchorStart + reader.Anchor.Length > length)
            {
                throw new InvariantViolationException("anchor outside the input");
            }

            signature.AddBytes(reader.Anchor);
        }

        if (reader.HasTag)
        {
            if (reader.TagStart < 0 || reader.TagStart + reader.RawTag.Length > length)
            {
                throw new InvariantViolationException("tag outside the input");
            }

            string? resolved = reader.GetResolvedTag();
            if (string.IsNullOrEmpty(resolved))
            {
                throw new InvariantViolationException("a tag resolved to nothing");
            }

            signature.Add(resolved);
        }
    }

    private static void CheckScalar(in YamlReader reader, ref byte[] scratch, ref HashCode signature)
    {
        int max = reader.MaxDecodedLength;
        if (scratch.Length < max)
        {
            scratch = new byte[max];
        }

        int written = reader.DecodeScalar(scratch);
        if (written < 0 || written > max)
        {
            throw new InvariantViolationException($"decoded {written} bytes with a bound of {max}");
        }

        if (!Utf8.IsValid(scratch.AsSpan(0, written)))
        {
            throw new InvariantViolationException("a scalar decoded to invalid UTF-8");
        }

        if (!reader.ValueIsEscapedOrFolded && !reader.RawScalar.SequenceEqual(scratch.AsSpan(0, written)))
        {
            throw new InvariantViolationException("a scalar marked as plain text differs from its raw text");
        }

        string text = reader.GetString();
        if (System.Text.Encoding.UTF8.GetByteCount(text) != written)
        {
            throw new InvariantViolationException("GetString and DecodeScalar disagree");
        }

        signature.AddBytes(scratch.AsSpan(0, written));
        signature.Add((int)reader.ScalarStyle);
    }
}
