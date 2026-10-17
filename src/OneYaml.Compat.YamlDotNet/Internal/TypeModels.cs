using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.Serialization;
using OneYaml.Compat.YamlDotNet.Core;
using OneYaml.Compat.YamlDotNet.Serialization;

namespace OneYaml.Compat.YamlDotNet.Internal;

internal enum TypeKind
{
    Object,
    Scalar,
    Enum,
    Array,
    Collection,
    Dictionary,
    Convertible,
    Poco,
}

// One property or field of a type, as the serializer sees it.
[RequiresUnreferencedCode("Reads members of arbitrary types by reflection.")]
[RequiresDynamicCode("Reads members of arbitrary types by reflection.")]
internal sealed class MemberModel
{
    private readonly PropertyInfo? _property;
    private readonly FieldInfo? _field;

    public MemberModel(MemberInfo member, string key, Type type, bool canWrite)
    {
        Member = member;
        _property = member as PropertyInfo;
        _field = member as FieldInfo;
        Key = key;
        Type = type;
        CanWrite = canWrite;
    }

    public MemberInfo Member { get; }

    public string Key { get; }

    public Type Type { get; }

    public bool CanWrite { get; }

    public int Order { get; set; }

    public ScalarStyle Style { get; set; }

    public DefaultValuesHandling? Handling { get; set; }

    public bool HasDefaultValue { get; set; }

    public object? DefaultValue { get; set; }

    public Type? SerializeAs { get; set; }

    public object? Get(object instance) => _property is not null ? _property.GetValue(instance) : _field!.GetValue(instance);

    public void Set(object instance, object? value)
    {
        if (_property is not null)
        {
            _property.SetValue(instance, value);
        }
        else
        {
            _field!.SetValue(instance, value);
        }
    }
}

[RequiresUnreferencedCode("Reads members of arbitrary types by reflection.")]
[RequiresDynamicCode("Reads members of arbitrary types by reflection.")]
internal sealed class TypeModel
{
    public TypeModel(Type type)
    {
        Type = type;
    }

    public Type Type { get; }

    public TypeKind Kind { get; set; }

    // For Nullable<T>: T. The binder reads the underlying type and maps null to null.
    public Type? Underlying { get; set; }

    public Type? Element { get; set; }

    public Type? KeyType { get; set; }

    public Type? ValueType { get; set; }

    // The class to create when Type is an interface or abstract.
    public Type? Concrete { get; set; }

    public MemberModel[] Members { get; set; } = [];

    public Dictionary<string, MemberModel> ByKey { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, MemberModel>? ByKeyIgnoreCase { get; set; }

    public MethodInfo? Add { get; set; }

    public bool IsList { get; set; }

    public bool IsDictionary { get; set; }

    public IYamlTypeConverter? AttributeConverter { get; set; }

    public Dictionary<string, object>? EnumByName { get; set; }

    public Dictionary<object, string>? EnumToName { get; set; }
}

[RequiresUnreferencedCode("Reads members of arbitrary types by reflection.")]
[RequiresDynamicCode("Reads members of arbitrary types by reflection.")]
internal sealed class TypeModels
{
    private readonly Settings _settings;
    private readonly ConcurrentDictionary<Type, TypeModel> _cache = new();

    public TypeModels(Settings settings)
    {
        _settings = settings;
    }

    public Settings Settings => _settings;

    public TypeModel Get(Type type) => _cache.GetOrAdd(type, Build);

    public object? Create(Type type)
    {
        if (_settings.ObjectFactory is not null)
        {
            return _settings.ObjectFactory(type);
        }

        return Activator.CreateInstance(type, _settings.EnablePrivateConstructors);
    }

    public static bool IsScalarType(Type t) =>
        t.IsPrimitive || t == typeof(string) || t == typeof(decimal) || t == typeof(DateTime) || t == typeof(DateTimeOffset)
        || t == typeof(Guid) || t == typeof(TimeSpan) || t == typeof(Uri) || t == typeof(Version) || t == typeof(DateOnly)
        || t == typeof(TimeOnly) || t == typeof(BigInteger) || t == typeof(Int128) || t == typeof(UInt128) || t == typeof(Half);

