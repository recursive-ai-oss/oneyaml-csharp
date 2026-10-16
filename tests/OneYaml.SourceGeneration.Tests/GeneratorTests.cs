using Microsoft.CodeAnalysis;
using Xunit;

namespace OneYaml.SourceGeneration.Tests;

public class GeneratorTests
{
    private const string Header = "using System;\nusing System.Collections.Generic;\nusing OneYaml;\n";

    private const string Demo = Header + """
        namespace Demo;

        public sealed class Server
        {
            public string Host { get; set; } = "";
            public int Port { get; init; }
            public List<string> Tags { get; set; } = new();
            [YamlPropertyName("max-conn")] public int? MaxConnections { get; set; }
        }

        public sealed record Limits(int Cpu, string? Memory = "1Gi");

        public struct Pair
        {
            public int Left;
            public int Right { get; set; }
        }

        public enum Mode { Fast, Slow }

        [YamlSerializable(typeof(Server))]
        [YamlSerializable(typeof(Limits))]
        [YamlSerializable(typeof(Pair))]
        [YamlSerializable(typeof(Mode))]
        [YamlSerializable(typeof(Dictionary<string, Limits>))]
        public partial class DemoContext : YamlSerializerContext
        {
        }
        """;

    private const string Immutables = Header + """
        using System.Collections.Immutable;

        namespace Imm;

        public enum Access
        {
            [YamlEnumMemberName("ro")] ReadOnly,
            ReadWrite,
        }

        public sealed class Holder
        {
            public ImmutableArray<int> Array { get; set; }
            public ImmutableList<string>? List { get; set; }
            public IImmutableSet<int>? Set { get; set; }
            public ImmutableStack<int>? Stack { get; set; }
            public ImmutableDictionary<string, Access>? Map { get; set; }
            public ImmutableSortedDictionary<string, int>? Sorted { get; set; }
        }

        [YamlSerializable(typeof(Holder))]
        public partial class ImmContext : YamlSerializerContext
        {
        }
        """;

    [Fact]
    public void A_context_gets_metadata_that_compiles_cleanly()
    {
        GeneratorRun run = GeneratorHarness.Run(Demo);

        Assert.Empty(run.GeneratorDiagnostics);
        Assert.Single(run.Sources);
        Assert.DoesNotContain(run.CompilationDiagnostics, d => d.Severity >= DiagnosticSeverity.Warning);
    }

    private const string Attributes = Header + """
        using System.Text.Json.Serialization;

        namespace Attr;

        public sealed class LevelConverter : YamlConverter<Level>
        {
            public override Level Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options) => new(1);
            public override void Write(Utf8YamlEmitter writer, Level value, YamlSerializerOptions options) { }
        }

        [YamlConverter(typeof(LevelConverter))]
        public readonly record struct Level(int N);

        public sealed class BoxConverter<T> : YamlConverter<Box<T>>
        {
            public override Box<T>? Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options) => null;
            public override void Write(Utf8YamlEmitter writer, Box<T> value, YamlSerializerOptions options) { }
        }

        [YamlConverter(typeof(BoxConverter<>))]
        public sealed class Box<T> { public T? Value { get; set; } }

        public sealed class Settings
        {
            public string Name { get; set; } = "";
            [YamlConverter(typeof(UpperConverter))] public string Title { get; set; } = "";
            public Box<string> Content { get; set; } = new();
            [YamlExtensionData] public Dictionary<string, object?> Extra { get; set; } = new();
        }

        public sealed class UpperConverter : YamlConverter<string>
        {
            public override string? Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options) => reader.GetString();
            public override void Write(Utf8YamlEmitter writer, string value, YamlSerializerOptions options) { }
        }

        public sealed record Labelled(string Name) { [JsonExtensionData] public IDictionary<string, object>? Rest { get; set; } }

        [YamlDerivedType(typeof(Cat), "cat")]
        [YamlDerivedType(typeof(Dog), "dog")]
        public abstract class Pet { public string Name { get; set; } = ""; }
        public sealed class Cat : Pet { public int Lives { get; set; } }
        public sealed class Dog : Pet { }

        [YamlPolymorphic(DiscriminatorKind = YamlDiscriminatorKind.Tag)]
        [YamlDerivedType(typeof(Circle), "circle")]
        public abstract record Figure;
        public sealed record Circle(double Radius) : Figure;

        [JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
        [JsonDerivedType(typeof(Car), "car")]
        [JsonDerivedType(typeof(Truck), 7)]
        public class Vehicle { public int Wheels { get; set; } }
        public sealed class Car : Vehicle { }
        public sealed class Truck : Vehicle { }

        [YamlSerializable(typeof(Level))]
        [YamlSerializable(typeof(Settings))]
        [YamlSerializable(typeof(Labelled))]
        [YamlSerializable(typeof(Pet))]
        [YamlSerializable(typeof(Figure))]
        [YamlSerializable(typeof(Vehicle))]
        public partial class AttrContext : YamlSerializerContext
        {
        }
        """;

