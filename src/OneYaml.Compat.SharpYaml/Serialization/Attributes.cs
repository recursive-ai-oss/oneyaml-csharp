using System;
using System.Globalization;

namespace OneYaml.Compat.SharpYaml.Serialization;

/// <summary>The base class of the attributes that shape how a type is read and written.</summary>
[AttributeUsage(AttributeTargets.All, AllowMultiple = false)]
public abstract class YamlAttribute : Attribute
{
}

/// <summary>Sets the key a member is read from and written to; the naming policy does not apply to it.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
public sealed class YamlPropertyNameAttribute : YamlAttribute
{
    /// <summary>Creates the attribute.</summary>
    /// <param name="name">The key.</param>
    public YamlPropertyNameAttribute(string name) => Name = name;

    /// <summary>The key.</summary>
    public string Name { get; }
}

/// <summary>Leaves a member out of the output, always or under a condition.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
public sealed class YamlIgnoreAttribute : YamlAttribute
{
    /// <summary>Creates the attribute; the member is always left out.</summary>
    public YamlIgnoreAttribute()
    {
        Condition = YamlIgnoreCondition.Always;
    }

    /// <summary>When the member is left out.</summary>
    public YamlIgnoreCondition Condition { get; set; }
}

/// <summary>Includes a member that is not public.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
public sealed class YamlIncludeAttribute : YamlAttribute
{
}

/// <summary>Makes a member required when reading.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
public sealed class YamlRequiredAttribute : YamlAttribute
{
}

/// <summary>Sets the position of a member in the output; lower numbers come first.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
public sealed class YamlPropertyOrderAttribute : YamlAttribute
{
    /// <summary>Creates the attribute.</summary>
    /// <param name="order">The position.</param>
    public YamlPropertyOrderAttribute(int order) => Order = order;

    /// <summary>The position.</summary>
    public int Order { get; }
}

/// <summary>Chooses the constructor used to create the object.</summary>
[AttributeUsage(AttributeTargets.Constructor, AllowMultiple = false)]
public sealed class YamlConstructorAttribute : YamlAttribute
{
}

/// <summary>Sets the layout of the collection items of a block sequence. It changes layout only and has no effect here.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
public sealed class YamlBlockSequenceItemStyleAttribute : YamlAttribute
{
    /// <summary>Creates the attribute with the default layout.</summary>
    public YamlBlockSequenceItemStyleAttribute()
    {
    }

    /// <summary>Creates the attribute with one layout for both kinds of item.</summary>
    /// <param name="style">The layout.</param>
    public YamlBlockSequenceItemStyleAttribute(YamlSequenceItemStyle style)
    {
        MappingStyle = style;
        SequenceStyle = style;
    }

    /// <summary>The layout of mapping items.</summary>
    public YamlSequenceItemStyle MappingStyle { get; set; }

    /// <summary>The layout of sequence items.</summary>
    public YamlSequenceItemStyle SequenceStyle { get; set; }
}

/// <summary>Lists a type for a serializer context. This package has no source generator for it; the attribute is accepted so that code compiles.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class YamlSerializableAttribute : YamlAttribute
{
    /// <summary>Creates the attribute.</summary>
    /// <param name="type">The type.</param>
    public YamlSerializableAttribute(Type type) => Type = type;

    /// <summary>The type.</summary>
    public Type Type { get; }

    /// <summary>The name of the generated property for the type's metadata.</summary>
    public string? TypeInfoPropertyName { get; set; }
}
