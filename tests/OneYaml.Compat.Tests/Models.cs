using System.ComponentModel;
using Cm = global::OneYaml.Compat.YamlDotNet.Serialization;
using CmCore = global::OneYaml.Compat.YamlDotNet.Core;
using Rl = global::YamlDotNet.Serialization;
using RlCore = global::YamlDotNet.Core;

namespace OneYaml.Compat.Tests;

public enum Color
{
    Red,
    Green,
    DarkBlue,
}

public class Person
{
    public string? Name { get; set; }

    public int Age { get; set; }

    public List<string>? Tags { get; set; }

    public Person? Nested { get; set; }

    public int Fld;

    public double Score { get; set; }

    public bool Active { get; set; }

    public Color Favourite { get; set; }

    public int? Optional { get; set; }

    public decimal Money { get; set; }

    public Dictionary<string, int>? Counts { get; set; }

    public int[]? Numbers { get; set; }

    public string FirstName { get; set; } = "";

    public string HTTPServerName { get; set; } = "";
}

public class Base
{
    public string? BaseValue { get; set; }
}

public class Derived : Base
{
    public int DerivedValue { get; set; }
}

public class Attributed
{
    public int Plain { get; set; }

    [Rl.YamlMember(Alias = "renamed")]
    [Cm.YamlMember(Alias = "renamed")]
    public int Aliased { get; set; }

    [Rl.YamlMember(Order = -1)]
    [Cm.YamlMember(Order = -1)]
    public int First { get; set; }

    [Rl.YamlIgnore]
    [Cm.YamlIgnore]
    public int Hidden { get; set; }

    [Rl.YamlMember(ScalarStyle = RlCore.ScalarStyle.DoubleQuoted)]
    [Cm.YamlMember(ScalarStyle = CmCore.ScalarStyle.DoubleQuoted)]
    public string? Quoted { get; set; }

    [Rl.YamlMember(ScalarStyle = RlCore.ScalarStyle.SingleQuoted)]
    [Cm.YamlMember(ScalarStyle = CmCore.ScalarStyle.SingleQuoted)]
    public string? Single { get; set; }

    [Rl.YamlMember(ScalarStyle = RlCore.ScalarStyle.Literal)]
    [Cm.YamlMember(ScalarStyle = CmCore.ScalarStyle.Literal)]
    public string? Block { get; set; }

    [Rl.YamlMember(DefaultValuesHandling = Rl.DefaultValuesHandling.OmitDefaults)]
    [Cm.YamlMember(DefaultValuesHandling = Cm.DefaultValuesHandling.OmitDefaults)]
    public int OmittedWhenZero { get; set; }

    [Rl.YamlMember(Alias = "keep_name", ApplyNamingConventions = false)]
    [Cm.YamlMember(Alias = "keep_name", ApplyNamingConventions = false)]
    public int AliasedNoConvention { get; set; }

    [DefaultValue(5)]
    public int WithDefault { get; set; } = 5;
}

public class Holder
{
    public IList<string>? Items { get; set; }

    public IDictionary<string, object>? Bag { get; set; }

    public IEnumerable<int>? Numbers { get; set; }

    public HashSet<string>? Set { get; set; }

    public object? Anything { get; set; }

    public List<Person>? People { get; set; }

    public Dictionary<string, List<int>>? Lists { get; set; }
}

public struct Point
{
    public int X { get; set; }

    public int Y { get; set; }
}