    private TypeModel Build(Type type)
    {
        var model = new TypeModel(type);
        Type? nullable = Nullable.GetUnderlyingType(type);
        if (nullable is not null)
        {
            model = Get(nullable);
            return model;
        }

        if (type == typeof(object))
        {
            model.Kind = TypeKind.Object;
            return model;
        }

        if (type.GetCustomAttribute<YamlConverterAttribute>() is { } converterAttribute)
        {
            model.AttributeConverter = (IYamlTypeConverter?)Activator.CreateInstance(converterAttribute.ConverterType);
        }

        if (type.IsEnum)
        {
            model.Kind = TypeKind.Enum;
            BuildEnum(model, type);
            return model;
        }

        if (IsScalarType(type))
        {
            model.Kind = TypeKind.Scalar;
            return model;
        }

        if (typeof(IYamlConvertible).IsAssignableFrom(type) && !type.IsInterface && !type.IsAbstract)
        {
            model.Kind = TypeKind.Convertible;
            return model;
        }

        if (type.IsArray && type.GetArrayRank() == 1)
        {
            model.Kind = TypeKind.Array;
            model.Element = type.GetElementType();
            return model;
        }

        if (TryDictionary(model, type) || TryCollection(model, type))
        {
            return model;
        }

        model.Kind = TypeKind.Poco;
        if (_settings.TypeMappings.TryGetValue(type, out Type? mapped))
        {
            model.Concrete = mapped;
        }

        BuildMembers(model, type);
        return model;
    }

    private void BuildEnum(TypeModel model, Type type)
    {
        model.EnumByName = new Dictionary<string, object>(StringComparer.Ordinal);
        model.EnumToName = [];
        foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            object value = field.GetValue(null)!;
            string name = field.GetCustomAttribute<EnumMemberAttribute>()?.Value ?? _settings.EnumNaming.Apply(field.Name);
            model.EnumByName[name] = value;
            model.EnumToName.TryAdd(value, name);
        }
    }

