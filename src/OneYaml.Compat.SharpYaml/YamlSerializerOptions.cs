using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Oy = global::OneYaml;

namespace OneYaml.Compat.SharpYaml;

/// <summary>Hints about the style of scalars in the output. This package cannot follow them: setting either to true is an error when writing (reading is not affected).</summary>
public sealed class YamlScalarStylePreferences
{
    /// <summary>Prefer plain scalars where they are valid.</summary>
    public bool PreferPlainStyle { get; set; }

    /// <summary>Quote scalars that could be read as another type.</summary>
    public bool PreferQuotedForAmbiguousScalars { get; set; }
}

/// <summary>Chooses the metadata of types. This package supports the reflection resolver only.</summary>
public interface IYamlTypeInfoResolver
{
    /// <summary>Gets the metadata of a type.</summary>
    /// <param name="type">The type.</param>
    /// <param name="options">The options in force.</param>
    /// <returns>The metadata, or <see langword="null"/> when the resolver has none.</returns>
    YamlTypeInfo? GetTypeInfo(Type type, YamlSerializerOptions options);
}

/// <summary>The options of <see cref="YamlSerializer"/>. An instance does not change once it is in use.</summary>
public sealed class YamlSerializerOptions : IEquatable<YamlSerializerOptions>
{
    private static readonly YamlSerializerOptions s_default = new();

    // SharpYaml stops at this depth unless the options say otherwise.
    private const int DefaultDepth = 64;

    // Engine options made for a ReferenceHandling other than None. Reading honours it (aliases are followed), writing
    // cannot, so a write through such options is refused instead of leaving out the anchors the program asked for.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Oy.YamlSerializerOptions, object> s_noWrite = new();

    private readonly object _gate = new();
    private Oy.YamlSerializerOptions? _engine;
    private int _indentSize = 2;

    /// <summary>Creates options with the defaults.</summary>
    public YamlSerializerOptions()
    {
    }

    /// <summary>Creates options with the values of other options.</summary>
    /// <param name="options">The options to copy.</param>
    public YamlSerializerOptions(YamlSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        PropertyNamingPolicy = options.PropertyNamingPolicy;
        SourceName = options.SourceName;
        DictionaryKeyPolicy = options.DictionaryKeyPolicy;
        PropertyNameCaseInsensitive = options.PropertyNameCaseInsensitive;
        UnmappedMemberHandling = options.UnmappedMemberHandling;
        PreferredObjectCreationHandling = options.PreferredObjectCreationHandling;
        DefaultIgnoreCondition = options.DefaultIgnoreCondition;
        WriteIndented = options.WriteIndented;
        _indentSize = options._indentSize;
        MappingOrder = options.MappingOrder;
        BlockSequenceMappingStyle = options.BlockSequenceMappingStyle;
        BlockSequenceSequenceStyle = options.BlockSequenceSequenceStyle;
        Schema = options.Schema;
        UseSchema = options.UseSchema;
        DuplicateKeyHandling = options.DuplicateKeyHandling;
        UnsafeAllowDeserializeFromTagTypeName = options.UnsafeAllowDeserializeFromTagTypeName;
        ScalarStylePreferences = options.ScalarStylePreferences;
        ReferenceHandling = options.ReferenceHandling;
        MaxDepth = options.MaxDepth;
        Limits = options.Limits;
        TypeInfoResolver = options.TypeInfoResolver;
    }

    /// <summary>The default options: member names as written, indented output, the Core schema.</summary>
    public static YamlSerializerOptions Default => s_default;

    /// <summary>How member names become keys; <see langword="null"/> keeps the names as written.</summary>
    public JsonNamingPolicy? PropertyNamingPolicy { get; init; }

    /// <summary>The name of the source, reported in errors.</summary>
    public string? SourceName { get; init; }

    /// <summary>How dictionary keys are converted. Not supported by this package: a policy other than null is an error.</summary>
    public JsonNamingPolicy? DictionaryKeyPolicy { get; init; }

    /// <summary>Whether keys are matched to members ignoring case.</summary>
    public bool PropertyNameCaseInsensitive { get; init; }

    /// <summary>What to do with a key that no member takes.</summary>
    public JsonUnmappedMemberHandling UnmappedMemberHandling { get; init; }

    /// <summary>Whether an existing collection is filled or replaced. Only <see cref="JsonObjectCreationHandling.Replace"/> is supported.</summary>
    public JsonObjectCreationHandling PreferredObjectCreationHandling { get; init; }

    /// <summary>When members are left out of the output.</summary>
    public YamlIgnoreCondition DefaultIgnoreCondition { get; init; }

    /// <summary>Whether the output is indented. The writer always writes indented block YAML, so <see langword="false"/> is an error when writing (reading is not affected).</summary>
    public bool WriteIndented { get; init; } = true;

