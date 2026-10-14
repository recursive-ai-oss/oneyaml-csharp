namespace OneYaml;

/// <summary>The kind of problem a <see cref="YamlException"/> reports.</summary>
public enum YamlErrorCategory
{
    /// <summary>The text is not valid YAML.</summary>
    Syntax = 0,

    /// <summary>The input bytes or characters are not valid in their encoding.</summary>
    Encoding,

    /// <summary>A configured limit (depth, size, document count, alias expansion) was exceeded.</summary>
    Limit,

    /// <summary>A mapping has the same key twice.</summary>
    DuplicateKey,

    /// <summary>A tag cannot be resolved or is not allowed.</summary>
    UnresolvedTag,

    /// <summary>A value cannot be bound to the requested .NET type.</summary>
    Binding,

    /// <summary>A patch operation addresses a path that does not exist or cannot be used.</summary>
    PatchPath,

    /// <summary>A patch <c>test</c> operation found a different value.</summary>
    PatchTest,

    /// <summary>A patch is malformed or an operation is not valid.</summary>
    PatchInvalid,

    /// <summary>An edit would write through an alias, which is refused.</summary>
    RefusedAlias,

    /// <summary>An edit or pointer matches more than one place and is refused.</summary>
    RefusedAmbiguous,
}
