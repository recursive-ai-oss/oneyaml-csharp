using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using OneYaml.Documents;

namespace OneYaml;

/// <summary>The kind of one RFC 6902 operation.</summary>
public enum YamlPatchOperationKind
{
    /// <summary>Adds a value, or replaces the member that is already there.</summary>
    Add = 0,

    /// <summary>Removes a value.</summary>
    Remove,

    /// <summary>Replaces a value that must exist.</summary>
    Replace,

    /// <summary>Removes a value and adds it somewhere else.</summary>
    Move,

    /// <summary>Adds a copy of a value somewhere else.</summary>
    Copy,

    /// <summary>Checks that a value equals the one given, and fails the patch if not.</summary>
    Test,
}

/// <summary>One operation of a <see cref="YamlPatch"/>, without its value.</summary>
/// <param name="Kind">What the operation does.</param>
/// <param name="Path">The RFC 6901 pointer the operation works on.</param>
/// <param name="From">For a move or copy, the pointer the value is taken from; otherwise <see langword="null"/>.</param>
public readonly record struct YamlPatchOperation(YamlPatchOperationKind Kind, string Path, string? From);

/// <summary>
/// An RFC 6902 patch built in code. Operations run in order and the patch is atomic: if one fails, the
/// document is left as it was. Values are converted when they are added, so a patch can be applied to
/// any number of documents.
/// </summary>
/// <example>
/// <code>
/// var patch = new YamlPatch()
///     .Test("/name", "web")
///     .Replace("/replicas", 3)
///     .Add("/labels/tier", "frontend");
/// document.ApplyPatch(patch);
/// </code>
/// </example>
public sealed class YamlPatch : IReadOnlyList<YamlPatchOperation>
{
    private readonly List<PatchStep> _steps = [];

    /// <summary>Creates an empty patch.</summary>
    public YamlPatch()
    {
    }

    /// <summary>The number of operations.</summary>
    public int Count => _steps.Count;

    internal IReadOnlyList<PatchStep> Steps => _steps;

    /// <summary>Gets an operation.</summary>
    /// <param name="index">The zero-based position.</param>
    public YamlPatchOperation this[int index]
    {
        get
        {
            PatchStep s = _steps[index];
            return new YamlPatchOperation((YamlPatchOperationKind)(int)s.Kind, s.Path, s.From);
        }
    }

    /// <summary>Parses a patch from JSON text, the form RFC 6902 defines.</summary>
    /// <param name="json">A JSON array of operations.</param>
    /// <param name="limits">The limits for nesting depth, or <see langword="null"/> for the defaults.</param>
    /// <returns>The patch.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is <see langword="null"/>.</exception>
    /// <exception cref="YamlException">The text is not a valid patch (<see cref="YamlErrorCategory.PatchInvalid"/>).</exception>
    public static YamlPatch Parse(string json, YamlLimits? limits = null)
    {
        YamlLimits l = limits ?? YamlLimits.Default;
        return FromSteps(PatchEngine.ReadOperations(PatchEngine.ParseJson(json, l), l));
    }

    /// <summary>Parses a patch from UTF-8 JSON, the form RFC 6902 defines.</summary>
    /// <param name="utf8Json">A JSON array of operations.</param>
    /// <param name="limits">The limits for nesting depth, or <see langword="null"/> for the defaults.</param>
    /// <returns>The patch.</returns>
    /// <exception cref="YamlException">The bytes are not a valid patch (<see cref="YamlErrorCategory.PatchInvalid"/>).</exception>
    public static YamlPatch Parse(ReadOnlySpan<byte> utf8Json, YamlLimits? limits = null)
    {
        YamlLimits l = limits ?? YamlLimits.Default;
        return FromSteps(PatchEngine.ReadOperations(PatchEngine.ParseJson(utf8Json, l), l));
    }

