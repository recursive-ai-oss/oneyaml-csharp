namespace OneYaml.Fuzzing;

/// <summary>
/// The generic load and the lossless document over the same input: an unedited document writes back the
/// input byte for byte, and each document's value read through the document equals the generic load.
/// </summary>
internal sealed class DocumentTarget : FuzzTarget
{
    private long _both;
    private long _documentOnly;
    private long _genericOnly;

    public override string Name => "doc";

    public override bool Run(byte[] input, int hint, YamlLimits limits)
    {
        var documentOptions = new YamlDocumentOptions { Limits = limits };
        var serializerOptions = new YamlSerializerOptions { Limits = limits };

        YamlDocument? doc = null;
        try
        {
            doc = YamlDocument.Parse(input, documentOptions);
        }
        catch (YamlException)
        {
            // The documented outcome for input that is not valid or over a limit.
        }

        IReadOnlyList<object?>? generic = null;
        try
        {
            generic = YamlSerializer.DeserializeAll<object>(input, serializerOptions);
        }
        catch (YamlException)
        {
        }

        try
        {
            if (doc is not null)
            {
                byte[] again = doc.ToBytes();
                if (Plant(input))
                {
                    again = [.. again, (byte)'#'];
                }

                if (!again.AsSpan().SequenceEqual(input))
                {
                    throw new FuzzFailure("document-roundtrip", $"an unedited document wrote {again.Length} bytes for {input.Length} bytes of input");
                }
            }

            if (doc is not null && generic is not null)
            {
                _both++;
                if (doc.Documents.Count != generic.Count)
                {
                    throw new FuzzFailure("document-count", $"{doc.Documents.Count} documents vs {generic.Count} in the generic load");
                }

                for (int i = 0; i < generic.Count; i++)
                {
                    object? viaDocument;
                    try
                    {
                        viaDocument = doc.Documents[i].Deserialize<object>(serializerOptions);
                    }
                    catch (YamlException ex)
                    {
                        throw new FuzzFailure("document-node-deserialize-fails", $"document {i}: the generic load succeeded and the document's own read gave {ex.Category}: {ex.Message}");
                    }

                    string? diff = Values.Diff(generic[i], viaDocument);
                    if (diff is not null)
                    {
                        throw new FuzzFailure("document-vs-generic", $"document {i}: {diff}");
                    }
                }
            }
            else if (doc is not null)
            {
                _documentOnly++;
            }
            else if (generic is not null)
            {
                _genericOnly++;
            }
        }
        finally
        {
            doc?.Dispose();
        }

        return doc is not null || generic is not null;
    }

    public override string Stats() => $"both={_both} documentOnly={_documentOnly} genericOnly={_genericOnly}";
}
