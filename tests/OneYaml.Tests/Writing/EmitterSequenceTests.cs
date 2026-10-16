using System.Buffers;
using System.Text;
using OneYaml.Tests.Emitting;
using Xunit;

namespace OneYaml.Tests.Writing;

/// <summary>
/// Random call sequences against the public emitter: a bad order is refused with an <see cref="InvalidOperationException"/>, bad data with a <see cref="YamlException"/>,
/// and nothing else, and every sequence the emitter accepts to the end is text that two other parsers read the same.
/// </summary>
public class EmitterSequenceTests
{
    [Fact]
    public void Random_call_sequences_end_in_valid_text_or_a_yaml_error()
    {
        int refused = 0;
        int accepted = 0;
        for (int seed = 0; seed < 6000; seed++)
        {
            var random = new Random(seed);
            var strings = new HostileStrings(seed);
            var buffer = new ArrayBufferWriter<byte>();
            var emitter = new Utf8YamlEmitter(buffer, new YamlSerializerOptions { IndentSize = 1 + (seed % 8) });
            // A well-formed program, with one call replaced by a random one about a quarter of the time.
            var program = new List<Action<Utf8YamlEmitter>>();
            Value(program, random, strings, 0);
            if (random.Next(4) == 0)
            {
                program[random.Next(program.Count)] = e => Step(e, random, strings);
            }

            bool failed = false;
            try
            {
                foreach (Action<Utf8YamlEmitter> call in program)
                {
                    call(emitter);
                }
            }
            catch (Exception e) when (e is YamlException or InvalidOperationException)
            {
                failed = true;
                refused++;
            }

            if (failed || emitter.CurrentDepth != 0 || buffer.WrittenCount == 0)
            {
                continue;
            }

            string yaml = Encoding.UTF8.GetString(buffer.WrittenSpan);
            if (!yaml.EndsWith('\n'))
            {
                Assert.Fail($"seed {seed}: the text does not end with a line break: {yaml}");
            }

            string? problem = ReadBack.Difference(yaml);
            Assert.True(problem is null, $"seed {seed}: {problem}\n{yaml}");
            accepted++;
        }

        // Both outcomes must actually occur, or the test checks nothing.
        Assert.True(refused > 500, $"only {refused} sequences were refused");
        Assert.True(accepted > 300, $"only {accepted} sequences were accepted to the end");
    }

    private static void Value(List<Action<Utf8YamlEmitter>> program, Random r, HostileStrings strings, int depth)
    {
        int kind = r.Next(depth > 3 ? 6 : 9);
        switch (kind)
        {
            case 0: program.Add(e => e.WriteNullValue()); break;
            case 1: program.Add(e => e.WriteBooleanValue(true)); break;
            case 2: { long n = r.Next(-50, 50); program.Add(e => e.WriteNumberValue(n)); break; }
            case 3: { double d = r.NextDouble() * 100; program.Add(e => e.WriteNumberValue(d)); break; }
            case 4:
            case 5: { string t = strings.Next(allowLong: r.Next(30) == 0); program.Add(e => e.WriteStringValue(t)); break; }
            case 6:
            case 7:
                program.Add(e => e.WriteStartSequence());
                for (int i = r.Next(0, 4); i > 0; i--)
                {
                    Value(program, r, strings, depth + 1);
                }

                program.Add(e => e.WriteEndSequence());
                break;
            default:
                program.Add(e => e.WriteStartMapping());
                for (int i = r.Next(0, 4); i > 0; i--)
                {
                    string key = strings.Next(allowLong: r.Next(40) == 0) + i;
                    program.Add(e => e.WritePropertyName(key));
                    Value(program, r, strings, depth + 1);
                }

                program.Add(e => e.WriteEndMapping());
                break;
        }
    }

    private static void Step(Utf8YamlEmitter e, Random r, HostileStrings strings)
    {
        switch (r.Next(14))
        {
            case 0: e.WriteStartMapping(); break;
            case 1: e.WriteStartSequence(); break;
            case 2:
            case 3: e.WriteEndMapping(); break;
            case 4: e.WriteEndSequence(); break;
            case 5:
            case 6: e.WritePropertyName(strings.Next(allowLong: false)); break;
            case 7:
            case 8: e.WriteStringValue(strings.Next(allowLong: r.Next(20) == 0)); break;
            case 9: e.WriteNumberValue((long)r.Next(-100, 100)); break;
            case 10: e.WriteNumberValue(r.NextDouble() * 1e5); break;
            case 11: e.WriteBooleanValue(r.Next(2) == 0); break;
            case 12: e.WriteNullValue(); break;
            default: e.WriteBinaryValue(new byte[r.Next(0, 6)]); break;
        }
    }
}
