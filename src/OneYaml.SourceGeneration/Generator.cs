using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace OneYaml.SourceGeneration;

/// <summary>
/// Writes type metadata for every class that derives from <c>YamlSerializerContext</c> and carries
/// <c>[YamlSerializable]</c> attributes, so typed loading needs no reflection.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class YamlSerializableGenerator : IIncrementalGenerator
{
    /// <inheritdoc/>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<GenerationResult> results = context.SyntaxProvider.ForAttributeWithMetadataName(
            "OneYaml.YamlSerializableAttribute",
            static (node, _) => node is ClassDeclarationSyntax,
            static (syntax, cancellation) => Parser.Run(syntax, cancellation));

        context.RegisterSourceOutput(results, static (output, result) =>
        {
            foreach (DiagnosticInfo diagnostic in result.Diagnostics)
            {
                output.ReportDiagnostic(diagnostic.ToDiagnostic());
            }

            if (result.Source is not null && result.HintName is not null)
            {
                output.AddSource(result.HintName, result.Source);
            }
        });
    }
}
