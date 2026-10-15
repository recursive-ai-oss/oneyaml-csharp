using System;
using System.Collections.Generic;
using OneYaml.Compat.YamlDotNet.Core;
using OneYaml.Compat.YamlDotNet.Serialization;
using OneYaml.Compat.YamlDotNet.Serialization.NamingConventions;

namespace OneYaml.Compat.YamlDotNet.Internal;

// Everything a builder collects, frozen into one object when Build() is called.
internal sealed class Settings
{
    public INamingConvention Naming { get; set; } = NullNamingConvention.Instance;

    public INamingConvention EnumNaming { get; set; } = NullNamingConvention.Instance;

    public List<IYamlTypeConverter> Converters { get; private set; } = [];

    public Dictionary<string, Type> TagToType { get; private set; } = [];

    public Dictionary<Type, TagName> TypeToTag { get; private set; } = [];

    public Dictionary<(Type Type, string Member), List<Attribute>> Overrides { get; private set; } = [];

    public Dictionary<Type, Type> TypeMappings { get; private set; } = [];

    public Func<Type, object>? ObjectFactory { get; set; }

    public bool IgnoreFields { get; set; }

    public bool IncludeNonPublicProperties { get; set; }

    public bool EnablePrivateConstructors { get; set; }

    // Reading.
    public bool IgnoreUnmatched { get; set; }

    public bool CaseInsensitive { get; set; }

    public bool DuplicateKeyChecking { get; set; }

    public bool UnquotedStringTypeDeserialization { get; set; }

    public int MaxRecursion { get; set; }

    // Writing.
    public bool IndentedSequences { get; set; }

    public bool JsonCompatible { get; set; }

    public bool DisableAliases { get; set; }

    public bool EnsureRoundtrip { get; set; }

    public bool QuoteNecessaryStrings { get; set; }

    public bool QuoteNumericStrings { get; set; }

    public ScalarStyle DefaultScalarStyle { get; set; }

    public DefaultValuesHandling Defaults { get; set; }

    public string NewLine { get; set; } = "\n";

    public Settings Clone()
    {
        var copy = (Settings)MemberwiseClone();
        copy.Converters = [.. Converters];
        copy.TagToType = new Dictionary<string, Type>(TagToType);
        copy.TypeToTag = new Dictionary<Type, TagName>(TypeToTag);
        copy.Overrides = new Dictionary<(Type Type, string Member), List<Attribute>>(Overrides);
        copy.TypeMappings = new Dictionary<Type, Type>(TypeMappings);
        return copy;
    }
}
