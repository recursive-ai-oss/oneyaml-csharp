using System.Text.Json.Serialization;

namespace OneYaml.Tests.Attributes;

// Models for the converter, extension data and polymorphism attributes. Each is read and written through reflection
// and through the generated context below, and both must agree.

internal sealed class LevelConverter : YamlConverter<Level>
{
    public override Level Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options)
    {
        string text = reader.GetString();
        if (!text.StartsWith("lv", StringComparison.Ordinal) || !int.TryParse(text.AsSpan(2), out int n))
        {
            throw reader.CreateException("A level reads like lv3.");
        }

        return new Level(n);
    }

    public override void Write(Utf8YamlEmitter writer, Level value, YamlSerializerOptions options) =>
        writer.WriteStringValue("lv" + value.Value);
}

[YamlConverter(typeof(LevelConverter))]
internal readonly record struct Level(int Value);

internal sealed class ShoutConverter : YamlConverter<string>
{
    public override string? Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options) =>
        reader.GetString().ToUpperInvariant();

    public override void Write(Utf8YamlEmitter writer, string value, YamlSerializerOptions options) =>
        writer.WriteStringValue(value.ToLowerInvariant());
}

internal sealed class Job
{
    public Level Level { get; set; }

    [YamlConverter(typeof(ShoutConverter))]
    public string Name { get; set; } = "";

    public string Plain { get; set; } = "";
}

// A converter on the constructor path: the member is read through its converter into an argument slot.
internal sealed record Step(string Id, [property: YamlConverter(typeof(ShoutConverter))] string Text);

internal sealed class BoxConverter<T> : YamlConverter<Box<T>>
{
    public override Box<T>? Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options) =>
        new Box<T> { Value = (T)(object)("<" + reader.GetString() + ">") };

    public override void Write(Utf8YamlEmitter writer, Box<T> value, YamlSerializerOptions options) =>
        writer.WriteStringValue(value.Value?.ToString() ?? "");
}

[YamlConverter(typeof(BoxConverter<>))]
internal sealed class Box<T>
{
    public T Value { get; set; } = default!;
}

internal sealed class Crate
{
    public Box<string> Content { get; set; } = new();
}

internal sealed class Settings
{
    public string Name { get; set; } = "";

    public int Port { get; set; }

    [YamlExtensionData]
    public Dictionary<string, object?> Extra { get; set; } = new();
}

// The dictionary is made when the type has none, as System.Text.Json does.
internal sealed class Lazy
{
    public string Name { get; set; } = "";

    [YamlExtensionData]
    public IDictionary<string, object?>? Rest { get; set; }
}

internal sealed class FromJson
{
    public string Name { get; set; } = "";

    [JsonExtensionData]
    public Dictionary<string, object> Other { get; set; } = new();
}

internal sealed record Labelled(string Name, int Size)
{
    [YamlExtensionData]
    public Dictionary<string, object?> Extra { get; set; } = new();
}

[YamlPolymorphic]
[YamlDerivedType(typeof(Cat), "cat")]
[YamlDerivedType(typeof(Hound), "dog")]
[YamlDerivedType(typeof(Wild), "wild")]
internal abstract class Pet
{
    public string Name { get; set; } = "";
}

internal sealed class Cat : Pet
{
    public int Lives { get; set; } = 9;
}

internal sealed class Hound : Pet
{
    public bool Good { get; set; } = true;
}

internal sealed class Wild : Pet
{
    [YamlExtensionData]
    public Dictionary<string, object?> More { get; set; } = new();
}

internal sealed class Home
{
    public Pet? Pet { get; set; }

    public List<Pet> Others { get; set; } = [];
}

[YamlPolymorphic(Discriminator = YamlDiscriminatorKind.Tag)]
[YamlDerivedType(typeof(Circle), "circle")]
[YamlDerivedType(typeof(Square), "!square")]
[YamlDerivedType(typeof(Label), "label")]
internal abstract record Figure;

internal sealed record Circle(double Radius) : Figure;

