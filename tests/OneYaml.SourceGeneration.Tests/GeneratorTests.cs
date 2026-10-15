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

    [Fact]
    public void A_context_gets_metadata_that_compiles_cleanly()
    {
        GeneratorRun run = GeneratorHarness.Run(Demo);

        Assert.Empty(run.GeneratorDiagnostics);
        Assert.Single(run.Sources);
        Assert.DoesNotContain(run.CompilationDiagnostics, d => d.Severity >= DiagnosticSeverity.Warning);
    }

    [Theory]
    [InlineData("Demo", Demo)]
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
            "OY0006", DiagnosticSeverity.Error, """
            public sealed class Loop : List<Loop> { }
            [YamlSerializable(typeof(Loop))]
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