    private static Type? FindGeneric(Type type, Type definition)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == definition)
        {
            return type;
        }

        return type.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == definition);
    }

    private bool TryDictionary(TypeModel model, Type type)
    {
        Type? generic = FindGeneric(type, typeof(IDictionary<,>)) ?? FindGeneric(type, typeof(IReadOnlyDictionary<,>));
        bool nonGeneric = typeof(IDictionary).IsAssignableFrom(type) || type == typeof(IDictionary);
        if (generic is null && !nonGeneric)
        {
            return false;
        }

        model.Kind = TypeKind.Dictionary;
        Type[] args = generic?.GetGenericArguments() ?? [typeof(object), typeof(object)];
        model.KeyType = args[0];
        model.ValueType = args[1];
        if (type.IsInterface || type.IsAbstract)
        {
            model.Concrete = _settings.TypeMappings.TryGetValue(type, out Type? mapped)
                ? mapped
                : typeof(Dictionary<,>).MakeGenericType(args);
        }

        Type concrete = model.Concrete ?? type;
        model.IsDictionary = typeof(IDictionary).IsAssignableFrom(concrete);
        if (!model.IsDictionary)
        {
            model.Add = concrete.GetMethod("Add", [args[0], args[1]]);
        }

        return true;
    }

    private bool TryCollection(TypeModel model, Type type)
    {
        if (type == typeof(string) || !typeof(IEnumerable).IsAssignableFrom(type))
        {
            return false;
        }

        Type? generic = FindGeneric(type, typeof(IEnumerable<>));
        Type element = generic?.GetGenericArguments()[0] ?? typeof(object);
        model.Kind = TypeKind.Collection;
        model.Element = element;
        if (type.IsInterface || type.IsAbstract)
        {
            if (_settings.TypeMappings.TryGetValue(type, out Type? mapped))
            {
                model.Concrete = mapped;
            }
            else if (FindGeneric(type, typeof(ISet<>)) is not null || (type.IsGenericType && type.GetGenericTypeDefinition().Name.StartsWith("IReadOnlySet", StringComparison.Ordinal)))
            {
                model.Concrete = typeof(HashSet<>).MakeGenericType(element);
            }
            else
            {
                model.Concrete = typeof(List<>).MakeGenericType(element);
            }
        }

        Type concrete = model.Concrete ?? type;
        model.IsList = typeof(IList).IsAssignableFrom(concrete) && !concrete.IsArray;
        if (!model.IsList)
        {
            model.Add = concrete.GetMethod("Add", [element]);
            if (model.Add is null)
            {
                // Not something that can be filled (a read-only or custom enumerable): fall back to mapping like an object.
                model.Kind = TypeKind.Poco;
                BuildMembers(model, type);
            }
        }

        return true;
    }

    private void BuildMembers(TypeModel model, Type type)
    {
        BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
        var list = new List<MemberModel>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (!_settings.IgnoreFields)
        {
            foreach (FieldInfo f in type.GetFields(flags))
            {
                if (f.IsStatic || f.GetCustomAttribute<System.Runtime.CompilerServices.CompilerGeneratedAttribute>() is not null)
                {
                    continue;
                }

                AddMember(list, seen, f, f.FieldType, !f.IsInitOnly && !f.IsLiteral);
            }
        }

        BindingFlags propertyFlags = _settings.IncludeNonPublicProperties ? flags | BindingFlags.NonPublic : flags;
        foreach (PropertyInfo p in type.GetProperties(propertyFlags))
        {
            if (p.GetIndexParameters().Length != 0 || p.GetMethod is null || (!_settings.IncludeNonPublicProperties && !p.GetMethod.IsPublic))
            {
                continue;
            }

            bool canWrite = p.SetMethod is not null && (_settings.IncludeNonPublicProperties || p.SetMethod.IsPublic);
            AddMember(list, seen, p, p.PropertyType, canWrite);
        }

        model.Members = [.. list.OrderBy(m => m.Order)];
        foreach (MemberModel m in model.Members)
        {
            if (m.CanWrite)
            {
                model.ByKey.TryAdd(m.Key, m);
            }
        }

        if (_settings.CaseInsensitive)
        {
            model.ByKeyIgnoreCase = new Dictionary<string, MemberModel>(StringComparer.OrdinalIgnoreCase);
            foreach (MemberModel m in model.Members)
            {
                if (m.CanWrite)
                {
                    model.ByKeyIgnoreCase.TryAdd(m.Key, m);
                }
            }
        }
    }

    private void AddMember(List<MemberModel> list, HashSet<string> seen, MemberInfo member, Type type, bool canWrite)
    {
        if (!seen.Add(member.Name))
        {
            return;
        }

        List<Attribute>? overrides = null;
        foreach (var entry in _settings.Overrides)
        {
            if (entry.Key.Member == member.Name && entry.Key.Type.IsAssignableFrom(member.ReflectedType))
            {
                (overrides ??= []).AddRange(entry.Value);
            }
        }

        bool ignored = member.GetCustomAttribute<Serialization.YamlIgnoreAttribute>() is not null;
        YamlMemberAttribute? attribute = member.GetCustomAttribute<YamlMemberAttribute>();
        if (overrides is not null)
        {
            foreach (Attribute a in overrides)
            {
                if (a is Serialization.YamlIgnoreAttribute)
                {
                    ignored = true;
                }
                else if (a is YamlMemberAttribute m)
                {
                    attribute = m;
                }
            }
        }

        if (ignored)
        {
            return;
        }

        string key = attribute?.Alias is { } alias
            ? alias
            : attribute is { ApplyNamingConventions: false } ? member.Name : _settings.Naming.Apply(member.Name);
        var model = new MemberModel(member, key, type, canWrite)
        {
            Order = attribute?.Order ?? 0,
            Style = attribute?.ScalarStyle ?? ScalarStyle.Any,
            SerializeAs = attribute?.SerializeAs,
        };
        if (attribute is { IsDefaultValuesHandlingSpecified: true })
        {
            model.Handling = attribute.DefaultValuesHandling;
        }

        if (member.GetCustomAttribute<DefaultValueAttribute>() is { } dv)
        {
            model.HasDefaultValue = true;
            model.DefaultValue = dv.Value;
        }

        list.Add(model);
    }
}