    /// <summary>Reads a patch from a JSON node tree.</summary>
    /// <param name="json">A <see cref="JsonArray"/> of operations.</param>
    /// <param name="limits">The limits for nesting depth, or <see langword="null"/> for the defaults.</param>
    /// <returns>The patch.</returns>
    /// <exception cref="YamlException">The tree is not a valid patch (<see cref="YamlErrorCategory.PatchInvalid"/>).</exception>
    public static YamlPatch FromJson(JsonNode? json, YamlLimits? limits = null)
    {
        YamlLimits l = limits ?? YamlLimits.Default;
        return FromSteps(PatchEngine.ReadOperations(json, l));
    }

    private static YamlPatch FromSteps(List<PatchStep> steps)
    {
        var patch = new YamlPatch();
        patch._steps.AddRange(steps);
        return patch;
    }

    /// <summary>Adds an <c>add</c> operation.</summary>
    /// <param name="path">An RFC 6901 pointer. For a mapping the member is added or replaced; for a sequence, the value is inserted before the item at the index, and <c>-</c> appends.</param>
    /// <param name="value">null, a string, a boolean, a number, a <see cref="YamlNode"/>, or a dictionary or sequence of these.</param>
    /// <returns>This patch.</returns>
    /// <exception cref="ArgumentException">The value contains a type that has no YAML form.</exception>
    public YamlPatch Add(string path, object? value) => Append(PatchKind.Add, path, null, value, hasValue: true);

    /// <summary>Adds a <c>remove</c> operation.</summary>
    /// <param name="path">An RFC 6901 pointer to the entry or item to remove.</param>
    /// <returns>This patch.</returns>
    public YamlPatch Remove(string path) => Append(PatchKind.Remove, path, null, null, hasValue: false);

    /// <summary>Adds a <c>replace</c> operation.</summary>
    /// <param name="path">An RFC 6901 pointer to a value that must exist.</param>
    /// <param name="value">The new value, as for <see cref="Add"/>.</param>
    /// <returns>This patch.</returns>
    /// <exception cref="ArgumentException">The value contains a type that has no YAML form.</exception>
    public YamlPatch Replace(string path, object? value) => Append(PatchKind.Replace, path, null, value, hasValue: true);

    /// <summary>Adds a <c>move</c> operation.</summary>
    /// <param name="from">An RFC 6901 pointer to the value to move.</param>
    /// <param name="path">An RFC 6901 pointer to where it goes. It must not lie inside the value.</param>
    /// <returns>This patch.</returns>
    public YamlPatch Move(string from, string path) => Append(PatchKind.Move, path, from, null, hasValue: false);

    /// <summary>Adds a <c>copy</c> operation.</summary>
    /// <param name="from">An RFC 6901 pointer to the value to copy.</param>
    /// <param name="path">An RFC 6901 pointer to where the copy goes.</param>
    /// <returns>This patch.</returns>
    public YamlPatch Copy(string from, string path) => Append(PatchKind.Copy, path, from, null, hasValue: false);

    /// <summary>
    /// Adds a <c>test</c> operation. The value is compared with the document's by the YAML data
    /// model under the Core schema: <c>1</c> and <c>1.0</c> differ, and <c>0x1F</c> equals <c>31</c>.
    /// </summary>
    /// <param name="path">An RFC 6901 pointer.</param>
    /// <param name="value">The expected value, as for <see cref="Add"/>.</param>
    /// <returns>This patch.</returns>
    /// <exception cref="ArgumentException">The value contains a type that has no YAML form.</exception>
    public YamlPatch Test(string path, object? value) => Append(PatchKind.Test, path, null, value, hasValue: true);

    private YamlPatch Append(PatchKind kind, string path, string? from, object? value, bool hasValue)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (kind is PatchKind.Move or PatchKind.Copy)
        {
            ArgumentNullException.ThrowIfNull(from);
        }

        ValueTree? tree = hasValue ? ValueTree.FromObject(value, YamlLimits.Default.MaxDepth) : null;
        _steps.Add(new PatchStep(kind, path, from, tree));
        return this;
    }

    /// <inheritdoc />
    public IEnumerator<YamlPatchOperation> GetEnumerator()
    {
        for (int i = 0; i < _steps.Count; i++)
        {
            yield return this[i];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
