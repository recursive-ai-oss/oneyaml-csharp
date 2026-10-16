#region ConverterMapping
using OneYaml;
#endregion
using System.Buffers;
using System.Text;
using Xunit;

namespace DocSamples.ReaderEmitter;

[Collection(nameof(DocSample))]
public class ReaderEmitterSamples
{
    [Fact]
    public void ConverterMapping() => DocSample.Run(nameof(ConverterMapping), () =>
    {
        #region ConverterMapping
        var pod = YamlSerializer.Deserialize<Pod>("""
            images:
              - nginx:1.27
              - name: envoy
                tag: "1.30"
                note: ignored by the converter
            """)!;
        Console.WriteLine(string.Join(", ", pod.Images)); // Image { Name = nginx, Tag = 1.27 }, Image { Name = envoy, Tag = 1.30 }

        Console.Write(YamlSerializer.Serialize(pod));
        // images:
        //   - nginx:1.27
        //   - envoy:1.30
        #endregion
    });

    [Fact]
    public void EmitterAlone() => DocSample.Run(nameof(EmitterAlone), () =>
    {
        #region EmitterAlone
        var buffer = new ArrayBufferWriter<byte>();
        var emitter = new Utf8YamlEmitter(buffer);
        emitter.WriteStartMapping();
        emitter.WritePropertyName("name");
        emitter.WriteStringValue("web");
        emitter.WritePropertyName("ports");
        emitter.WriteStartSequence();
        emitter.WriteNumberValue(80);
        emitter.WriteNumberValue(443);
        emitter.WriteEndSequence();
        emitter.WritePropertyName("base");
        emitter.WriteTag("!ref");
        emitter.WriteStringValue("defaults");
        emitter.WriteEndMapping();

        Console.Write(Encoding.UTF8.GetString(buffer.WrittenSpan));
        // name: web
        // ports:
        //   - 80
        //   - 443
        // base: !ref defaults
        #endregion
    });
}

#region ConverterMapping
record Pod(List<Image> Images);

[YamlConverter(typeof(ImageConverter))]
record Image(string Name, string Tag);

// Reads "name:tag" or a mapping with 'name' and 'tag'; always writes the short form.
sealed class ImageConverter : YamlConverter<Image>
{
    public override Image Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options)
    {
        if (reader.TokenType == YamlTokenKind.Scalar)
        {
            string text = reader.GetString();
            int colon = text.LastIndexOf(':');
            return colon > 0 ? new Image(text[..colon], text[(colon + 1)..]) : new Image(text, "latest");
        }

        if (reader.TokenType != YamlTokenKind.MappingStart)
        {
            throw reader.CreateException("An image is a string or a mapping.");
        }

        string name = "", tag = "latest";
        while (reader.Read() && reader.TokenType != YamlTokenKind.MappingEnd)
        {
            string key = reader.GetString(); // on the key
            reader.Read();                   // on the value
            switch (key)
            {
                case "name": name = reader.GetString(); break;
                case "tag": tag = reader.GetString(); break;
                default: reader.Skip(); break;  // skips a whole collection too
            }
        }

        return new Image(name, tag); // the reader is left on MappingEnd, the last token of the value
    }

    public override void Write(Utf8YamlEmitter writer, Image value, YamlSerializerOptions options) =>
        writer.WriteStringValue($"{value.Name}:{value.Tag}");
}
#endregion
