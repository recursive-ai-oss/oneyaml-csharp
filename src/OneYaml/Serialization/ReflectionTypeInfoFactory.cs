using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using OneYaml.Serialization.Metadata;

namespace OneYaml;

/// <summary>
/// Finds type metadata by reflection. Using it needs code that reflection can reach, so it is
/// unavailable to trimmed or ahead-of-time compiled programs that do not keep their types; those use
/// generated metadata instead.
/// </summary>
public sealed class DefaultYamlTypeInfoResolver : IYamlTypeInfoResolver
{
    /// <summary>Creates the resolver.</summary>
    [RequiresUnreferencedCode("Reflection-based binding reads members and constructors that trimming can remove.")]
    [RequiresDynamicCode("Reflection-based binding makes generic types at run time.")]
    public DefaultYamlTypeInfoResolver()
    {
    }

    /// <inheritdoc/>
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The constructor of this type carries the annotation, so reaching an instance means the caller accepted it.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "The constructor of this type carries the annotation, so reaching an instance means the caller accepted it.")]
    public YamlTypeInfo? GetTypeInfo(Type type, YamlSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(options);
        return ReflectionTypeInfoFactory.Create(type, options);
    }
}

[RequiresUnreferencedCode("Reflection-based binding reads members and constructors that trimming can remove.")]
[RequiresDynamicCode("Reflection-based binding makes generic types at run time.")]
internal static class ReflectionTypeInfoFactory
{
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    [ThreadStatic]
    private static int s_depth;

    public static YamlTypeInfo Create(Type type, YamlSerializerOptions options)
    {
        if (++s_depth > 64)
        {
            s_depth = 0;
            throw new NotSupportedException($"Type '{type}' contains itself through its collection types, which cannot be described.");
        }

        try
        {
            return CreateCore(type, options);
        }
        catch (Exception ex) when (ex is ArgumentException or TypeLoadException or MemberAccessException or InvalidProgramException)
        {
            // A raw reflection failure names an internal method, which tells a user nothing; say which type could not be described.
            throw new NotSupportedException($"Type '{type}' cannot be bound by reflection: it is not a class or struct with a public constructor, a supported collection or dictionary, or an enum. Give it a converter or a constructor.", ex);
        }
        finally
        {
            if (s_depth > 0)
            {
                s_depth--;
            }
        }
    }

    private static YamlTypeInfo CreateCore(Type type, YamlSerializerOptions options)
    {
        if (type.IsPointer || type.IsByRef || type.IsByRefLike)
        {
            throw new NotSupportedException($"Type '{type}' cannot be bound.");
        }

        foreach (YamlConverter c in options.Converters)
        {
            if (!c.CanConvert(type))
            {
                continue;
            }

            YamlConverter? chosen = c is YamlConverterFactory factory ? factory.CreateConverter(type, options) : c;
            if (chosen is null)
            {
                continue;
            }

            return Call(nameof(FromConverter), [type], options, chosen);
        }

        if (type.GetCustomAttribute<YamlConverterAttribute>(false) is { } converterAttribute)
        {
            return Call(nameof(MakeFromAttribute), [type], options, Instantiate(converterAttribute.ConverterType, type));
        }

        if (FindPolymorphism(type) is { } polymorphism)
        {
            return Call(nameof(MakePolymorphic), [type], options, polymorphism);
        }

        if (BuiltInConverters.CreateLeaf(type) is { } leaf)
        {
            return Call(nameof(FromConverter), [type], options, leaf);
        }

        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            return Call(nameof(MakeNullable), [underlying], options);
        }

        if (type.IsEnum)
        {
            return Call(nameof(MakeEnum), [type], options);
        }

        if (type.IsArray)
        {
            if (type.GetArrayRank() != 1)
            {
                throw new NotSupportedException($"Type '{type}' is a multi-dimensional array, which cannot be bound.");
            }

            return Call(nameof(MakeArray), [type.GetElementType()!], options);
        }

        if (type.IsGenericType && TryCreateGeneric(type, options) is { } generic)
        {
            return generic;
        }

        if (!type.IsInterface && !type.IsAbstract && TryCreateOther(type, options) is { } other)
        {
            return other;
        }

