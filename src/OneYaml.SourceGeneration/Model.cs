using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace OneYaml.SourceGeneration;

// How a type is described in the generated context. The order follows the reflection resolver.
internal enum TypeShape
{
    Leaf,
    Nullable,
    Enum,
    Array,
    List,
    HashSet,
    Dictionary,
    Interface,
    CustomDictionary,
    CustomCollection,
    ImmutableCollection,
    ImmutableDictionary,
    Converter,
    Object,
}

// One type that gets a property in the context. Held only while a context is being analysed.
internal sealed class TypeSpec
{
    public TypeSpec(ITypeSymbol symbol, string name, string property, TypeShape shape)
    {
        Symbol = symbol;
        Name = name;
        Property = property;
        Shape = shape;
    }

    public ITypeSymbol Symbol { get; }

    // The fully qualified C# name, without nullable annotations.
    public string Name { get; }

    // The name of the context property.
    public string Property { get; }

    public TypeShape Shape { get; }

    // Element, key and value types, or the underlying or implementing type, as the shape needs them.
    public List<TypeSpec> Dependencies { get; } = [];

    // The implementing type of an interface shape, or the collection and dictionary type the shape names.
    public string? ImplementationName { get; set; }

    public ObjectSpec? Object { get; set; }

    // For a type with its own converter: the code that makes it.
    public string? ConverterText { get; set; }

    // 3 public, 2 internal, 1 private: how far the type can be seen.
    public int Visibility { get; set; } = 3;

    public bool Processed { get; set; }
}

internal sealed class ObjectSpec
{
    public bool IsStruct { get; set; }

    // None, Parameterless, Default or Constructor.
    public CreatorKind Creator { get; set; }

    public string InitializerText { get; set; } = string.Empty;

    public List<ParameterSpec> Parameters { get; } = [];

    public List<MemberSpec> Members { get; } = [];

    // Set when the type is the base of derived types that are told apart by a discriminator.
    public PolymorphismSpec? Polymorphism { get; set; }
}

internal sealed class PolymorphismSpec
{
    // 0 key, 1 tag: the values of YamlDiscriminatorKind.
    public int Kind { get; set; }

    public string Key { get; set; } = "$type";

    public List<(TypeSpec Type, string Discriminator)> Derived { get; } = [];
}

internal enum CreatorKind
{
    None,
    Parameterless,
    Default,
    Constructor,
}

internal sealed class ParameterSpec
{
    public ParameterSpec(string name, string typeName, bool isValueType, bool isNullableValue, bool hasDefault, string defaultText)
    {
        Name = name;
        TypeName = typeName;
        IsNonNullableValueType = isValueType && !isNullableValue;
        HasDefault = hasDefault;
        DefaultText = defaultText;
    }

    public string Name { get; }

    public string TypeName { get; }

    public bool IsNonNullableValueType { get; }

    public bool HasDefault { get; }

    public string DefaultText { get; }
}

internal enum SetterKind
{
    // Filled through the constructor only: reading into the slot needs no assignment.
    None,
    Plain,
    InitOnly,
}

internal sealed class MemberSpec
{
    public MemberSpec(string name, string typeName, TypeSpec type)
    {
        Name = name;
        TypeName = typeName;
        Type = type;
    }

    public string Name { get; }

    public string TypeName { get; }

    public TypeSpec Type { get; }

    public string? SerializedName { get; set; }

    public bool IsRequired { get; set; }

    public int Order { get; set; }

    public bool CanRead { get; set; }

    // The condition an ignore attribute names for writing, as the value of the condition enums
    // (0 never, 2 when writing default, 3 when writing null); null when no attribute names one.
    public int? IgnoreCondition { get; set; }

    public SetterKind Setter { get; set; }

    // The type that declares the property, which an accessor for its init-only setter must name:
    // the runtime looks the setter up on that type and does not search its base types.
    public string? DeclaringTypeName { get; set; }

    public bool DeclaringIsGeneric { get; set; }

    // Collects the keys no other member takes.
    public bool IsExtensionData { get; set; }

    // The code that makes the converter a YamlConverter attribute names for this member.
    public string? ConverterText { get; set; }
}

// A diagnostic that can be compared, so the pipeline can tell when nothing changed.
internal sealed class DiagnosticInfo : IEquatable<DiagnosticInfo>
{
    public DiagnosticInfo(DiagnosticDescriptor descriptor, string? path, Microsoft.CodeAnalysis.Text.TextSpan span, Microsoft.CodeAnalysis.Text.LinePositionSpan lineSpan, string[] arguments)
    {
        Descriptor = descriptor;
        Path = path;
        Span = span;
        LineSpan = lineSpan;
        Arguments = arguments;
    }

    public DiagnosticDescriptor Descriptor { get; }

    public string? Path { get; }

    public Microsoft.CodeAnalysis.Text.TextSpan Span { get; }

    public Microsoft.CodeAnalysis.Text.LinePositionSpan LineSpan { get; }

    public string[] Arguments { get; }

    public Diagnostic ToDiagnostic()
    {
        Location location = Path is null
            ? Location.None
            : Location.Create(Path, Span, LineSpan);
        return Diagnostic.Create(Descriptor, location, Arguments);
    }

    public bool Equals(DiagnosticInfo? other) =>
        other is not null
        && Descriptor.Id == other.Descriptor.Id
        && Path == other.Path
        && Span == other.Span
        && ArgumentsEqual(other.Arguments);

    private bool ArgumentsEqual(string[] other)
    {
        if (Arguments.Length != other.Length)
        {
            return false;
        }

        for (int i = 0; i < other.Length; i++)
        {
            if (Arguments[i] != other[i])
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as DiagnosticInfo);

    public override int GetHashCode() => HashCode(Descriptor.Id, Path, Span.Start);

    private static int HashCode(string id, string? path, int start) => (((id.GetHashCode() * 31) + (path?.GetHashCode() ?? 0)) * 31) + start;
}

// What one context produces: source text, or none, and the diagnostics to report.
internal sealed class GenerationResult : IEquatable<GenerationResult>
{
    public GenerationResult(string? hintName, string? source, DiagnosticInfo[] diagnostics)
    {
        HintName = hintName;
        Source = source;
        Diagnostics = diagnostics;
    }

    public string? HintName { get; }

    public string? Source { get; }

    public DiagnosticInfo[] Diagnostics { get; }

    public bool Equals(GenerationResult? other)
    {
        if (other is null || HintName != other.HintName || Source != other.Source || Diagnostics.Length != other.Diagnostics.Length)
        {
            return false;
        }

        for (int i = 0; i < Diagnostics.Length; i++)
        {
            if (!Diagnostics[i].Equals(other.Diagnostics[i]))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as GenerationResult);

    public override int GetHashCode() => (HintName?.GetHashCode() ?? 0) ^ (Source?.GetHashCode() ?? 0);
}
