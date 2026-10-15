using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Cs = global::OneYaml.Compat.SharpYaml.Serialization;
using Oy = global::OneYaml;

namespace OneYaml.Compat.SharpYaml;

// Describes types to the engine the way SharpYaml 3 does: its attributes, its rules for which members take part,
// and its names for enum values. Everything that is not a plain object (collections, scalars, nullable values)
// is left to the engine's own resolver, which builds on this one for the types inside.
[RequiresUnreferencedCode("Reflection-based binding reads members and constructors that trimming can remove.")]
[RequiresDynamicCode("Reflection-based binding makes generic types at run time.")]
internal sealed class ReflectionResolver : Oy.IYamlTypeInfoResolver
{
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    private readonly YamlSerializerOptions _options;
    private readonly Oy.DefaultYamlTypeInfoResolver _default = new();

    public ReflectionResolver(YamlSerializerOptions options) => _options = options;

    // The engine's own resolver carries the same annotations; this type's are on the class and the constructor path.
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The type carries the annotation, so reaching an instance means the caller accepted reflection.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "The type carries the annotation, so reaching an instance means the caller accepted reflection.")]
    public Oy.YamlTypeInfo? GetTypeInfo(Type type, Oy.YamlSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (type.IsEnum)
        {
            return Call(nameof(MakeEnum), type, options);
        }

        if (type.IsPrimitive || type == typeof(decimal))
        {
            return Call(nameof(MakeLenient), type, options);
        }

        if (!MaybeObject(type))
        {
            return _default.GetTypeInfo(type, options);
        }

        // Dates, GUIDs and the like are scalars the engine already reads and writes.
        if (IsLeaf(type))
        {
            return type.IsValueType ? Call(nameof(MakeLenient), type, options) : _default.GetTypeInfo(type, options)!;
        }

        return Call(nameof(MakeObject), type, options);
    }

    // The types the engine reads and writes as scalars. A plain list: asking the engine's metadata for members would
    // resolve the types of those members, and a type that contains itself would never finish.
    private static bool IsLeaf(Type type) =>
        type == typeof(Guid) || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(DateOnly)
        || type == typeof(TimeOnly) || type == typeof(TimeSpan) || type == typeof(Uri) || type == typeof(System.Numerics.BigInteger)
        || type == typeof(Int128) || type == typeof(UInt128) || type == typeof(Half);

    private static bool MaybeObject(Type type) =>
        !(type.IsPrimitive || type.IsArray || type.IsPointer || type.IsByRef || type.IsInterface || type.IsAbstract
          || type == typeof(string) || type == typeof(object) || type == typeof(decimal)
          || Nullable.GetUnderlyingType(type) is not null
          || typeof(IEnumerable).IsAssignableFrom(type)
          || typeof(Delegate).IsAssignableFrom(type));

    private YamlSerializerOptions Compat => _options;

    private Oy.YamlTypeInfo Call(string name, Type type, Oy.YamlSerializerOptions options)
    {
        MethodInfo method = typeof(ReflectionResolver).GetMethod(name, Any)!.MakeGenericMethod(type);
        try
        {
            return (Oy.YamlTypeInfo)method.Invoke(method.IsStatic ? null : this, [options])!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    // The engine's converter for a value type, with a null read as the default value and, for numbers and booleans, a quoted form accepted.
    private Oy.YamlTypeInfo<T> MakeLenient<T>(Oy.YamlSerializerOptions options)
        where T : struct
    {
        var inner = (Oy.YamlTypeInfo<T>)_default.GetTypeInfo(typeof(T), options)!;
        return Oy.YamlMetadataServices.CreateValueInfo(options, new LenientScalarConverter<T>(inner.Converter, LenientScalarConverter<T>.ParserFor(inner), LenientScalarConverter<T>.IsInteger()));
    }

    private static Oy.YamlTypeInfo<TEnum> MakeEnum<TEnum>(Oy.YamlSerializerOptions options)
        where TEnum : struct, Enum =>
        Oy.YamlMetadataServices.CreateValueInfo(options, new EnumConverter<TEnum>());

    private Oy.YamlTypeInfo<T> MakeObject<T>(Oy.YamlSerializerOptions options) => BuildObject<T>(options);

    private Oy.YamlTypeInfo<T> BuildObject<T>(Oy.YamlSerializerOptions options)
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

        Oy.YamlParameterInfo[] parameterInfos = [.. parameters.Select(static p => new Oy.YamlParameterInfo(
            p.Name ?? string.Empty,
            p.ParameterType,
            HasDefault(p),
            HasDefault(p) ? p.DefaultValue : null))];

        var values = new Oy.YamlObjectInfoValues<T>
        {
            ObjectCreator = creator,
            ConstructorCreator = ctorCreator,
            ConstructorParameterMetadataInitializer = () => parameterInfos,
            PropertyMetadataInitializer = _ => BuildProperties<T>(parameters),
        };
        return Oy.YamlMetadataServices.CreateObjectInfo(options, values);
    }

    private static bool HasDefault(ParameterInfo p) => p.HasDefaultValue && p.DefaultValue is not (DBNull or Missing);

    // The constructor that creates the object: the marked one, a public parameterless one, or the only public one.
    private static ConstructorInfo? SelectConstructor(Type type, out bool parameterless)
    {
        parameterless = false;
        if (type.IsInterface || type.IsAbstract)
        {
            return null;
        }

        ConstructorInfo[] all = type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        ConstructorInfo? marked = all.FirstOrDefault(static c => !c.GetParameters().Any(static x => x.ParameterType.IsByRef)
            && (c.IsDefined(typeof(Cs.YamlConstructorAttribute), false) || c.IsDefined(typeof(JsonConstructorAttribute), false)));
        if (marked is not null)
        {
            parameterless = marked.GetParameters().Length == 0;
            return marked;
        }

        ConstructorInfo[] ctors = [.. all.Where(static c => c.IsPublic && !c.GetParameters().Any(static x => x.ParameterType.IsByRef))];
        ConstructorInfo? none = ctors.FirstOrDefault(static c => c.GetParameters().Length == 0);
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

    private Oy.YamlPropertyInfo[] BuildProperties<T>(ParameterInfo[] parameters)
    {
        // Walk from the most derived type, so a member hidden by a derived one is the one that stays; the output lists
        // the base type's members first, the fields of a type before its properties.
        var levels = new List<List<(MemberInfo Member, Type Type, string Name)>>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (Type? t = typeof(T); t is not null && t != typeof(object); t = t.BaseType)
        {
            var fields = new List<(MemberInfo Member, Type Type, string Name)>();
            var props = new List<(MemberInfo Member, Type Type, string Name)>();
            foreach (PropertyInfo p in t.GetProperties(Any & ~BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (p.GetIndexParameters().Length != 0 || IsUnbindable(p.PropertyType))
                {
                    continue;
                }

                bool isPublic = p.GetMethod is { IsPublic: true } || p.SetMethod is { IsPublic: true };
                if (!isPublic && !IsIncluded(p))
                {
                    continue;
                }

                if (IsIgnored(p))
                {
                    names.Add(p.Name);
                    continue;
                }

                if (names.Add(p.Name))
                {
                    props.Add((p, p.PropertyType, p.Name));
                }
            }

            // A field takes part only when it asks to.
            foreach (FieldInfo f in t.GetFields(Any & ~BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (f.IsSpecialName || IsUnbindable(f.FieldType) || !IsIncluded(f) || IsIgnored(f) || names.Contains(f.Name))
                {
                    continue;
                }

                names.Add(f.Name);
                fields.Add((f, f.FieldType, f.Name));
            }

            fields.AddRange(props);
            levels.Add(fields);
        }

        levels.Reverse();
        List<(MemberInfo Member, Type Type, string Name)> members = [.. levels.SelectMany(static l => l)];

        List<(MemberInfo Member, Type Type, string Name)> bound = [];
        foreach ((MemberInfo member, Type type, string name) in members)
        {
            bool byConstructor = BoundByConstructor(parameters, name, type, typeof(T));
            if (member is PropertyInfo p && !byConstructor)
            {
                bool hasSetter = p.SetMethod is not null && (p.SetMethod.IsPublic || IsIncluded(p));
                bool hasGetter = p.GetMethod is not null && (p.GetMethod.IsPublic || IsIncluded(p));
                if (!hasSetter && !hasGetter)
                {
                    continue;
                }
            }

            bound.Add((member, type, name));
        }

        // Sorted order ranks the members by the key they are written under.
        Dictionary<MemberInfo, int>? rank = null;
        if (Compat.MappingOrder == YamlMappingOrderPolicy.Sorted)
        {
            rank = [];
            var keyed = bound.Select(m => (m.Member, Key: KeyOf(m.Member, m.Name))).OrderBy(static k => k.Key, StringComparer.Ordinal).ToList();
            for (int i = 0; i < keyed.Count; i++)
            {
                rank[keyed[i].Member] = i;
            }
        }

        var result = new List<Oy.YamlPropertyInfo>(bound.Count);
        foreach ((MemberInfo member, Type type, string name) in bound)
        {
            result.Add(MakeProperty<T>(member, type, name, parameters, rank));
        }

        return [.. result];
    }

    private string KeyOf(MemberInfo member, string name) =>
        ExplicitName(member) ?? Compat.PropertyNamingPolicy?.ConvertName(name) ?? name;

    private static string? ExplicitName(MemberInfo member) =>
        member.GetCustomAttribute<Cs.YamlPropertyNameAttribute>(false)?.Name
        ?? member.GetCustomAttribute<JsonPropertyNameAttribute>(false)?.Name;

    private static bool IsIncluded(MemberInfo m) =>
        m.IsDefined(typeof(Cs.YamlIncludeAttribute), false) || m.IsDefined(typeof(JsonIncludeAttribute), false);

    private static bool IsUnbindable(Type type) =>
        type.IsByRefLike || type.IsPointer || type.IsFunctionPointer || typeof(Delegate).IsAssignableFrom(type);

    // The member is left out of reading and writing altogether.
    private static bool IsIgnored(MemberInfo m)
    {
        if (m.GetCustomAttribute<Cs.YamlIgnoreAttribute>(false) is { } yaml)
        {
            return yaml.Condition == YamlIgnoreCondition.Always;
        }

        return m.GetCustomAttribute<JsonIgnoreAttribute>(false) is { Condition: JsonIgnoreCondition.Always };
    }

    private static YamlIgnoreCondition IgnoreConditionOf(MemberInfo m)
    {
        if (m.GetCustomAttribute<Cs.YamlIgnoreAttribute>(false) is { } yaml)
        {
            return yaml.Condition;
        }

        return m.GetCustomAttribute<JsonIgnoreAttribute>(false)?.Condition switch
        {
            JsonIgnoreCondition.WhenWritingNull => YamlIgnoreCondition.WhenWritingNull,
            JsonIgnoreCondition.WhenWritingDefault => YamlIgnoreCondition.WhenWritingDefault,
            _ => YamlIgnoreCondition.Never,
        };
    }

    // A constructor parameter fills the member of the same name, so their types must agree.
    private static bool BoundByConstructor(ParameterInfo[] parameters, string member, Type memberType, Type owner)
    {
        foreach (ParameterInfo x in parameters)
        {
            if (string.Equals(x.Name, member, StringComparison.OrdinalIgnoreCase))
            {
                return x.ParameterType == memberType
                    ? true
                    : throw new NotSupportedException($"The constructor parameter '{x.Name}' of '{owner}' has type '{x.ParameterType}' but the member '{member}' has type '{memberType}'; make them the same type.");
            }
        }

        return false;
    }

    private static ParameterInfo? ParameterOf(ParameterInfo[] parameters, string member)
    {
        foreach (ParameterInfo x in parameters)
        {
            if (string.Equals(x.Name, member, StringComparison.OrdinalIgnoreCase))
            {
                return x;
            }
        }

        return null;
    }

    private Oy.YamlPropertyInfo MakeProperty<T>(MemberInfo member, Type propertyType, string name, ParameterInfo[] parameters, Dictionary<MemberInfo, int>? rank)
    {
        string? explicitName = ExplicitName(member);
        ParameterInfo? parameter = ParameterOf(parameters, name);

        // A constructor parameter without a default has no value to fall back on, so the key must be present.
        bool required = member.IsDefined(typeof(Cs.YamlRequiredAttribute), false)
            || member.IsDefined(typeof(JsonRequiredAttribute), false)
            || member.IsDefined(typeof(RequiredMemberAttribute), false)
            || (parameter is not null && !HasDefault(parameter));
        int order = rank is not null
            ? rank[member]
            : member.GetCustomAttribute<Cs.YamlPropertyOrderAttribute>(false)?.Order
                ?? member.GetCustomAttribute<JsonPropertyOrderAttribute>(false)?.Order
                ?? 0;

        MethodInfo make = typeof(ReflectionResolver).GetMethod(nameof(MakeAccessors), Any)!.MakeGenericMethod(typeof(T), propertyType);
        try
        {
            return (Oy.YamlPropertyInfo)make.Invoke(null, [member, name, explicitName, required, order, IgnoreConditionOf(member)])!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static Oy.YamlPropertyInfo MakeAccessors<TDeclaring, TProperty>(MemberInfo member, string name, string? explicitName, bool required, int order, YamlIgnoreCondition ignore)
    {
        Func<TDeclaring, TProperty>? getter = null;
        Action<TDeclaring, TProperty>? classSetter = null;
        Oy.YamlRefSetter<TDeclaring, TProperty>? refSetter = null;
        bool isStruct = typeof(TDeclaring).IsValueType;
        if (member is PropertyInfo p)
        {
            if (p.GetMethod is not null && ignore is not YamlIgnoreCondition.WhenWriting)
            {
                getter = PropertyGetter<TDeclaring, TProperty>(p.GetMethod);
            }

            if (p.SetMethod is not null && (p.SetMethod.IsPublic || IsIncluded(p)) && ignore is not YamlIgnoreCondition.WhenReading)
            {
                if (isStruct)
                {
                    refSetter = p.SetMethod.CreateDelegate<Oy.YamlRefSetter<TDeclaring, TProperty>>();
                }
                else
                {
                    classSetter = p.SetMethod.CreateDelegate<Action<TDeclaring, TProperty>>();
                }
            }
        }
        else if (member is FieldInfo f)
        {
            if (ignore is not YamlIgnoreCondition.WhenWriting)
            {
                getter = FieldGetter<TDeclaring, TProperty>(f);
            }

            if (!f.IsInitOnly && ignore is not YamlIgnoreCondition.WhenReading)
            {
                if (isStruct)
                {
                    refSetter = FieldRefSetter<TDeclaring, TProperty>(f);
                }
                else
                {
                    classSetter = FieldSetter<TDeclaring, TProperty>(f);
                }
            }
        }

        // A member with no setter is filled through the constructor, or its key is read and dropped.
        classSetter ??= isStruct ? null : static (_, _) => { };
        refSetter ??= isStruct ? static (ref TDeclaring _, TProperty _) => { } : null;
        return Oy.YamlMetadataServices.CreatePropertyInfo(new Oy.YamlPropertyInfoValues<TDeclaring, TProperty>
        {
            Name = name,
            SerializedName = explicitName,
            IsRequired = required,
            Order = order,
            Getter = getter,
            IgnoreCondition = ignore switch
            {
                YamlIgnoreCondition.WhenWritingNull => Oy.YamlIgnoreCondition.WhenWritingNull,
                YamlIgnoreCondition.WhenWritingDefault => Oy.YamlIgnoreCondition.WhenWritingDefault,
                _ => null,
            },
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
            return Expression.Lambda<Action<TDeclaring, TProperty>>(Expression.Assign(Expression.Field(target, field), value), target, value).Compile();
        }

        return (o, v) => field.SetValue(o, v);
    }

    private static Oy.YamlRefSetter<TDeclaring, TProperty> FieldRefSetter<TDeclaring, TProperty>(FieldInfo field)
    {
        if (RuntimeFeature.IsDynamicCodeSupported)
        {
            ParameterExpression target = Expression.Parameter(typeof(TDeclaring).MakeByRefType());
            ParameterExpression value = Expression.Parameter(typeof(TProperty));
            return Expression.Lambda<Oy.YamlRefSetter<TDeclaring, TProperty>>(Expression.Assign(Expression.Field(target, field), value), target, value).Compile();
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
            var converted = new Expression[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                Type pt = parameters[i].ParameterType;
                Expression item = Expression.ArrayIndex(args, Expression.Constant(i));
                // A missing value is null in the slot; a value type takes its default then.
                converted[i] = pt.IsValueType
                    ? Expression.Condition(Expression.Equal(item, Expression.Constant(null)), Expression.Default(pt), Expression.Convert(item, pt))
                    : Expression.Convert(item, pt);
            }

            return Expression.Lambda<Func<object?[], T>>(Expression.New(ctor, converted), args).Compile();
        }

        return a =>
        {
            object?[] copy = new object?[a.Length];
            for (int i = 0; i < a.Length; i++)
            {
                copy[i] = a[i] ?? (parameters[i].ParameterType.IsValueType ? Activator.CreateInstance(parameters[i].ParameterType) : null);
            }

            return (T)ctor.Invoke(copy);
        };
    }
}

// An enum is written by its declared name, whatever the naming policy, and read by name (exactly or ignoring case) or number.
internal sealed class EnumConverter<TEnum> : Oy.YamlConverter<TEnum>
    where TEnum : struct, Enum
{
    private readonly Dictionary<string, TEnum> _exact = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TEnum> _ignoreCase = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<TEnum, string> _written = [];

    public EnumConverter()
    {
        foreach (TEnum value in Enum.GetValues<TEnum>())
        {
            string name = value.ToString();
            _exact.TryAdd(name, value);
            _ignoreCase.TryAdd(name, value);
            _written.TryAdd(value, name);
        }
    }

    public override bool HandleNull => true;

    public override void Write(Oy.Utf8YamlEmitter writer, TEnum value, Oy.YamlSerializerOptions options)
    {
        if (_written.TryGetValue(value, out string? name))
        {
            writer.WriteStringValue(name);
            return;
        }

        string text = value.ToString();
        if (text.Length > 0 && (char.IsAsciiDigit(text[0]) || text[0] == '-'))
        {
            if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long number))
            {
                writer.WriteNumberValue(number);
            }
            else
            {
                writer.WriteNumberValue(ulong.Parse(text, CultureInfo.InvariantCulture));
            }
        }
        else
        {
            writer.WriteStringValue(text);
        }
    }

    public override TEnum Read(ref Oy.Utf8YamlReader reader, Type typeToConvert, Oy.YamlSerializerOptions options)
    {
        if (reader.IsNull)
        {
            return default;
        }

        if (reader.TokenType != Oy.YamlTokenKind.Scalar)
        {
            throw reader.CreateException($"Expected a scalar for {typeof(TEnum).Name}.");
        }

        string text = reader.GetString();
        if (_exact.TryGetValue(text, out TEnum v) || _ignoreCase.TryGetValue(text, out v))
        {
            return v;
        }

        if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long number))
        {
            return FromNumber(ref reader, number);
        }

        // Flag combinations such as "Read, Write".
        if (text.Contains(',', StringComparison.Ordinal) && Enum.TryParse(text, ignoreCase: true, out v))
        {
            return v;
        }

        throw reader.CreateException($"Invalid enum scalar '{text}'.");
    }

    private static TEnum FromNumber(ref Oy.Utf8YamlReader reader, long number)
    {
        // Enum.ToObject cuts a number down to the underlying type, so the range is checked first.
        bool fits = Type.GetTypeCode(Enum.GetUnderlyingType(typeof(TEnum))) switch
        {
            TypeCode.SByte => number is >= sbyte.MinValue and <= sbyte.MaxValue,
            TypeCode.Byte => number is >= byte.MinValue and <= byte.MaxValue,
            TypeCode.Int16 => number is >= short.MinValue and <= short.MaxValue,
            TypeCode.UInt16 => number is >= ushort.MinValue and <= ushort.MaxValue,
            TypeCode.Int32 => number is >= int.MinValue and <= int.MaxValue,
            TypeCode.UInt32 => number is >= uint.MinValue and <= uint.MaxValue,
            TypeCode.UInt64 => number >= 0,
            _ => true,
        };
        return fits ? (TEnum)Enum.ToObject(typeof(TEnum), number) : throw reader.CreateException($"The number is outside the range of {typeof(TEnum).Name}.");
    }
}