internal sealed record Square : Figure
{
    public double Side { get; set; }
}

[YamlConverter(typeof(LabelConverter))]
internal sealed record Label : Figure
{
    public string Text { get; set; } = "";
}

internal sealed class LabelConverter : YamlConverter<Label>
{
    public override Label? Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options) =>
        new Label { Text = reader.GetString() };

    public override void Write(Utf8YamlEmitter writer, Label value, YamlSerializerOptions options) =>
        writer.WriteStringValue(value.Text);
}

internal sealed class Drawing
{
    public List<Figure> Figures { get; set; } = [];
}

// A base that can be created is read as itself when no discriminator is present; System.Text.Json's attributes name the key.
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(Car), "car")]
[JsonDerivedType(typeof(Truck), 7)]
[JsonDerivedType(typeof(Bus), -3)]
internal class Vehicle
{
    public int Wheels { get; set; } = 4;
}

internal sealed class Car : Vehicle
{
    public string Colour { get; set; } = "";
}

internal sealed class Truck : Vehicle
{
    public int Load { get; set; }
}

internal sealed class Bus : Vehicle
{
    public int Seats { get; set; }
}

// The YAML attribute names no key and System.Text.Json's does: the YAML attribute wins, so the key stays $type.
[YamlPolymorphic]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[YamlDerivedType(typeof(MixA), "a")]
internal class Mixed
{
    public int X { get; set; }
}

internal sealed class MixA : Mixed
{
    public int Y { get; set; }
}

// Mistakes in the attributes, which are refused when the metadata is made or first used.
[YamlPolymorphic]
[YamlDerivedType(typeof(DupA), "same")]
[YamlDerivedType(typeof(DupB), "same")]
internal abstract class DupBase;

internal sealed class DupA : DupBase;

internal sealed class DupB : DupBase;

internal sealed class TwoExtensions
{
    [YamlExtensionData]
    public Dictionary<string, object?> One { get; set; } = new();

    [YamlExtensionData]
    public Dictionary<string, object?> Two { get; set; } = new();
}

[YamlConverter(typeof(string))]
internal sealed class NotAConverter
{
}

internal sealed class TagReader : YamlConverter<string>
{
    public override string? Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options)
    {
        string raw = reader.TryGetTag(out ReadOnlySpan<byte> tag) ? System.Text.Encoding.UTF8.GetString(tag) : "";
        return reader.Tag + "|" + reader.Anchor + "|" + raw;
    }

    public override void Write(Utf8YamlEmitter writer, string value, YamlSerializerOptions options) => writer.WriteStringValue(value);
}

internal sealed class TagProbe
{
    [YamlConverter(typeof(TagReader))]
    public string? A { get; set; }

    [YamlConverter(typeof(TagReader))]
    public string? B { get; set; }

    [YamlConverter(typeof(TagReader))]
    public string? C { get; set; }

    [YamlConverter(typeof(TagReader))]
    public string? D { get; set; }
}

// Types made through a constructor are told apart by a key too.
[YamlDerivedType(typeof(Memo), "memo")]
internal record Note(string Title);

internal sealed record Memo(string Title, int Priority) : Note(Title);

[YamlSerializable(typeof(Job))]
[YamlSerializable(typeof(Step))]
[YamlSerializable(typeof(Level))]
[YamlSerializable(typeof(Crate))]
[YamlSerializable(typeof(Settings))]
[YamlSerializable(typeof(Lazy))]
[YamlSerializable(typeof(FromJson))]
[YamlSerializable(typeof(Labelled))]
[YamlSerializable(typeof(Pet))]
[YamlSerializable(typeof(Cat))]
[YamlSerializable(typeof(Home))]
[YamlSerializable(typeof(Figure))]
[YamlSerializable(typeof(Drawing))]
[YamlSerializable(typeof(Vehicle))]
[YamlSerializable(typeof(Mixed))]
[YamlSerializable(typeof(Note))]
internal sealed partial class AttributeContext : YamlSerializerContext
{
}