    [Theory]
    [InlineData("Demo", Demo)]
    [InlineData("Attributes", Attributes)]
    [InlineData("Immutable", Immutables)]
    [InlineData("Nested", Header + """
        public sealed class Item { public string Name { get; set; } = ""; public Item[] Parts { get; set; } = []; }

        public static partial class Outer
        {
            [YamlSerializable(typeof(Item))]
            internal sealed partial class Inner : YamlSerializerContext
            {
            }
        }
        """)]
    public void The_emitted_source_matches_the_snapshot(string name, string source)
    {
        GeneratorRun run = GeneratorHarness.Run(source);
        string actual = Assert.Single(run.Sources).Source.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (Environment.GetEnvironmentVariable("ONEYAML_UPDATE_SNAPSHOTS") == "1")
        {
            File.WriteAllText(GeneratorHarness.SnapshotPath(name), actual);
        }

        // Read the copy next to the test assembly: deterministic builds map source paths,
        // so the source location is not usable on a build server.
        string expected = Path.Combine(AppContext.BaseDirectory, "Snapshots", name + ".g.cs.txt");
        Assert.Equal(File.ReadAllText(expected).Replace("\r\n", "\n", StringComparison.Ordinal), actual);
    }

    [Fact]
    public void Converter_extension_data_and_polymorphism_attributes_compile_cleanly_without_reflection()
    {
        GeneratorRun run = GeneratorHarness.Run(Attributes);
        string source = Assert.Single(run.Sources).Source;

        Assert.Empty(run.GeneratorDiagnostics);
        Assert.DoesNotContain(run.CompilationDiagnostics, d => d.Severity >= DiagnosticSeverity.Warning);
        Assert.Contains("GetAttributeConverter<global::Attr.Level>(Options, new global::Attr.LevelConverter())", source, StringComparison.Ordinal);
        // An open generic converter is closed over the type arguments of the type it converts.
        Assert.Contains("new global::Attr.BoxConverter<string>()", source, StringComparison.Ordinal);
        Assert.Contains("IsExtensionData = true,", source, StringComparison.Ordinal);
        Assert.Contains("CreateDerivedTypeInfo<global::Attr.Pet, global::Attr.Cat>(\"cat\"", source, StringComparison.Ordinal);
        Assert.Contains("YamlDiscriminatorKind.Tag", source, StringComparison.Ordinal);
        Assert.Contains("\"kind\"", source, StringComparison.Ordinal);
        Assert.Contains("CreateDerivedTypeInfo<global::Attr.Vehicle, global::Attr.Truck>(\"7\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Reflection", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Immutable_collections_and_attributed_enums_compile_cleanly_and_use_the_library_converters()
    {
        GeneratorRun run = GeneratorHarness.Run(Immutables);
        string source = Assert.Single(run.Sources).Source;

        Assert.Empty(run.GeneratorDiagnostics);
        Assert.DoesNotContain(run.CompilationDiagnostics, d => d.Severity >= DiagnosticSeverity.Warning);
        Assert.Contains("CreateConvertedCollectionInfo<global::System.Collections.Immutable.ImmutableArray<int>, int>", source, StringComparison.Ordinal);
        Assert.Contains("static v => v.IsDefault", source, StringComparison.Ordinal);
        Assert.Contains("CreateConvertedDictionaryInfo<global::System.Collections.Immutable.ImmutableSortedDictionary<string, int>, string, int>", source, StringComparison.Ordinal);
        Assert.Contains("ImmutableStack.CreateRange(global::System.Linq.Enumerable.Reverse(l))", source, StringComparison.Ordinal);
        // The converter reads the member attributes at run time, so reflection and generated metadata name enum values alike.
        Assert.Contains("CreateEnumInfo<global::Imm.Access>", source, StringComparison.Ordinal);
    }

    [Fact]
    public void The_emitted_code_uses_the_metadata_namespace_for_the_plumbing()
    {
        string source = Assert.Single(GeneratorHarness.Run(Demo).Sources).Source;

        Assert.Contains("global::OneYaml.Serialization.Metadata.YamlMetadataServices", source, StringComparison.Ordinal);
        Assert.Contains("global::OneYaml.Serialization.Metadata.YamlObjectInfoValues<", source, StringComparison.Ordinal);
        Assert.DoesNotContain("global::OneYaml.YamlMetadataServices", source, StringComparison.Ordinal);
        Assert.DoesNotContain("global::OneYaml.YamlPropertyInfo", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Init_only_members_are_set_through_an_unsafe_accessor_and_nothing_uses_reflection()
    {
        GeneratorRun run = GeneratorHarness.Run(Demo);
        string source = Assert.Single(run.Sources).Source;

        Assert.Contains("UnsafeAccessor(global::System.Runtime.CompilerServices.UnsafeAccessorKind.Method, Name = \"set_Port\")", source, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Reflection", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetProperty", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Activator", source, StringComparison.Ordinal);
    }

    [Fact]
    public void A_context_in_the_global_namespace_is_supported()
    {
        GeneratorRun run = GeneratorHarness.Run(Header + """
            public sealed class Thing { public int Id { get; set; } }

            [YamlSerializable(typeof(Thing))]
            partial class GlobalContext : YamlSerializerContext
            {
            }
            """);

        Assert.Empty(run.GeneratorDiagnostics);
        Assert.DoesNotContain(run.CompilationDiagnostics, d => d.Severity >= DiagnosticSeverity.Warning);
    }

    [Fact]
    public void A_user_declared_constructor_is_kept()
    {
        GeneratorRun run = GeneratorHarness.Run(Header + """
            public sealed class Thing { public int Id { get; set; } }

            [YamlSerializable(typeof(Thing))]
            public partial class OwnCtor : YamlSerializerContext
            {
                public OwnCtor(int unused) : base(null) { }
            }
            """);

        Assert.Empty(run.GeneratorDiagnostics);
        Assert.DoesNotContain(run.CompilationDiagnostics, d => d.Severity >= DiagnosticSeverity.Warning);
    }

    [Fact]
    public void Types_with_the_same_name_in_different_namespaces_get_distinct_properties()
    {
        GeneratorRun run = GeneratorHarness.Run(Header + """
            namespace A { public sealed class Item { public int X { get; set; } } }
            namespace B { public sealed class Item { public int Y { get; set; } } }

            [YamlSerializable(typeof(A.Item))]
            [YamlSerializable(typeof(B.Item))]
            public partial class Ctx : YamlSerializerContext
            {
            }
            """);

        Assert.Empty(run.GeneratorDiagnostics);
        Assert.DoesNotContain(run.CompilationDiagnostics, d => d.Severity >= DiagnosticSeverity.Warning);
    }

    // ---- Diagnostics ----

    public static TheoryData<string, DiagnosticSeverity, string> Problems() => new()
    {
        {
            "OY0001", DiagnosticSeverity.Error, """
            public sealed class T { public int A { get; set; } }
            [YamlSerializable(typeof(T))]
            public class Ctx : YamlSerializerContext { }
            """
        },
        {
            "OY0001", DiagnosticSeverity.Error, """
            public sealed class T { public int A { get; set; } }
            public class Wrapper
            {
                [YamlSerializable(typeof(T))]
                public partial class Ctx : YamlSerializerContext { }
            }
            """
        },
        {
            "OY0002", DiagnosticSeverity.Error, """
            public sealed class T { public int A { get; set; } }
            [YamlSerializable(typeof(T))]
            public partial class NotAContext { }
            """
        },
        {
            "OY0003", DiagnosticSeverity.Error, """
            public sealed class Box<T> { public T Value { get; set; } = default!; }
            [YamlSerializable(typeof(Box<>))]
            public partial class Ctx : YamlSerializerContext { }
            """
        },
        {
            "OY0004", DiagnosticSeverity.Warning, """
            public sealed class T { public int A { get; set; } public int[,] Grid { get; set; } = new int[0, 0]; }
            [YamlSerializable(typeof(T))]
            public partial class Ctx : YamlSerializerContext { }
            """
        },
        {
            "OY0004", DiagnosticSeverity.Warning, """
            public sealed class T { public int A { get; set; } public Action Callback { get; set; } = () => { }; }
            [YamlSerializable(typeof(T))]
            public partial class Ctx : YamlSerializerContext { }
            """
        },
        {
            "OY0005", DiagnosticSeverity.Warning, """
            public abstract class Shape { public int Sides { get; set; } }
            [YamlSerializable(typeof(Shape))]
            public partial class Ctx : YamlSerializerContext { }
            """
        },
        {
            "OY0005", DiagnosticSeverity.Warning, """
            public interface IThing { int A { get; set; } }
            public sealed class Holder { public IThing Thing { get; set; } = null!; }
            [YamlSerializable(typeof(Holder))]
            public partial class Ctx : YamlSerializerContext { }
            """
        },
        {
            "OY0005", DiagnosticSeverity.Warning, """
            public sealed class TwoCtors { public TwoCtors(int a) { } public TwoCtors(string b) { } public int A { get; set; } }
            [YamlSerializable(typeof(TwoCtors))]
            public partial class Ctx : YamlSerializerContext { }
            """
        },
        {
            "OY0007", DiagnosticSeverity.Error, """
            public sealed class Mismatch { public Mismatch(long count) { Count = (int)count; } public int Count { get; } }
            [YamlSerializable(typeof(Mismatch))]
            public partial class Ctx : YamlSerializerContext { }
            """
        },
        {
            "OY0006", DiagnosticSeverity.Error, """
            public sealed class Loop : List<Loop> { }
            [YamlSerializable(typeof(Loop))]
            public partial class Ctx : YamlSerializerContext { }
            """
        },
        {
            "OY0008", DiagnosticSeverity.Error, """
            public sealed class Wrong { [YamlExtensionData] public Dictionary<string, int> Extra { get; set; } = new(); }
            [YamlSerializable(typeof(Wrong))]
            public partial class Ctx : YamlSerializerContext { }
            """
        },
        {
            "OY0009", DiagnosticSeverity.Error, """
            public sealed class NotAConverter { }
            [YamlConverter(typeof(NotAConverter))]
            public sealed class Thing { public int A { get; set; } }
            [YamlSerializable(typeof(Thing))]
            public partial class Ctx : YamlSerializerContext { }
            """
        },
        {
            "OY0009", DiagnosticSeverity.Error, """
            public sealed class Two<A, B> : YamlConverter<Thing> { public override Thing Read(ref Utf8YamlReader r, Type t, YamlSerializerOptions o) => new(); public override void Write(Utf8YamlEmitter w, Thing v, YamlSerializerOptions o) { } }
            [YamlConverter(typeof(Two<,>))]
            public sealed class Thing { public int A { get; set; } }
            [YamlSerializable(typeof(Thing))]
            public partial class Ctx : YamlSerializerContext { }
            """
        },
        {
            "OY0010", DiagnosticSeverity.Error, """
            public class Base { }
            public class Stranger { }
            [YamlDerivedType(typeof(Stranger), "s")]
            public class Root : Base { }
            [YamlSerializable(typeof(Root))]
            public partial class Ctx : YamlSerializerContext { }
            """
        },
    };

    [Theory]
    [MemberData(nameof(Problems))]
    public void Unsupported_shapes_are_reported_with_a_stable_id(string id, DiagnosticSeverity severity, string source)
    {
        GeneratorRun run = GeneratorHarness.Run(Header + source);

        Diagnostic found = Assert.Single(run.GeneratorDiagnostics, d => d.Id == id);
        Assert.Equal(severity, found.Severity);
        Assert.NotEmpty(found.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
        if (severity == DiagnosticSeverity.Error)
        {
            Assert.Empty(run.Sources);
        }
    }

    [Fact]
    public void Diagnostics_point_at_the_source()
    {
        GeneratorRun run = GeneratorHarness.Run(Header + """
            public abstract class Shape { public int Sides { get; set; } }
            [YamlSerializable(typeof(Shape))]
            public partial class Ctx : YamlSerializerContext { }
            """);

        Diagnostic d = Assert.Single(run.GeneratorDiagnostics, x => x.Id == "OY0005");

        Assert.Equal(3, d.Location.GetLineSpan().StartLinePosition.Line);
    }

    [Fact]
    public void Keyword_member_names_special_default_values_and_user_members_named_like_generated_ones_compile()
    {
        GeneratorRun run = GeneratorHarness.Run(Header + """
            public sealed class Keywords { public string @class { get; set; } = ""; public int @event; public required string @namespace { get; init; } }
            public sealed record Defaults(double A = double.NaN, float B = float.PositiveInfinity, double C = double.NegativeInfinity);
            public sealed class Item { public int V { get; set; } }
            [YamlSerializable(typeof(Keywords))]
            [YamlSerializable(typeof(Defaults))]
            [YamlSerializable(typeof(Item))]
            public partial class Ctx : YamlSerializerContext
            {
                public int _Item;
                private static void CreateKeywords() { }
            }
            """);

        Assert.Empty(GeneratorHarness.Ids(run.GeneratorDiagnostics));
        Assert.DoesNotContain(run.CompilationDiagnostics, d => d.Severity >= DiagnosticSeverity.Warning);
    }

    [Fact]
    public void An_init_only_setter_declared_on_a_base_type_is_reached_through_that_type()
    {
        GeneratorRun run = GeneratorHarness.Run(Header + """
            public class BaseInit { public string? Name { get; init; } }
            public sealed class DerivedInit : BaseInit { }
            [YamlSerializable(typeof(DerivedInit))]
            public partial class Ctx : YamlSerializerContext { }
            """);

        string source = Assert.Single(run.Sources).Source;

        Assert.Matches(@"extern void SetInit\d+\(global::[\w.]*BaseInit instance,", source);
        Assert.DoesNotContain(run.CompilationDiagnostics, d => d.Severity >= DiagnosticSeverity.Warning);
    }

    [Fact]
    public void Supported_shapes_raise_no_diagnostic()
    {
        GeneratorRun run = GeneratorHarness.Run(Header + """
            public enum E { A }
            public record R(int A, E E = E.A);
            public struct S { public int X { get; set; } }
            public class C { public R? R { get; set; } public S S { get; set; } public int[] Xs { get; set; } = []; public IReadOnlyList<string> L { get; set; } = []; public object? O { get; set; } public dynamic? D { get; set; } }
            [YamlSerializable(typeof(C))]
            [YamlSerializable(typeof(int?))]
            [YamlSerializable(typeof(Dictionary<string, List<C>>))]
            public partial class Ctx : YamlSerializerContext { }
            """);

        Assert.Empty(GeneratorHarness.Ids(run.GeneratorDiagnostics));
        Assert.DoesNotContain(run.CompilationDiagnostics, d => d.Severity >= DiagnosticSeverity.Warning);
    }
}
