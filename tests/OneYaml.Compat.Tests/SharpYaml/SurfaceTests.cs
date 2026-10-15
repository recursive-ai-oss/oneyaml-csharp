using System.Reflection;
using Xunit;
using Cs = global::OneYaml.Compat.SharpYaml;
using Rs = global::SharpYaml;

namespace OneYaml.Compat.Tests.SharpYamlCompat;

// Compares the public surface of the package with that of SharpYaml, so what is left out is a list that a test keeps true.
public class SharpYamlSurfaceTests
{
    // The types of SharpYaml 3 that the package has. Their members are compared by name.
    private static readonly string[] Shared =
    [
        "YamlSerializer", "YamlSerializerOptions", "YamlException", "SyntaxErrorException", "SemanticErrorException", "Mark",
        "YamlIgnoreCondition", "YamlMappingOrderPolicy", "YamlReferenceHandling", "YamlSchemaKind", "YamlDuplicateKeyHandling",
        "YamlSequenceItemStyle", "YamlStyle",
        "YamlScalarStylePreferences", "IYamlTypeInfoResolver", "ReflectionYamlTypeInfoResolver",
        "YamlTypeInfo", "YamlTypeInfo`1",
        "Serialization.YamlSerializerContext", "Serialization.YamlAttribute", "Serialization.YamlPropertyNameAttribute", "Serialization.YamlIgnoreAttribute",
        "Serialization.YamlIncludeAttribute", "Serialization.YamlRequiredAttribute", "Serialization.YamlPropertyOrderAttribute",
        "Serialization.YamlConstructorAttribute", "Serialization.YamlBlockSequenceItemStyleAttribute",
        "Serialization.YamlSerializableAttribute",
    ];

    // Public members of those types that the package does not have, with the reason.
    private static readonly Dictionary<string, string> LeftOut = new(StringComparer.Ordinal)
    {
        ["YamlSerializerOptions.Converters"] = "custom converters are written against the reader and writer of SharpYaml, which the package does not have",
        ["YamlTypeInfo.Write"] = "reads and writes go through the engine, not through SharpYaml's reader and writer",
        ["YamlTypeInfo.ReadAsObject"] = "reads and writes go through the engine, not through SharpYaml's reader and writer",
        ["YamlTypeInfo`1.Write"] = "reads and writes go through the engine, not through SharpYaml's reader and writer",
        ["YamlTypeInfo`1.ReadAsObject"] = "reads and writes go through the engine, not through SharpYaml's reader and writer",
        ["YamlTypeInfo`1.Read"] = "reads and writes go through the engine, not through SharpYaml's reader and writer",
        ["YamlSerializerOptions.PolymorphismOptions"] = "polymorphic binding is not offered; a program that uses it fails to compile",
        ["YamlSerializerOptions.Clone"] = "compiler-generated copy of a record-like type",
        ["YamlSerializerOptions.<Clone>$"] = "compiler-generated copy of a record-like type",
        ["YamlException.Data"] = "inherited",
    };

    private static string KeyOf(Type t) => t.FullName![(t.FullName!.StartsWith("SharpYaml.", StringComparison.Ordinal) ? "SharpYaml.".Length : 0)..];

    private static string CompatKeyOf(Type t) => t.FullName!["OneYaml.Compat.SharpYaml.".Length..];

    private static IEnumerable<string> Members(Type t)
    {
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (MemberInfo m in t.GetMembers(Flags))
        {
            if (m is MethodInfo { IsSpecialName: true } || m is ConstructorInfo || m is Type)
            {
                continue;
            }

            if (m.Name is "Equals" or "GetHashCode" or "ToString" or "PrintMembers" or "EqualityContract" or "Deconstruct" or "op_Equality" or "op_Inequality")
            {
                continue;
            }

            yield return m.Name;
        }
    }

    [Fact]
    public void The_package_has_the_members_of_the_shared_types_but_those_left_out()
    {
        Dictionary<string, Type> real = typeof(Rs.YamlSerializer).Assembly.GetExportedTypes().ToDictionary(KeyOf);
        Dictionary<string, Type> compat = typeof(Cs.YamlSerializer).Assembly.GetExportedTypes().ToDictionary(CompatKeyOf);
        var problems = new List<string>();
        foreach (string name in Shared)
        {
            if (!real.TryGetValue(name, out Type? r))
            {
                problems.Add($"{name}: not in SharpYaml 3.14.0");
                continue;
            }

            if (!compat.TryGetValue(name, out Type? c))
            {
                problems.Add($"{name}: missing from the package");
                continue;
            }

            var have = new HashSet<string>(Members(c), StringComparer.Ordinal);
            foreach (string member in Members(r))
            {
                if (!have.Contains(member) && !LeftOut.ContainsKey(name + "." + member))
                {
                    problems.Add($"{name}.{member}: missing from the package");
                }
            }

            if (r.IsEnum)
            {
                Assert.Equal(Enum.GetNames(r), Enum.GetNames(c));
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void Every_public_type_of_the_package_has_a_namesake_in_SharpYaml_or_is_listed()
    {
        Dictionary<string, Type> real = typeof(Rs.YamlSerializer).Assembly.GetExportedTypes().ToDictionary(KeyOf);
        var extra = typeof(Cs.YamlSerializer).Assembly.GetExportedTypes()
            .Select(CompatKeyOf)
            .Where(n => !real.ContainsKey(n))
            .ToList();
        Assert.True(extra.Count == 0, "types the real library does not have: " + string.Join(", ", extra));
    }
}
