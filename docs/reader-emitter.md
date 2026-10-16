# Reader and emitter

`Utf8YamlReader` and `Utf8YamlEmitter` are the token-level types under the serializer, in the shape of `Utf8JsonReader` and `Utf8JsonWriter`. You meet the reader inside a custom `YamlConverter<T>`, and you can use the emitter on its own to write YAML token by token.

The samples on this page are complete programs that assume implicit usings (the default for new projects); the first shows the `using OneYaml;` line the others leave out.

## Reading tokens in a converter

`YamlConverter<T>.Read` gets the reader on the first token of the value and must leave it on the last token of that value: the scalar itself, or the matching `MappingEnd` or `SequenceEnd`. The reader is never on a document marker or an alias: aliases are followed for you, and every expansion counts against the [limits](limits.md).

This converter reads an image either as `name:tag` or as a mapping, and always writes the short form:

<!-- sample: ConverterMapping -->
```csharp
using OneYaml;

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
```

What the reader offers:

- `TokenType`: `Scalar`, `MappingStart`, `MappingEnd`, `SequenceStart` or `SequenceEnd`. `Read()` moves to the next token; `Skip()` skips the whole value the reader is on.
- `GetString()` returns the text of any scalar. `GetBoolean()`, `GetInt32()`, `GetInt64()`, `GetDouble()`, `GetDecimal()` and `GetBigInteger()` read it under the Core schema and throw a `YamlException` when it is not of that type; `IsNull` tells whether it is a null.
- `Tag` and `TryGetTag` give the tag resolved against the document's `%TAG` directives (`!!str` is `tag:yaml.org,2002:str`); `TryGetRawTag` gives it as written, without allocating. `Anchor` and `TryGetAnchor` give the anchor name.
- `CreateException(message)` builds a binding error that carries the line, column and path of the current token.

The reader is a `ref struct` with no public constructor: it exists only while the serializer calls your converter. To read a whole file by its structure, use [`YamlDocument`](editing.md) or [untyped values](serializer.md#untyped-values).

## Writing YAML token by token

`Utf8YamlEmitter` writes UTF-8 YAML to an `IBufferWriter<byte>`, with the same layout and quoting as `YamlSerializer`. Pass `YamlSerializerOptions` to change the indentation.

<!-- sample: EmitterAlone -->
```csharp
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
```

(`ArrayBufferWriter<byte>` needs `using System.Buffers;` and `Encoding` needs `using System.Text;`.) The emitter has a `Write…Value` method for strings, booleans, null, every number type and binary data (written as `!!binary`), `WriteTag`, `WriteAnchor` and `WriteAlias` for properties of the next node, and `WritePropertyName` for keys. A string that would read back as something else is quoted, so the output reads back as what you wrote. Calling the emitter in the wrong order (a value where a key is required, a second tag on one node, ending a collection that is not open) throws `InvalidOperationException`, as `Utf8JsonWriter` does. `WriteTag` takes the resolved tag: pass `tag:yaml.org,2002:str` for the core string tag, because `!!str` names a local tag.
