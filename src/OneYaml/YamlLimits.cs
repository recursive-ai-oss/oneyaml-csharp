using System;
namespace OneYaml;

/// <summary>
/// Resource limits for reading YAML. Exceeding one throws a <see cref="YamlException"/> with
/// category <see cref="YamlErrorCategory.Limit"/>.
/// </summary>
public sealed record YamlLimits
{
    /// <summary>The limits used when none are given. Generous enough for real-world files.</summary>
    public static YamlLimits Default { get; } = new();

    /// <summary>Tighter limits for input from an untrusted source.</summary>
    /// <example>
    /// <code language="csharp">
    /// var options = new YamlSerializerOptions { Limits = YamlLimits.Untrusted };
    /// try
    /// {
    ///     YamlSerializer.Deserialize&lt;object&gt;(new string('[', 200) + new string(']', 200), options);
    /// }
    /// catch (YamlException e) when (e.Category == YamlErrorCategory.Limit)
    /// {
    ///     Console.WriteLine("refused: too deep"); // refused: too deep
    /// }
    /// </code>
    /// </example>
    public static YamlLimits Untrusted { get; } = new()
    {
        MaxDepth = 128,
        MaxInputBytes = 64L * 1024 * 1024,
        MaxScalarBytes = 16 * 1024 * 1024,
        MaxDocuments = 10_000,
        AliasNodeFactor = 2,
        AliasNodeFloor = 100_000,
        AliasByteFactor = 2,
        AliasByteFloor = 8L * 1024 * 1024,
        MaxIntegerDigits = 4_300,
    };

    /// <summary>No limits at all. Only for input that is fully trusted.</summary>
    public static YamlLimits None { get; } = new()
    {
        MaxDepth = int.MaxValue,
        MaxInputBytes = long.MaxValue,
        MaxScalarBytes = int.MaxValue,
        MaxDocuments = int.MaxValue,
        AliasNodeFactor = long.MaxValue,
        AliasNodeFloor = long.MaxValue,
        AliasByteFactor = long.MaxValue,
        AliasByteFloor = long.MaxValue,
        MaxIntegerDigits = int.MaxValue,
    };

    /// <summary>The deepest nesting of collections allowed.</summary>
    public int MaxDepth { get; init; } = 1_000;

    /// <summary>The largest input, in bytes. The limit is checked against the input as given and again against its UTF-8 form.</summary>
    public long MaxInputBytes { get; init; } = long.MaxValue;

    /// <summary>The largest scalar, in bytes of its source text.</summary>
    public int MaxScalarBytes { get; init; } = int.MaxValue;

    /// <summary>The most documents one stream may hold.</summary>
    public int MaxDocuments { get; init; } = int.MaxValue;

    /// <summary>
    /// The most digits an integer written in the document may have when it is read as a number of
    /// any size. Hexadecimal and octal digits count as the decimal digits they stand for. Reading
    /// a longer integer throws a <see cref="YamlException"/> with category
    /// <see cref="YamlErrorCategory.Limit"/>, before any work is done on the digits: converting
    /// a very long integer takes more than linear time. Integers that fit in 64 bits are not affected.
    /// </summary>
    public int MaxIntegerDigits { get; init; } = 4_300;

    /// <summary>
    /// Alias expansion budget in nodes: this many times the document's own node count, but at least
    /// <see cref="AliasNodeFloor"/>. Applied by the code that follows aliases.
    /// </summary>
    public long AliasNodeFactor { get; init; } = 10;

    /// <summary>The smallest alias expansion budget in nodes.</summary>
    public long AliasNodeFloor { get; init; } = 1_000_000;

    /// <summary>
    /// Alias expansion budget in bytes: this many times the input size, but at least
    /// <see cref="AliasByteFloor"/>. Applied by the code that follows aliases.
    /// </summary>
    public long AliasByteFactor { get; init; } = 10;

    /// <summary>The smallest alias expansion budget in bytes.</summary>
    public long AliasByteFloor { get; init; } = 64L * 1024 * 1024;

    /// <summary>The number of nodes alias expansion may produce for a document with the given node count.</summary>
    /// <param name="ownNodes">The number of nodes in the document itself.</param>
    /// <returns>The budget, never less than <see cref="AliasNodeFloor"/>.</returns>
    public long AliasNodeBudget(long ownNodes) => Budget(ownNodes, AliasNodeFactor, AliasNodeFloor);

    /// <summary>The number of bytes alias expansion may produce for an input of the given size.</summary>
    /// <param name="inputBytes">The size of the input in bytes.</param>
    /// <returns>The budget, never less than <see cref="AliasByteFloor"/>.</returns>
    public long AliasByteBudget(long inputBytes) => Budget(inputBytes, AliasByteFactor, AliasByteFloor);

    // The one wording for a refused alias expansion, whoever noticed it: what was refused, and the settings that set the budget.
    internal string AliasBudgetMessage(string what) =>
        $"{what} exceeds the budget set by YamlLimits (AliasNodeFactor {AliasNodeFactor}, AliasNodeFloor {AliasNodeFloor}, AliasByteFactor {AliasByteFactor}, AliasByteFloor {AliasByteFloor}).";

    // Each limit at the lower of the two: what a reader that must honour both has to use.
    internal static YamlLimits Stricter(YamlLimits a, YamlLimits b) =>
        a.Equals(b) ? b : new YamlLimits
        {
            MaxDepth = Math.Min(a.MaxDepth, b.MaxDepth),
            MaxInputBytes = Math.Min(a.MaxInputBytes, b.MaxInputBytes),
            MaxScalarBytes = Math.Min(a.MaxScalarBytes, b.MaxScalarBytes),
            MaxDocuments = Math.Min(a.MaxDocuments, b.MaxDocuments),
            MaxIntegerDigits = Math.Min(a.MaxIntegerDigits, b.MaxIntegerDigits),
            AliasNodeFactor = Math.Min(a.AliasNodeFactor, b.AliasNodeFactor),
            AliasNodeFloor = Math.Min(a.AliasNodeFloor, b.AliasNodeFloor),
            AliasByteFactor = Math.Min(a.AliasByteFactor, b.AliasByteFactor),
            AliasByteFloor = Math.Min(a.AliasByteFloor, b.AliasByteFloor),
        };

    private static long Budget(long own, long factor, long floor)
    {
        if (factor == long.MaxValue || floor == long.MaxValue)
        {
            return long.MaxValue;
        }

        long scaled = own > long.MaxValue / Math.Max(factor, 1) ? long.MaxValue : own * factor;
        return Math.Max(scaled, floor);
    }
}