    /// <summary>The number of spaces of one indent level, from 1 to 8.</summary>
    public int IndentSize
    {
        get => _indentSize;
        init => _indentSize = value is >= 1 and <= 8 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The indent size must be between 1 and 8.");
    }

    /// <summary>The order in which members are written.</summary>
    public YamlMappingOrderPolicy MappingOrder { get; init; }

    /// <summary>The layout of mappings in block sequences. Output always starts such a mapping on the line of the dash, so any other value is an error when writing (reading is not affected).</summary>
    public YamlSequenceItemStyle BlockSequenceMappingStyle { get; init; } = YamlSequenceItemStyle.Compact;

    /// <summary>The layout of sequences in block sequences. Output always puts a sequence inside a sequence in the layout described by <see cref="YamlSequenceItemStyle.Expanded"/>, so any other value is an error when writing (reading is not affected).</summary>
    public YamlSequenceItemStyle BlockSequenceSequenceStyle { get; init; } = YamlSequenceItemStyle.Expanded;

    /// <summary>The schema used when <see cref="UseSchema"/> is set. Only <see cref="YamlSchemaKind.Core"/> is supported.</summary>
    public YamlSchemaKind Schema { get; init; }

    /// <summary>Whether <see cref="Schema"/> applies. Reading always follows the Core schema, so a value of true is the same as false when the schema is Core.</summary>
    public bool UseSchema { get; init; }

    /// <summary>What to do with a repeated key. Only <see cref="YamlDuplicateKeyHandling.Error"/> is supported.</summary>
    public YamlDuplicateKeyHandling DuplicateKeyHandling { get; init; }

    /// <summary>Allows types to be named by tags. Not supported by this package: true is an error.</summary>
    public bool UnsafeAllowDeserializeFromTagTypeName { get; init; }

    /// <summary>Hints about the style of scalars. This package cannot follow them: setting either to true is an error when writing (reading is not affected).</summary>
    public YamlScalarStylePreferences ScalarStylePreferences { get; init; } = new();

    /// <summary>How shared objects are read and written. Aliases are always followed when reading, within the limits. Writing cannot keep sharing, so a value other than <see cref="YamlReferenceHandling.None"/> is an error when writing.</summary>
    public YamlReferenceHandling ReferenceHandling { get; init; }

    /// <summary>The greatest nesting depth; 0 means 64. The property reads back the value that was set. Reading stops at the lower of it and <see cref="global::OneYaml.YamlLimits.MaxDepth"/> of <see cref="Limits"/>, so a value above that limit has no effect.</summary>
    public int MaxDepth { get; init; }

    /// <summary>
    /// The limits reading keeps to: input size, nesting depth, alias expansion, integer length. <see langword="null"/> means
    /// <see cref="global::OneYaml.YamlLimits.Default"/>. Not in SharpYaml; set <see cref="global::OneYaml.YamlLimits.Untrusted"/> for input you do not control.
    /// <see cref="MaxDepth"/> can lower the depth further, never raise it above these limits.
    /// </summary>
    public global::OneYaml.YamlLimits? Limits { get; init; }

    /// <summary>The resolver of type metadata. Only the reflection resolver is supported.</summary>
    public IYamlTypeInfoResolver? TypeInfoResolver { get; init; }

    /// <inheritdoc/>
    public bool Equals(YamlSerializerOptions? other) => ReferenceEquals(this, other);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => ReferenceEquals(this, obj);

