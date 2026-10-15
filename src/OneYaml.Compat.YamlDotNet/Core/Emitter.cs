using System;
using System.IO;
using System.Text;
using OneYaml.Compat.YamlDotNet.Core.Events;
using OneYaml.Compat.YamlDotNet.Internal;

namespace OneYaml.Compat.YamlDotNet.Core;

/// <summary>Writes a stream of events as YAML text.</summary>
public class Emitter : IEmitter
{
    private readonly TextWriter _output;
    private readonly Settings _settings = new() { MaxRecursion = int.MaxValue };
    private readonly EventBuffer _buffer = new();
    private readonly BufferEmitter _collector;
    private bool _inDocument;
    private bool _explicitStart;
    private bool _wroteDocument;

    /// <summary>Creates an emitter that writes to a writer.</summary>
    /// <param name="output">The writer.</param>
    public Emitter(TextWriter output)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _collector = new BufferEmitter(_buffer);
    }

    /// <inheritdoc/>
    public void Emit(ParsingEvent parsingEvent)
    {
        ArgumentNullException.ThrowIfNull(parsingEvent);
        switch (parsingEvent)
        {
            case DocumentStart start:
                _buffer.Clear();
                _inDocument = true;
                _explicitStart = !start.IsImplicit;
                break;
            case DocumentEnd:
                if (_inDocument)
                {
                    Flush();
                }

                break;
            case StreamStart or StreamEnd:
                break;
            default:
                _collector.Emit(parsingEvent);
                break;
        }
    }

    private void Flush()
    {
        _inDocument = false;
        var renderer = new TextRenderer(_settings);
        renderer.Document(_buffer, 0, _buffer.Count, _explicitStart || _wroteDocument);
        _output.Write(Encoding.UTF8.GetString(renderer.Written));
        _wroteDocument = true;
    }
}
