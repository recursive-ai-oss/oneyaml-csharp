using Microsoft.CodeAnalysis;

namespace OneYaml.SourceGeneration;

// Stable ids: an id is never reused for a different problem.
internal static class Diagnostics
{
    private const string Category = "OneYaml";

    public static readonly DiagnosticDescriptor ContextNotPartial = new(
        "OY0001",
        "The serializer context must be a partial, non-generic class",
        "'{0}' must be a non-generic partial class, nested only in partial types, so the generator can add its members",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ContextBaseType = new(
        "OY0002",
        "The serializer context must derive from YamlSerializerContext",
        "'{0}' is marked with YamlSerializable but does not derive from OneYaml.YamlSerializerContext",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor OpenGenericType = new(
        "OY0003",
        "Open generic types cannot be described",
        "'{0}' is an open generic type; list a closed type such as List<int> instead",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnsupportedMember = new(
        "OY0004",
        "A type or member cannot be described and is left out",
        "{0}",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor CannotCreate = new(
        "OY0005",
        "A type cannot be created",
        "'{0}' {1}, so reading a mapping into it fails at run time; read it through a concrete type or mark a constructor with YamlConstructor",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor CollectionCycle = new(
        "OY0006",
        "A collection type contains itself",
        "'{0}' is a collection whose element type leads back to it, which cannot be described",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ParameterTypeMismatch = new(
        "OY0007",
        "A constructor parameter and its member differ in type",
        "The constructor parameter '{0}' of '{1}' has type '{2}' but the member of the same name has type '{3}'; make them the same type",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
