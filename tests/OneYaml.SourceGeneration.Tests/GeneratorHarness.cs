using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace OneYaml.SourceGeneration.Tests;

/// <summary>What one generator run produced.</summary>
internal sealed record GeneratorRun(
    ImmutableArray<Diagnostic> GeneratorDiagnostics,
    ImmutableArray<Diagnostic> CompilationDiagnostics,
    IReadOnlyList<(string HintName, string Source)> Sources);

/// <summary>Runs the generator over source text, the way the compiler would.</summary>
internal static class GeneratorHarness
{
    private static readonly Lazy<ImmutableArray<MetadataReference>> s_references = new(() =>
    {
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var references = paths
            .Where(p => Path.GetFileName(p) is { } name && name.EndsWith(".dll", StringComparison.Ordinal))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();
        references.Add(MetadataReference.CreateFromFile(typeof(YamlSerializer).Assembly.Location));
        return [.. references];
    });

    public static GeneratorRun Run(string source)
    {
        Compilation compilation = CSharpCompilation.Create(
            "Generated.Input",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))],
            s_references.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new YamlSerializableGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation output, out ImmutableArray<Diagnostic> generatorDiagnostics);
        GeneratorDriverRunResult result = driver.GetRunResult();
        var sources = result.Results
            .SelectMany(r => r.GeneratedSources)
            .Select(s => (s.HintName, s.SourceText.ToString()))
            .ToList();
        return new GeneratorRun(generatorDiagnostics, output.GetDiagnostics(), sources);
    }

    public static string[] Ids(ImmutableArray<Diagnostic> diagnostics) =>
        [.. diagnostics.Where(d => d.Id.StartsWith("OY", StringComparison.Ordinal)).Select(d => d.Id)];

    public static string SnapshotPath(string name, [System.Runtime.CompilerServices.CallerFilePath] string caller = "") =>
        Path.Combine(Path.GetDirectoryName(caller)!, "Snapshots", name + ".g.cs.txt");
}
