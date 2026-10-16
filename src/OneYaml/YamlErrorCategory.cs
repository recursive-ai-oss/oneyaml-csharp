namespace OneYaml;

/// <summary>The kind of problem a <see cref="YamlException"/> reports.</summary>
public enum YamlErrorCategory
{
    /// <summary>No category was given. The serializer reports an exception that a converter throws this way as <see cref="Binding"/>.</summary>
    None = 0,

    /// <summary>The text is not valid YAML.</summary>
    Syntax,

    /// <summary>The input bytes or characters are not valid in their encoding.</summary>
    Encoding,

    /// <summary>A configured limit (depth, size, document count, alias expansion) was exceeded.</summary>
    Limit,

    /// <summary>
    /// A mapping has the same key twice. Keys are compared by value after Core resolution, so <c>1</c> and <c>0x1</c> are
    /// one key. A float literal beyond the double range keeps its own value (<c>1e400</c> and <c>2e400</c> differ), except that
    /// literals which round to the same subnormal double, such as <c>3e-324</c> and <c>4e-324</c>, are one key.
    /// </summary>
    DuplicateKey,

    /// <summary>A tag cannot be resolved or is not allowed.</summary>
    UnresolvedTag,

    /// <summary>A value cannot be bound to the requested .NET type.</summary>
    Binding,

    /// <summary>A pointer or path names nothing or cannot be used: a patch operation, <see cref="YamlDocument.Get{T}(string, int)"/>, <see cref="YamlDocument.GetNode(string, int)"/> and every other pointer method share this category.</summary>
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