    /// <inheritdoc/>
    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);

    /// <inheritdoc/>
    public override string ToString() => $"YamlSerializerOptions {{ PropertyNamingPolicy = {PropertyNamingPolicy}, PropertyNameCaseInsensitive = {PropertyNameCaseInsensitive}, IndentSize = {IndentSize} }}";

    // The options of the engine, made once from these: the instance is read-only in effect, so what it builds can be kept.
    [RequiresUnreferencedCode("Reflection-based binding reads members and constructors that trimming can remove.")]
    [RequiresDynamicCode("Reflection-based binding makes generic types at run time.")]
    internal Oy.YamlSerializerOptions GetEngine()
    {
        Oy.YamlSerializerOptions? engine = Volatile.Read(ref _engine);
        if (engine is not null)
        {
            return engine;
        }

        // The check on the engine's own switch is spelled out here so a trimmed application, where that switch is
        // fixed to off, drops the reflection path below.
        if (!Oy.YamlSerializer.IsReflectionEnabledByDefault || !YamlSerializer.IsReflectionEnabledByDefault)
        {
            throw new InvalidOperationException("Reflection-based binding is turned off in this application. Pass metadata from a YamlTypeInfo or a YamlSerializerContext, or turn the OneYaml.YamlSerializer.IsReflectionEnabledByDefault switch on (and SharpYaml.YamlSerializer.IsReflectionEnabledByDefault, if the program sets it).");
        }

        lock (_gate)
        {
            return _engine ??= Build(new ReflectionResolver(this));
        }
    }

    private Oy.YamlSerializerOptions Build(Oy.IYamlTypeInfoResolver resolver)
    {
        Validate();
        Oy.YamlLimits limits = Limits ?? Oy.YamlLimits.Default;
        int depth = MaxDepth > 0 ? MaxDepth : DefaultDepth;
        if (depth < limits.MaxDepth)
        {
            limits = limits with { MaxDepth = depth };
        }

        var made = new Oy.YamlSerializerOptions
        {
            PropertyNamingPolicy = PropertyNamingPolicy,
            PropertyNameCaseInsensitive = PropertyNameCaseInsensitive,
            UnmappedMemberHandling = UnmappedMemberHandling == JsonUnmappedMemberHandling.Disallow ? Oy.YamlUnmappedMemberHandling.Disallow : Oy.YamlUnmappedMemberHandling.Skip,
            IndentSize = _indentSize,
            DefaultIgnoreCondition = DefaultIgnoreCondition switch
            {
                YamlIgnoreCondition.WhenWritingNull => Oy.YamlIgnoreCondition.WhenWritingNull,
                YamlIgnoreCondition.WhenWritingDefault => Oy.YamlIgnoreCondition.WhenWritingDefault,
                _ => Oy.YamlIgnoreCondition.Never,
            },
            Limits = limits,
            TypeInfoResolver = resolver,
        };
        if (ReferenceHandling != YamlReferenceHandling.None || UnsupportedLayout() is not null)
        {
            s_noWrite.Add(made, this);
        }

        return made;
    }

    internal static void RefuseReferenceHandlingWrite(Oy.YamlTypeInfo info)
    {
        if (!s_noWrite.TryGetValue(info.Options, out object? made))
        {
            return;
        }

        var options = (YamlSerializerOptions)made;
        if (options.ReferenceHandling != YamlReferenceHandling.None)
        {
            throw new NotSupportedException("ReferenceHandling other than None is not supported when writing with OneYaml.Compat.SharpYaml: shared objects are written in full each time and no anchors are written. Set ReferenceHandling to None to write (reading follows aliases with either value). See docs/compat/sharpyaml.md for what is and what is not covered.");
        }

        throw new NotSupportedException($"{options.UnsupportedLayout()} at a non-default value is not supported when writing with OneYaml.Compat.SharpYaml: the writer has one layout. Leave it at its default value to write (reading is not affected). See docs/compat/sharpyaml.md for what is and what is not covered.");
    }

    // An option the engine cannot honour is refused loudly: silently ignoring it would change what the program means.
    private void Validate()
    {
        if (DictionaryKeyPolicy is not null)
        {
            throw Unsupported(nameof(DictionaryKeyPolicy));
        }

        if (PreferredObjectCreationHandling != JsonObjectCreationHandling.Replace)
        {
            throw Unsupported(nameof(PreferredObjectCreationHandling));
        }

        if (DefaultIgnoreCondition is YamlIgnoreCondition.Always or YamlIgnoreCondition.WhenWriting or YamlIgnoreCondition.WhenReading)
        {
            throw Unsupported(nameof(DefaultIgnoreCondition));
        }

        if (DuplicateKeyHandling != YamlDuplicateKeyHandling.Error)
        {
            throw Unsupported(nameof(DuplicateKeyHandling));
        }

        if (UseSchema && Schema != YamlSchemaKind.Core)
        {
            throw Unsupported(nameof(Schema));
        }

        if (UnsafeAllowDeserializeFromTagTypeName)
        {
            throw Unsupported(nameof(UnsafeAllowDeserializeFromTagTypeName));
        }

        if (TypeInfoResolver is not null and not ReflectionYamlTypeInfoResolver)
        {
            throw Unsupported(nameof(TypeInfoResolver));
        }
    }

    // These options only choose a layout, and the writer has one layout. Reading is not affected, so one options object
    // can be shared for reading; writing with a different layout than asked for would hide that the output differs.
    private string? UnsupportedLayout() =>
        BlockSequenceMappingStyle is not (YamlSequenceItemStyle.Default or YamlSequenceItemStyle.Compact) ? nameof(BlockSequenceMappingStyle)
        : BlockSequenceSequenceStyle is not (YamlSequenceItemStyle.Default or YamlSequenceItemStyle.Expanded) ? nameof(BlockSequenceSequenceStyle)
        : ScalarStylePreferences is { PreferPlainStyle: true } or { PreferQuotedForAmbiguousScalars: true } ? nameof(ScalarStylePreferences)
        : !WriteIndented ? nameof(WriteIndented)
        : null;

    internal static NotSupportedException Unsupported(string what) =>
        new($"{what} is not supported by OneYaml.Compat.SharpYaml. Leave it at its default value. See docs/compat/sharpyaml.md for what is and what is not covered.");

    internal YamlMappingOrderPolicy Order => MappingOrder;
}