        return Call(nameof(MakeObject), [type], options);
    }

    private static YamlTypeInfo? TryCreateGeneric(Type type, YamlSerializerOptions options)
    {
        Type def = type.GetGenericTypeDefinition();
        Type[] args = type.GetGenericArguments();
        if (def == typeof(List<>))
        {
            return Call(nameof(MakeList), args, options);
        }

        if (def == typeof(Dictionary<,>))
        {
            return Call(nameof(MakeDictionary), args, options);
        }

        if (def == typeof(HashSet<>))
        {
            return Call(nameof(MakeHashSet), args, options);
        }

        if (def.Namespace == "System.Collections.Immutable" && TryCreateImmutable(def, args, options) is { } immutable)
        {
            return immutable;
        }

        if (def == typeof(IList<>) || def == typeof(ICollection<>) || def == typeof(IEnumerable<>)
            || def == typeof(IReadOnlyList<>) || def == typeof(IReadOnlyCollection<>))
        {
            return Call(nameof(MakeAdapted), [type, typeof(List<>).MakeGenericType(args)], options);
        }

        if (def == typeof(ISet<>) || def == typeof(IReadOnlySet<>))
        {
            return Call(nameof(MakeAdapted), [type, typeof(HashSet<>).MakeGenericType(args)], options);
        }

        if (def == typeof(IDictionary<,>) || def == typeof(IReadOnlyDictionary<,>))
        {
            return Call(nameof(MakeAdapted), [type, typeof(Dictionary<,>).MakeGenericType(args)], options);
        }

        return null;
    }

    // The immutable collections are built from a list (or a dictionary) read the usual way. An interface
    // such as IImmutableList<T> is read as the usual implementation, as System.Text.Json does.
    private static YamlTypeInfo? TryCreateImmutable(Type def, Type[] args, YamlSerializerOptions options) => def.Name switch
    {
        "ImmutableArray`1" => Call(nameof(MakeImmutableArray), args, options),
        "ImmutableList`1" => Call(nameof(MakeImmutableList), args, options),
        "IImmutableList`1" => Call(nameof(MakeImmutableIList), args, options),
        "ImmutableHashSet`1" => Call(nameof(MakeImmutableHashSet), args, options),
        "IImmutableSet`1" => Call(nameof(MakeImmutableISet), args, options),
        "ImmutableSortedSet`1" => Call(nameof(MakeImmutableSortedSet), args, options),
        "ImmutableQueue`1" => Call(nameof(MakeImmutableQueue), args, options),
        "IImmutableQueue`1" => Call(nameof(MakeImmutableIQueue), args, options),
        "ImmutableStack`1" => Call(nameof(MakeImmutableStack), args, options),
        "IImmutableStack`1" => Call(nameof(MakeImmutableIStack), args, options),
        "ImmutableDictionary`2" => Call(nameof(MakeImmutableDictionary), args, options),
        "IImmutableDictionary`2" => Call(nameof(MakeImmutableIDictionary), args, options),
        "ImmutableSortedDictionary`2" => Call(nameof(MakeImmutableSortedDictionary), args, options),
        _ => null,
    };

    // Other concrete collection and dictionary types with a public parameterless constructor.
    private static YamlTypeInfo? TryCreateOther(Type type, YamlSerializerOptions options)
    {
        if (type.GetConstructor(Type.EmptyTypes) is null && !type.IsValueType)
        {
            return null;
        }

        foreach (Type i in type.GetInterfaces())
        {
            if (!i.IsGenericType)
            {
                continue;
            }

            Type def = i.GetGenericTypeDefinition();
            if (def == typeof(IDictionary<,>))
            {
                Type[] a = i.GetGenericArguments();
                return Call(nameof(MakeGeneralDictionary), [type, a[0], a[1]], options);
            }
        }

        foreach (Type i in type.GetInterfaces())
        {
            if (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICollection<>))
            {
                return Call(nameof(MakeGeneralCollection), [type, i.GetGenericArguments()[0]], options);
            }
        }

        return null;
    }

    private static YamlTypeInfo Call(string name, Type[] typeArguments, YamlSerializerOptions options, object? extra = null)
    {
        MethodInfo method = typeof(ReflectionTypeInfoFactory).GetMethod(name, Any)!.MakeGenericMethod(typeArguments);
        try
        {
            return (YamlTypeInfo)method.Invoke(null, extra is null ? [options] : [options, extra])!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    // ---- Makers, called by reflection ----

    private static YamlTypeInfo<T> FromConverter<T>(YamlSerializerOptions options, object converter) =>
        converter is YamlConverter<T> typed
            ? new YamlTypeInfo<T>(options, typed)
            : throw new NotSupportedException($"The converter {converter.GetType()} cannot convert '{typeof(T)}' because it does not derive from YamlConverter<{typeof(T).Name}>.");

    private static YamlTypeInfo<T> MakeFromAttribute<T>(YamlSerializerOptions options, object converter) =>
        new YamlTypeInfo<T>(options, YamlMetadataServices.GetAttributeConverter<T>(options, (YamlConverter)converter));

    // Makes the converter a YamlConverter attribute names. An open generic converter type is closed over the type arguments of the
    // type it is applied to.
    private static YamlConverter Instantiate(Type converterType, Type target)
    {
        if (converterType.IsGenericTypeDefinition)
        {
            if (!target.IsGenericType || target.GetGenericArguments().Length != converterType.GetGenericArguments().Length)
            {
                throw new NotSupportedException($"The open generic converter {converterType} named by YamlConverter does not fit '{target}': it needs the same number of type arguments.");
            }

            converterType = converterType.MakeGenericType(target.GetGenericArguments());
        }

        if (!typeof(YamlConverter).IsAssignableFrom(converterType) || converterType.IsAbstract || converterType.GetConstructor(Type.EmptyTypes) is null)
        {
            throw new NotSupportedException($"The type {converterType} named by YamlConverter must be a converter with a public parameterless constructor.");
        }

        return (YamlConverter)Activator.CreateInstance(converterType)!;
    }

    private sealed class Polymorphism(YamlDiscriminatorKind kind, string key, List<(Type Type, string Discriminator)> derived)
    {
        public YamlDiscriminatorKind Kind { get; } = kind;

        public string Key { get; } = key;

        public List<(Type Type, string Discriminator)> Derived { get; } = derived;
    }

    // The polymorphism of a type, from its own attributes (a derived type does not inherit it). The YAML attributes win
    // over the System.Text.Json ones.
    private static Polymorphism? FindPolymorphism(Type type)
    {
        YamlPolymorphicAttribute? yaml = type.GetCustomAttribute<YamlPolymorphicAttribute>(false);
        JsonPolymorphicAttribute? json = type.GetCustomAttribute<JsonPolymorphicAttribute>(false);
        var derived = new List<(Type, string)>();
        foreach (YamlDerivedTypeAttribute a in type.GetCustomAttributes<YamlDerivedTypeAttribute>(false))
        {
            derived.Add((a.DerivedType, a.TypeDiscriminator));
        }

        if (derived.Count == 0)
        {
            foreach (JsonDerivedTypeAttribute a in type.GetCustomAttributes<JsonDerivedTypeAttribute>(false))
            {
                // A derived type with no discriminator cannot be told apart, so it is not listed.
                if (a.TypeDiscriminator is { } value && Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) is { } text)
                {
                    derived.Add((a.DerivedType, text));
                }
            }
        }

        if (yaml is null && json is null && derived.Count == 0)
        {
            return null;
        }

        // The name System.Text.Json gives the key applies only when no YAML polymorphism attribute is present.
        bool yamlPresent = yaml is not null || type.IsDefined(typeof(YamlDerivedTypeAttribute), false);
        string key = yamlPresent ? yaml?.TypeDiscriminatorPropertyName ?? "$type" : json?.TypeDiscriminatorPropertyName ?? "$type";
        return new Polymorphism(yaml?.DiscriminatorKind ?? YamlDiscriminatorKind.Key, key, derived);
    }

    private static YamlTypeInfo<T> MakePolymorphic<T>(YamlSerializerOptions options, object setup)
    {
        var polymorphism = (Polymorphism)setup;
        var derived = new YamlDerivedTypeInfo<T>[polymorphism.Derived.Count];
        MethodInfo make = typeof(ReflectionTypeInfoFactory).GetMethod(nameof(MakeDerived), Any)!;
        for (int i = 0; i < derived.Length; i++)
        {
            (Type derivedType, string discriminator) = polymorphism.Derived[i];
            if (derivedType == typeof(T) || !typeof(T).IsAssignableFrom(derivedType))
            {
                throw new NotSupportedException($"The derived type '{derivedType}' listed for '{typeof(T)}' must derive from it, or implement it, and must not be the type itself.");
            }

            derived[i] = (YamlDerivedTypeInfo<T>)make.MakeGenericMethod(typeof(T), derivedType).Invoke(null, [options, discriminator])!;
        }

        // A base that can be created is read as itself when the value names no derived type.
        YamlTypeInfo<T>? baseInfo = typeof(T).IsInterface || typeof(T).IsAbstract ? null : MakeObject<T>(options);
        return YamlMetadataServices.CreatePolymorphicInfo(options, baseInfo, polymorphism.Kind, polymorphism.Key, derived);
    }

    private static YamlDerivedTypeInfo<TBase> MakeDerived<TBase, TDerived>(YamlSerializerOptions options, string discriminator)
        where TDerived : TBase =>
        YamlMetadataServices.CreateDerivedTypeInfo<TBase, TDerived>(discriminator, () => options.GetTypeInfo<TDerived>());

    private static YamlTypeInfo<T?> MakeNullable<T>(YamlSerializerOptions options)
        where T : struct =>
        new YamlTypeInfo<T?>(options, new NullableConverter<T>(options.GetTypeInfo<T>().Converter));

    private static YamlTypeInfo<TEnum> MakeEnum<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] TEnum>(YamlSerializerOptions options)
        where TEnum : struct, Enum =>
        new YamlTypeInfo<TEnum>(options, new EnumConverter<TEnum>(options));

    private static YamlTypeInfo<T[]> MakeArray<T>(YamlSerializerOptions options) =>
        new YamlTypeInfo<T[]>(options, new ArrayConverter<T>(options.GetTypeInfo<T>()));

    private static YamlTypeInfo<List<T>> MakeList<T>(YamlSerializerOptions options) =>
        new YamlTypeInfo<List<T>>(options, new ListConverter<T>(options.GetTypeInfo<T>()));

    private static YamlTypeInfo<HashSet<T>> MakeHashSet<T>(YamlSerializerOptions options) =>
        new YamlTypeInfo<HashSet<T>>(options, new CollectionConverter<HashSet<T>, T>(options.GetTypeInfo<T>(), l => [.. l]));

    private static YamlTypeInfo<Dictionary<TKey, TValue>> MakeDictionary<TKey, TValue>(YamlSerializerOptions options)
        where TKey : notnull =>
        new YamlTypeInfo<Dictionary<TKey, TValue>>(options, new DictionaryConverter<TKey, TValue>(options.GetTypeInfo<TKey>(), options.GetTypeInfo<TValue>()));

    private static YamlTypeInfo<ImmutableArray<T>> MakeImmutableArray<T>(YamlSerializerOptions options) =>
        YamlMetadataServices.CreateConvertedCollectionInfo<ImmutableArray<T>, T>(options, options.GetTypeInfo<T>(), static l => ImmutableArray.CreateRange(l), static v => v.IsDefault);

    private static YamlTypeInfo<ImmutableList<T>> MakeImmutableList<T>(YamlSerializerOptions options) =>
        YamlMetadataServices.CreateConvertedCollectionInfo<ImmutableList<T>, T>(options, options.GetTypeInfo<T>(), static l => ImmutableList.CreateRange(l));

    private static YamlTypeInfo<IImmutableList<T>> MakeImmutableIList<T>(YamlSerializerOptions options) =>
        YamlMetadataServices.CreateConvertedCollectionInfo<IImmutableList<T>, T>(options, options.GetTypeInfo<T>(), static l => ImmutableList.CreateRange(l));

    private static YamlTypeInfo<ImmutableHashSet<T>> MakeImmutableHashSet<T>(YamlSerializerOptions options) =>
        YamlMetadataServices.CreateConvertedCollectionInfo<ImmutableHashSet<T>, T>(options, options.GetTypeInfo<T>(), static l => ImmutableHashSet.CreateRange(l));

    private static YamlTypeInfo<IImmutableSet<T>> MakeImmutableISet<T>(YamlSerializerOptions options) =>
        YamlMetadataServices.CreateConvertedCollectionInfo<IImmutableSet<T>, T>(options, options.GetTypeInfo<T>(), static l => ImmutableHashSet.CreateRange(l));

    private static YamlTypeInfo<ImmutableSortedSet<T>> MakeImmutableSortedSet<T>(YamlSerializerOptions options) =>
        YamlMetadataServices.CreateConvertedCollectionInfo<ImmutableSortedSet<T>, T>(options, options.GetTypeInfo<T>(), static l => ImmutableSortedSet.CreateRange(l));

    private static YamlTypeInfo<ImmutableQueue<T>> MakeImmutableQueue<T>(YamlSerializerOptions options) =>
        YamlMetadataServices.CreateConvertedCollectionInfo<ImmutableQueue<T>, T>(options, options.GetTypeInfo<T>(), static l => ImmutableQueue.CreateRange(l));

    private static YamlTypeInfo<IImmutableQueue<T>> MakeImmutableIQueue<T>(YamlSerializerOptions options) =>
        YamlMetadataServices.CreateConvertedCollectionInfo<IImmutableQueue<T>, T>(options, options.GetTypeInfo<T>(), static l => ImmutableQueue.CreateRange(l));

    // A stack enumerates from the top, so the list is reversed to read back what was written.
    private static YamlTypeInfo<ImmutableStack<T>> MakeImmutableStack<T>(YamlSerializerOptions options) =>
        YamlMetadataServices.CreateConvertedCollectionInfo<ImmutableStack<T>, T>(options, options.GetTypeInfo<T>(), static l => ImmutableStack.CreateRange(Reversed(l)));

    private static YamlTypeInfo<IImmutableStack<T>> MakeImmutableIStack<T>(YamlSerializerOptions options) =>
        YamlMetadataServices.CreateConvertedCollectionInfo<IImmutableStack<T>, T>(options, options.GetTypeInfo<T>(), static l => ImmutableStack.CreateRange(Reversed(l)));

    private static List<T> Reversed<T>(List<T> list)
    {
        list.Reverse();
        return list;
    }

    private static YamlTypeInfo<ImmutableDictionary<TKey, TValue>> MakeImmutableDictionary<TKey, TValue>(YamlSerializerOptions options)
        where TKey : notnull =>
        YamlMetadataServices.CreateConvertedDictionaryInfo<ImmutableDictionary<TKey, TValue>, TKey, TValue>(options, options.GetTypeInfo<TKey>(), options.GetTypeInfo<TValue>(), static d => ImmutableDictionary.CreateRange(d));

    private static YamlTypeInfo<IImmutableDictionary<TKey, TValue>> MakeImmutableIDictionary<TKey, TValue>(YamlSerializerOptions options)
        where TKey : notnull =>
        YamlMetadataServices.CreateConvertedDictionaryInfo<IImmutableDictionary<TKey, TValue>, TKey, TValue>(options, options.GetTypeInfo<TKey>(), options.GetTypeInfo<TValue>(), static d => ImmutableDictionary.CreateRange(d));

    private static YamlTypeInfo<ImmutableSortedDictionary<TKey, TValue>> MakeImmutableSortedDictionary<TKey, TValue>(YamlSerializerOptions options)
        where TKey : notnull =>
        YamlMetadataServices.CreateConvertedDictionaryInfo<ImmutableSortedDictionary<TKey, TValue>, TKey, TValue>(options, options.GetTypeInfo<TKey>(), options.GetTypeInfo<TValue>(), static d => ImmutableSortedDictionary.CreateRange(d));

    private static YamlTypeInfo<TTarget> MakeAdapted<TTarget, TImpl>(YamlSerializerOptions options)
        where TTarget : class
        where TImpl : class, TTarget =>
        new YamlTypeInfo<TTarget>(options, new AdaptedConverter<TTarget, TImpl>(options.GetTypeInfo<TImpl>().Converter));

    private static YamlTypeInfo<TDictionary> MakeGeneralDictionary<TDictionary, TKey, TValue>(YamlSerializerOptions options)
        where TDictionary : class, IDictionary<TKey, TValue>
        where TKey : notnull
    {
        Func<TDictionary> create = CreateFactory<TDictionary>(typeof(TDictionary).GetConstructor(Type.EmptyTypes)!);
        return new YamlTypeInfo<TDictionary>(options, new GeneralDictionaryConverter<TDictionary, TKey, TValue>(options.GetTypeInfo<TKey>(), options.GetTypeInfo<TValue>(), create));
    }

    private static YamlTypeInfo<TCollection> MakeGeneralCollection<TCollection, T>(YamlSerializerOptions options)
        where TCollection : class, ICollection<T>
    {
        Func<TCollection> create = CreateFactory<TCollection>(typeof(TCollection).GetConstructor(Type.EmptyTypes)!);
        return new YamlTypeInfo<TCollection>(options, new CollectionConverter<TCollection, T>(options.GetTypeInfo<T>(), list =>
        {
            TCollection c = create();
            foreach (T item in list)
            {
                c.Add(item);
            }

            return c;
        }));
    }

    // ---- Objects ----

    private static YamlTypeInfo<T> MakeObject<T>(YamlSerializerOptions options)
    {
        Type type = typeof(T);
        ConstructorInfo? ctor = SelectConstructor(type, out bool parameterless);
        ParameterInfo[] parameters = ctor is not null && !parameterless ? ctor.GetParameters() : [];
        Func<T>? creator = null;
        Func<object?[], T>? ctorCreator = null;
        if (ctor is not null && parameterless)
        {
            creator = CreateFactory<T>(ctor);
        }
        else if (type.IsValueType && ctor is null)
        {
            creator = static () => default!;
        }
        else if (ctor is not null)
        {
            ctorCreator = CreateParameterized<T>(ctor, parameters);
        }

        YamlParameterInfo[] parameterInfos = parameters.Select(p => new YamlParameterInfo(
            p.Name ?? string.Empty,
            p.ParameterType,
            p.HasDefaultValue && p.DefaultValue is not (DBNull or System.Reflection.Missing),
            p.HasDefaultValue && p.DefaultValue is not (DBNull or System.Reflection.Missing) ? p.DefaultValue : null)).ToArray();

        var values = new YamlObjectInfoValues<T>
        {
            ObjectCreator = creator,
            ConstructorCreator = ctorCreator,
            ConstructorParameterMetadataInitializer = () => parameterInfos,
            PropertyMetadataInitializer = o => BuildProperties<T>(o, parameters),
        };
        return YamlMetadataServices.CreateObjectInfo(options, values);
    }

    private static ConstructorInfo? SelectConstructor(Type type, out bool parameterless)
    {
        parameterless = false;
        if (type.IsInterface || type.IsAbstract)
        {
            return null;
        }

        // A constructor with a ref, in or out parameter cannot be called with a value from a mapping.
        ConstructorInfo[] ctors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Where(c => c.GetParameters().All(x => !x.ParameterType.IsByRef))
            .ToArray();
        ConstructorInfo? marked = ctors.FirstOrDefault(c => c.IsDefined(typeof(YamlConstructorAttribute), false))
            ?? ctors.FirstOrDefault(c => c.IsDefined(typeof(JsonConstructorAttribute), false));
        if (marked is not null)
        {
            parameterless = marked.GetParameters().Length == 0;
            return marked;
        }

        ConstructorInfo? none = ctors.FirstOrDefault(c => c.GetParameters().Length == 0);
        if (none is not null)
        {
            parameterless = true;
            return none;
        }

        if (type.IsValueType)
        {
            return null;
        }

        return ctors.Length == 1 ? ctors[0] : null;
    }

    private static YamlPropertyInfo[] BuildProperties<T>(YamlSerializerOptions options, ParameterInfo[] parameters)
    {
        var result = new List<YamlPropertyInfo>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (PropertyInfo p in OrderedProperties(typeof(T)))
        {
            if (p.GetIndexParameters().Length != 0 || IsUnbindable(p.PropertyType))
            {
                continue;
            }

            // An ignored member still takes its name, so the base member it overrides or hides stays out too.
            if (IsIgnored(p))
            {
                names.Add(p.Name);
                continue;
            }

            bool boundByConstructor = BoundByConstructor(parameters, p.Name, p.PropertyType, typeof(T));
            MethodInfo? setter = p.SetMethod is { IsPublic: true } ? p.SetMethod : null;
            bool extension = IsExtensionData(p, p.PropertyType, p.GetMethod is { IsPublic: true });
            if (setter is null && !boundByConstructor && !extension)
            {
                continue;
            }

            if (!names.Add(p.Name))
            {
                continue;
            }

            result.Add(Make<T>(options, p.PropertyType, p, null, setter, p.Name, p, extension));
        }

        foreach (FieldInfo f in typeof(T).GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (IsUnbindable(f.FieldType) || IsIgnored(f) || names.Contains(f.Name))
            {
                continue;
            }

            bool boundByConstructor = BoundByConstructor(parameters, f.Name, f.FieldType, typeof(T));
            bool extension = IsExtensionData(f, f.FieldType, true);
            if ((f.IsInitOnly || f.IsLiteral) && !boundByConstructor && !extension)
            {
                continue;
            }

            names.Add(f.Name);
            result.Add(Make<T>(options, f.FieldType, null, f, null, f.Name, f, extension));
        }

        return [.. result];
    }

    // A constructor parameter fills the member of the same name, so their types must agree: the value is
    // read once, as one type, and handed to the constructor.
    private static bool BoundByConstructor(ParameterInfo[] parameters, string member, Type memberType, Type owner)
    {
        foreach (ParameterInfo x in parameters)
        {
            if (string.Equals(x.Name, member, StringComparison.OrdinalIgnoreCase))
            {
                if (x.ParameterType != memberType)
                {
                    throw new NotSupportedException($"The constructor parameter '{x.Name}' of '{owner}' has type '{x.ParameterType}' but the member '{member}' has type '{memberType}'; make them the same type.");
                }

                return true;
            }
        }

        return false;
    }

    // Members of these types are left out, as the source generator leaves them out.
    private static bool IsUnbindable(Type type) =>
        type.IsByRefLike || type.IsPointer || type.IsFunctionPointer || typeof(Delegate).IsAssignableFrom(type);

    // Derived members first, so a member hidden by a derived one is the one that stays.
    private static IEnumerable<PropertyInfo> OrderedProperties(Type type)
    {
        for (Type? t = type; t is not null && t != typeof(object); t = t.BaseType)
        {
            foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                yield return p;
            }
        }
    }

    private static bool IsIgnored(MemberInfo m)
    {
        if (m.GetCustomAttribute<YamlIgnoreAttribute>(false) is { } yaml)
        {
            return yaml.Condition == YamlIgnoreCondition.Always;
        }

        if (m.GetCustomAttribute<JsonIgnoreAttribute>(false) is { } json)
        {
            return json.Condition == JsonIgnoreCondition.Always;
        }

        return false;
    }

    // The conditional ignore of a member that is still bound: it only affects writing.
    private static YamlIgnoreCondition? IgnoreConditionOf(MemberInfo m)
    {
        if (m.GetCustomAttribute<YamlIgnoreAttribute>(false) is { } yaml)
        {
            return yaml.Condition;
        }

        return m.GetCustomAttribute<JsonIgnoreAttribute>(false)?.Condition switch
        {
            JsonIgnoreCondition.WhenWritingNull => YamlIgnoreCondition.WhenWritingNull,
            JsonIgnoreCondition.WhenWritingDefault => YamlIgnoreCondition.WhenWritingDefault,
            JsonIgnoreCondition.Never => YamlIgnoreCondition.Never,
            _ => null,
        };
    }

    // The member that collects unmapped keys: marked with YamlExtensionData, or with the System.Text.Json attribute when its type
    // is one that can hold them (a model shared with System.Text.Json may use another type, which is then an ordinary member).
    private static bool IsExtensionData(MemberInfo member, Type memberType, bool hasGetter)
    {
        bool fits = memberType == typeof(Dictionary<string, object>) || memberType == typeof(IDictionary<string, object>);
        if (member.IsDefined(typeof(YamlExtensionDataAttribute), false))
        {
            if (!fits || !hasGetter)
            {
                throw new NotSupportedException($"The extension data member '{member.DeclaringType?.Name}.{member.Name}' must be a readable Dictionary<string, object> or IDictionary<string, object>.");
            }

            return true;
        }

        return fits && hasGetter && member.IsDefined(typeof(JsonExtensionDataAttribute), false);
    }

    private static YamlPropertyInfo Make<T>(YamlSerializerOptions options, Type propertyType, PropertyInfo? property, FieldInfo? field, MethodInfo? setter, string name, MemberInfo member, bool extension)
    {
        YamlConverter? memberConverter = member.GetCustomAttribute<YamlConverterAttribute>(false) is { } converterAttribute
            ? Instantiate(converterAttribute.ConverterType, propertyType)
            : null;
        string? explicitName = member.GetCustomAttribute<YamlPropertyNameAttribute>(false)?.Name
            ?? member.GetCustomAttribute<JsonPropertyNameAttribute>(false)?.Name;
        bool required = member.IsDefined(typeof(YamlRequiredAttribute), false)
            || member.IsDefined(typeof(JsonRequiredAttribute), false)
            || member.IsDefined(typeof(RequiredMemberAttribute), false);
        int order = member.GetCustomAttribute<YamlPropertyOrderAttribute>(false)?.Order
            ?? member.GetCustomAttribute<JsonPropertyOrderAttribute>(false)?.Order
            ?? 0;
        MethodInfo make = typeof(ReflectionTypeInfoFactory).GetMethod(nameof(MakeProperty), Any)!.MakeGenericMethod(typeof(T), propertyType);
        try
        {
            MethodInfo? getter = property?.GetMethod is { IsPublic: true } g ? g : null;
            return (YamlPropertyInfo)make.Invoke(null, [options, name, explicitName, required, order, setter, field, getter, IgnoreConditionOf(member), memberConverter, extension])!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            // Say which member could not be described; the inner message says why.
            if (ex.InnerException is NotSupportedException or ArgumentException or TypeLoadException or MemberAccessException)
            {
                throw new NotSupportedException($"The member '{typeof(T).Name}.{name}' of type '{propertyType}' cannot be bound: {ex.InnerException.Message}", ex.InnerException);
            }

            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static YamlPropertyInfo MakeProperty<TDeclaring, TProperty>(YamlSerializerOptions options, string name, string? explicitName, bool required, int order, MethodInfo? setter, FieldInfo? field, MethodInfo? getMethod, YamlIgnoreCondition? ignore, YamlConverter? converter, bool extension)
    {
        Func<TDeclaring, TProperty>? getter = field is not null ? FieldGetter<TDeclaring, TProperty>(field) : getMethod is not null ? PropertyGetter<TDeclaring, TProperty>(getMethod) : null;
        Action<TDeclaring, TProperty>? classSetter = null;
        YamlRefSetter<TDeclaring, TProperty>? refSetter = null;
        bool isStruct = typeof(TDeclaring).IsValueType;
        if (setter is not null)
        {
            if (isStruct)
            {
                refSetter = setter.CreateDelegate<YamlRefSetter<TDeclaring, TProperty>>();
            }
            else
            {
                classSetter = setter.CreateDelegate<Action<TDeclaring, TProperty>>();
            }
        }
        else if (field is not null && !field.IsInitOnly)
        {
            if (isStruct)
            {
                refSetter = FieldRefSetter<TDeclaring, TProperty>(field);
            }
            else
            {
                classSetter = FieldSetter<TDeclaring, TProperty>(field);
            }
        }

        // A member with no setter is filled through the constructor; reading it into a slot needs none.
        classSetter ??= isStruct ? null : static (_, _) => { };
        refSetter ??= isStruct ? static (ref TDeclaring _, TProperty _) => { } : null;
        return YamlMetadataServices.CreatePropertyInfo(new YamlPropertyInfoValues<TDeclaring, TProperty>
        {
            Name = name,
            SerializedName = explicitName,
            IsRequired = required,
            Order = order,
            Getter = getter,
            Converter = converter is null ? null : YamlMetadataServices.GetAttributeConverter<TProperty>(options, converter),
            IsExtensionData = extension,
            IgnoreCondition = ignore,
            Setter = classSetter,
            RefSetter = refSetter,
        });
    }

    private static Func<TDeclaring, TProperty> PropertyGetter<TDeclaring, TProperty>(MethodInfo getMethod)
    {
        if (!typeof(TDeclaring).IsValueType)
        {
            return getMethod.CreateDelegate<Func<TDeclaring, TProperty>>();
        }

        if (RuntimeFeature.IsDynamicCodeSupported)
        {
            ParameterExpression target = Expression.Parameter(typeof(TDeclaring));
            return Expression.Lambda<Func<TDeclaring, TProperty>>(Expression.Call(target, getMethod), target).Compile();
        }

        return o => (TProperty)getMethod.Invoke(o, null)!;
    }

    private static Func<TDeclaring, TProperty> FieldGetter<TDeclaring, TProperty>(FieldInfo field)
    {
        if (RuntimeFeature.IsDynamicCodeSupported)
        {
            ParameterExpression target = Expression.Parameter(typeof(TDeclaring));
            return Expression.Lambda<Func<TDeclaring, TProperty>>(Expression.Field(target, field), target).Compile();
        }

        return o => (TProperty)field.GetValue(o)!;
    }

    private static Action<TDeclaring, TProperty> FieldSetter<TDeclaring, TProperty>(FieldInfo field)
    {
        if (RuntimeFeature.IsDynamicCodeSupported)
        {
            ParameterExpression target = Expression.Parameter(typeof(TDeclaring));
            ParameterExpression value = Expression.Parameter(typeof(TProperty));
            return Expression.Lambda<Action<TDeclaring, TProperty>>(
                Expression.Assign(Expression.Field(target, field), value), target, value).Compile();
        }

        return (o, v) => field.SetValue(o, v);
    }

    private static YamlRefSetter<TDeclaring, TProperty> FieldRefSetter<TDeclaring, TProperty>(FieldInfo field)
    {
        if (RuntimeFeature.IsDynamicCodeSupported)
        {
            ParameterExpression target = Expression.Parameter(typeof(TDeclaring).MakeByRefType());
            ParameterExpression value = Expression.Parameter(typeof(TProperty));
            return Expression.Lambda<YamlRefSetter<TDeclaring, TProperty>>(
                Expression.Assign(Expression.Field(target, field), value), target, value).Compile();
        }

        return (ref TDeclaring o, TProperty v) =>
        {
            object boxed = o!;
            field.SetValue(boxed, v);
            o = (TDeclaring)boxed;
        };
    }

    private static Func<T> CreateFactory<T>(ConstructorInfo ctor)
    {
        if (RuntimeFeature.IsDynamicCodeSupported)
        {
            return Expression.Lambda<Func<T>>(Expression.New(ctor)).Compile();
        }

        return () => (T)ctor.Invoke(null);
    }

    private static Func<object?[], T> CreateParameterized<T>(ConstructorInfo ctor, ParameterInfo[] parameters)
    {
        if (RuntimeFeature.IsDynamicCodeSupported)
        {
            ParameterExpression args = Expression.Parameter(typeof(object[]));
            Expression[] converted = new Expression[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                Expression slot = Expression.ArrayIndex(args, Expression.Constant(i));
                Type pt = parameters[i].ParameterType;
                converted[i] = pt.IsValueType && Nullable.GetUnderlyingType(pt) is null
                    ? Expression.Condition(Expression.Equal(slot, Expression.Constant(null)), Expression.Default(pt), Expression.Convert(slot, pt))
                    : Expression.Convert(slot, pt);
            }

            return Expression.Lambda<Func<object?[], T>>(Expression.Convert(Expression.New(ctor, converted), typeof(T)), args).Compile();
        }

        int n = parameters.Length;
        return slots =>
        {
            object?[] exact = new object?[n];
            Array.Copy(slots, exact, n);
            return (T)ctor.Invoke(exact);
        };
    }
}
