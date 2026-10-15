using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

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
            : throw new InvalidOperationException($"The converter {converter.GetType()} cannot convert '{typeof(T)}' because it does not derive from YamlConverter<{typeof(T).Name}>.");

    private static YamlTypeInfo<T?> MakeNullable<T>(YamlSerializerOptions options)
        where T : struct =>
        new YamlTypeInfo<T?>(options, new NullableConverter<T>(options.GetTypeInfo<T>().Converter));

    private static YamlTypeInfo<TEnum> MakeEnum<TEnum>(YamlSerializerOptions options)
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
            PropertyMetadataInitializer = _ => BuildProperties<T>(parameters),
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

    private static YamlPropertyInfo[] BuildProperties<T>(ParameterInfo[] parameters)
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
            if (setter is null && !boundByConstructor)
            {
                continue;
            }

            if (!names.Add(p.Name))
            {
                continue;
            }

            result.Add(Make<T>(p.PropertyType, p, null, setter, p.Name, p));
        }

        foreach (FieldInfo f in typeof(T).GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (IsUnbindable(f.FieldType) || IsIgnored(f) || names.Contains(f.Name))
            {
                continue;
            }

            bool boundByConstructor = BoundByConstructor(parameters, f.Name, f.FieldType, typeof(T));
            if ((f.IsInitOnly || f.IsLiteral) && !boundByConstructor)
            {
                continue;
            }

            names.Add(f.Name);
            result.Add(Make<T>(f.FieldType, null, f, null, f.Name, f));
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

    private static YamlPropertyInfo Make<T>(Type propertyType, PropertyInfo? property, FieldInfo? field, MethodInfo? setter, string name, MemberInfo member)
    {
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
            return (YamlPropertyInfo)make.Invoke(null, [name, explicitName, required, order, setter, field, getter, IgnoreConditionOf(member)])!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static YamlPropertyInfo MakeProperty<TDeclaring, TProperty>(string name, string? explicitName, bool required, int order, MethodInfo? setter, FieldInfo? field, MethodInfo? getMethod, YamlIgnoreCondition? ignore)
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
