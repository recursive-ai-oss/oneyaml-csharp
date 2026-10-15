namespace OneYaml.Compat.SharpYaml;

/// <summary>When a member is left out of the output.</summary>
public enum YamlIgnoreCondition
{
    /// <summary>The member is always written and read.</summary>
    Never,

    /// <summary>The member is not written when it is null.</summary>
    WhenWritingNull,

    /// <summary>The member is not written when it has its default value.</summary>
    WhenWritingDefault,

    /// <summary>The member is neither written nor read.</summary>
    Always,

    /// <summary>The member is not written, but it is read.</summary>
    WhenWriting,

    /// <summary>The member is written, but it is not read.</summary>
    WhenReading,
}

/// <summary>The order in which the members of an object are written.</summary>
public enum YamlMappingOrderPolicy
{
    /// <summary>In the order of declaration.</summary>
    Declaration,

    /// <summary>Sorted by the written name.</summary>
    Sorted,
}

/// <summary>How shared objects are read and written.</summary>
public enum YamlReferenceHandling
{
    /// <summary>Shared objects are written again each time.</summary>
    None,

    /// <summary>Anchors and aliases are used for shared objects.</summary>
    Preserve,

    /// <summary>Anchors and aliases are used only where an object is shared.</summary>
    PreserveMinimal,
}

/// <summary>The scalar schema.</summary>
public enum YamlSchemaKind
{
    /// <summary>The YAML 1.2 Core schema.</summary>
    Core,

    /// <summary>The YAML 1.2 JSON schema.</summary>
    Json,

    /// <summary>The YAML 1.2 Failsafe schema.</summary>
    Failsafe,

    /// <summary>The Core schema with the YAML 1.1 additions.</summary>
    Extended,
}

/// <summary>What to do with a repeated key in a mapping.</summary>
public enum YamlDuplicateKeyHandling
{
    /// <summary>The repeated key is an error.</summary>
    Error,

    /// <summary>The last value is kept.</summary>
    LastWins,

    /// <summary>The first value is kept.</summary>
    FirstWins,
}

/// <summary>How the items of a block sequence that are collections are laid out.</summary>
public enum YamlSequenceItemStyle
{
    /// <summary>The default layout.</summary>
    Default,

    /// <summary>The item starts on its own line.</summary>
    Expanded,

    /// <summary>The item starts on the line of the dash.</summary>
    Compact,
}

/// <summary>The layout of a collection.</summary>
public enum YamlStyle
{
    /// <summary>The writer chooses.</summary>
    Any,

    /// <summary>Block style.</summary>
    Block,

    /// <summary>Flow style.</summary>
    Flow,
}

