using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace OneYaml.SourceGeneration;

// Reads the compilation for one context and decides which types get metadata and how. The decisions
// follow the reflection resolver one for one, so a generated context and reflection bind alike.
internal sealed class Parser
{
    private static readonly SymbolDisplayFormat s_typeFormat = SymbolDisplayFormat.FullyQualifiedFormat;

    private static readonly HashSet<string> s_leafNames =
    [
        "System.Guid",
        "System.DateTimeOffset",
        "System.DateOnly",
        "System.TimeOnly",
        "System.TimeSpan",
        "System.Uri",
        "System.Numerics.BigInteger",
        "System.Int128",
        "System.UInt128",
        "System.Half",
    ];

    private static readonly string[] s_reserved =
    [
        "Options", "GetTypeInfo", "Default", "Equals", "GetHashCode", "ToString", "GetType", "Finalize", "MemberwiseClone",
    ];

    private readonly Compilation _compilation;
    private readonly INamedTypeSymbol _context;
    private readonly CancellationToken _cancellation;
    private readonly List<DiagnosticInfo> _diagnostics = [];
    private readonly Dictionary<ITypeSymbol, TypeSpec> _specs = new(SymbolEqualityComparer.Default);
    private readonly HashSet<ITypeSymbol> _failed = new(SymbolEqualityComparer.Default);
    private readonly List<TypeSpec> _order = [];
    private readonly Queue<TypeSpec> _pending = new();
    private readonly HashSet<string> _names = new(StringComparer.Ordinal);
    private readonly HashSet<string> _contextMembers = new(StringComparer.Ordinal);

    private readonly INamedTypeSymbol? _yamlIgnore;
    private readonly INamedTypeSymbol? _yamlName;
    private readonly INamedTypeSymbol? _yamlRequired;
    private readonly INamedTypeSymbol? _yamlOrder;
    private readonly INamedTypeSymbol? _yamlConstructor;
    private readonly INamedTypeSymbol? _jsonIgnore;
    private readonly INamedTypeSymbol? _jsonName;
    private readonly INamedTypeSymbol? _jsonRequired;
    private readonly INamedTypeSymbol? _jsonOrder;
    private readonly INamedTypeSymbol? _jsonConstructor;
    private readonly INamedTypeSymbol? _setsRequired;

    private bool _hasErrors;

    private Parser(Compilation compilation, INamedTypeSymbol context, CancellationToken cancellation)
    {
        _compilation = compilation;
        _context = context;
        _cancellation = cancellation;
        _yamlIgnore = compilation.GetTypeByMetadataName("OneYaml.YamlIgnoreAttribute");
        _yamlName = compilation.GetTypeByMetadataName("OneYaml.YamlPropertyNameAttribute");
        _yamlRequired = compilation.GetTypeByMetadataName("OneYaml.YamlRequiredAttribute");
        _yamlOrder = compilation.GetTypeByMetadataName("OneYaml.YamlPropertyOrderAttribute");
        _yamlConstructor = compilation.GetTypeByMetadataName("OneYaml.YamlConstructorAttribute");
        _jsonIgnore = compilation.GetTypeByMetadataName("System.Text.Json.Serialization.JsonIgnoreAttribute");
        _jsonName = compilation.GetTypeByMetadataName("System.Text.Json.Serialization.JsonPropertyNameAttribute");
        _jsonRequired = compilation.GetTypeByMetadataName("System.Text.Json.Serialization.JsonRequiredAttribute");
        _jsonOrder = compilation.GetTypeByMetadataName("System.Text.Json.Serialization.JsonPropertyOrderAttribute");
        _jsonConstructor = compilation.GetTypeByMetadataName("System.Text.Json.Serialization.JsonConstructorAttribute");
        _setsRequired = compilation.GetTypeByMetadataName("System.Diagnostics.CodeAnalysis.SetsRequiredMembersAttribute");
        foreach (string reserved in s_reserved)
        {
            _names.Add(reserved);
        }

        foreach (ISymbol member in context.GetMembers())
        {
            _names.Add(member.Name);
            _contextMembers.Add(member.Name);
        }
    }

    public static GenerationResult Run(GeneratorAttributeSyntaxContext context, CancellationToken cancellation)
    {
        var symbol = (INamedTypeSymbol)context.TargetSymbol;
        return new Parser(context.SemanticModel.Compilation, symbol, cancellation).Build(context.Attributes);
    }

    private GenerationResult Build(ImmutableArray<AttributeData> attributes)
    {
        Location contextLocation = _context.Locations.FirstOrDefault() ?? Location.None;
        if (!DerivesFromContext())
        {
            Report(Diagnostics.ContextBaseType, contextLocation, _context.Name);
            return Finish(null);
        }

        if (!IsPartialChain())
        {
            Report(Diagnostics.ContextNotPartial, contextLocation, _context.Name);
            return Finish(null);
        }

        foreach (AttributeData attribute in attributes)
        {
            _cancellation.ThrowIfCancellationRequested();
            if (attribute.ConstructorArguments.Length == 1 && attribute.ConstructorArguments[0].Value is ITypeSymbol root)
            {
                Location where = attribute.ApplicationSyntaxReference?.GetSyntax(_cancellation).GetLocation() ?? contextLocation;
                Require(root, where, isRoot: true);
            }
        }

        while (_pending.Count > 0)
        {
            _cancellation.ThrowIfCancellationRequested();
            ProcessObject(_pending.Dequeue());
        }

        CheckCollectionCycles(contextLocation);
        if (_hasErrors)
        {
            return Finish(null);
        }

        return Finish(new Emitter(_context, _order).Emit());
    }

    private GenerationResult Finish(string? source)
    {
        string? hint = source is null ? null : HintName();
        return new GenerationResult(hint, source, [.. _diagnostics]);
    }

    private string HintName()
    {
        string name = _context.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
        var sb = new StringBuilder(name.Length + 16);
        foreach (char c in name)
        {
            sb.Append(char.IsLetterOrDigit(c) || c == '.' ? c : '_');
        }

        return sb.Append(".YamlMetadata.g.cs").ToString();
    }

    private bool DerivesFromContext()
    {
        for (INamedTypeSymbol? t = _context.BaseType; t is not null; t = t.BaseType)
        {
            if (t.Name == "YamlSerializerContext" && t.ContainingNamespace.ToDisplayString() == "OneYaml")
            {
                return true;
            }
        }

        return false;
    }

    private bool IsPartialChain()
    {
        for (INamedTypeSymbol? t = _context; t is not null; t = t.ContainingType)
        {
            if (t.IsGenericType)
            {
                return false;
            }

            bool partial = false;
            foreach (SyntaxReference reference in t.DeclaringSyntaxReferences)
            {
                if (reference.GetSyntax(_cancellation) is TypeDeclarationSyntax decl && decl.Modifiers.Any(SyntaxKind.PartialKeyword))
                {
                    partial = true;
                    break;
                }
            }

            if (!partial)
            {
                return false;
            }
        }

        return true;
    }

    // ---- Diagnostics ----

    private void Report(DiagnosticDescriptor descriptor, Location location, params string[] arguments)
    {
        if (descriptor.DefaultSeverity == DiagnosticSeverity.Error)
        {
            _hasErrors = true;
        }

        _diagnostics.Add(new DiagnosticInfo(
            descriptor,
            location.SourceTree?.FilePath,
            location.SourceSpan,
            location.GetLineSpan().Span,
            arguments));
    }

    private static string Show(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);

    // ---- Types ----

    // Returns the spec for a type, making it on first use; null, with a diagnostic, when the type cannot be described.
    private TypeSpec? Require(ITypeSymbol type, Location where, bool isRoot)
    {
        if (type.TypeKind == TypeKind.Dynamic)
        {
            type = _compilation.GetSpecialType(SpecialType.System_Object);
        }

        if (_specs.TryGetValue(type, out TypeSpec? known))
        {
            return known;
        }

        if (_failed.Contains(type))
        {
            return null;
        }

        if (!CheckUsable(type, where, isRoot))
        {
            _failed.Add(type);
            return null;
        }

        TypeShape shape = Classify(type, out List<ITypeSymbol> children, out string? implementation);
        var spec = new TypeSpec(type, type.ToDisplayString(s_typeFormat), UniqueName(type), shape)
        {
            ImplementationName = implementation,
            Visibility = Visibility(type),
        };
        _specs.Add(type, spec);
        _order.Add(spec);
        foreach (ITypeSymbol child in children)
        {
            TypeSpec? dependency = Require(child, where, isRoot: false);
            if (dependency is null)
            {
                // A collection of something that cannot be described cannot be described either.
                _specs.Remove(type);
                _order.Remove(spec);
                _failed.Add(type);
                return null;
            }

            spec.Dependencies.Add(dependency);
        }

        if (shape == TypeShape.Object)
        {
            _pending.Enqueue(spec);
        }

        return spec;
    }

    private bool CheckUsable(ITypeSymbol type, Location where, bool isRoot)
    {
        string shown = Show(type);
        if (ContainsTypeParameter(type))
        {
            Report(isRoot ? Diagnostics.OpenGenericType : Diagnostics.UnsupportedMember, where, isRoot ? shown : $"'{shown}' is an open generic type and is left out");
            return false;
        }

        if (type.TypeKind is TypeKind.Error or TypeKind.Unknown)
        {
            return false;
        }

        if (type.TypeKind is TypeKind.Pointer or TypeKind.FunctionPointer or TypeKind.Delegate || type.IsRefLikeType)
        {
            Report(Diagnostics.UnsupportedMember, where, $"'{shown}' cannot be bound: pointers, delegates and ref structs are not supported");
            return false;
        }

        if (type is IArrayTypeSymbol { Rank: not 1 })
        {
            Report(Diagnostics.UnsupportedMember, where, $"'{shown}' is a multi-dimensional array, which cannot be bound");
            return false;
        }

        if (!IsAccessible(type))
        {
            Report(Diagnostics.UnsupportedMember, where, $"'{shown}' is not accessible from '{_context.Name}' and is left out");
            return false;
        }

        return true;
    }

    private static bool ContainsTypeParameter(ITypeSymbol type)
    {
        switch (type)
        {
            case ITypeParameterSymbol:
                return true;
            case IArrayTypeSymbol array:
                return ContainsTypeParameter(array.ElementType);
            case INamedTypeSymbol named:
                if (named.IsUnboundGenericType)
                {
                    return true;
                }

                foreach (ITypeSymbol argument in named.TypeArguments)
                {
                    if (ContainsTypeParameter(argument))
                    {
                        return true;
                    }
                }

                return named.ContainingType is { } outer && ContainsTypeParameter(outer);
            default:
                return false;
        }
    }

    private bool IsAccessible(ITypeSymbol type)
    {
        switch (type)
        {
            case IArrayTypeSymbol array:
                return IsAccessible(array.ElementType);
            case INamedTypeSymbol named:
                if (!_compilation.IsSymbolAccessibleWithin(named.OriginalDefinition, _context))
                {
                    return false;
                }

                foreach (ITypeSymbol argument in named.TypeArguments)
                {
                    if (!IsAccessible(argument))
                    {
                        return false;
                    }
                }

                return true;
            default:
                return true;
        }
    }

    // 3 public, 2 internal, 1 private or protected: the least visible part of the type decides.
    private static int Visibility(ITypeSymbol type)
    {
        switch (type)
        {
            case IArrayTypeSymbol array:
                return Visibility(array.ElementType);
            case INamedTypeSymbol named:
                int level = 3;
                for (INamedTypeSymbol? t = named; t is not null; t = t.ContainingType)
                {
                    level = Math.Min(level, t.DeclaredAccessibility switch
                    {
                        Accessibility.Public => 3,
                        Accessibility.Internal or Accessibility.ProtectedOrInternal or Accessibility.NotApplicable => 2,
                        _ => 1,
                    });
                }

                foreach (ITypeSymbol argument in named.TypeArguments)
                {
                    level = Math.Min(level, Visibility(argument));
                }

                return level;
            default:
                return 3;
        }
    }

    private TypeShape Classify(ITypeSymbol type, out List<ITypeSymbol> children, out string? implementation)
    {
        children = [];
        implementation = null;
        if (IsLeaf(type))
        {
            return TypeShape.Leaf;
        }

        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
        {
            children.Add(nullable.TypeArguments[0]);
            return TypeShape.Nullable;
        }

        if (type.TypeKind == TypeKind.Enum)
        {
            return TypeShape.Enum;
        }

        if (type is IArrayTypeSymbol array)
        {
            children.Add(array.ElementType);
            return TypeShape.Array;
        }

        if (type is INamedTypeSymbol { IsGenericType: true } generic)
        {
            string definition = MetadataName(generic.OriginalDefinition);
            ImmutableArray<ITypeSymbol> args = generic.TypeArguments;
            switch (definition)
            {
                case "System.Collections.Generic.List`1":
                    children.Add(args[0]);
                    return TypeShape.List;
                case "System.Collections.Generic.Dictionary`2":
                    children.Add(args[0]);
                    children.Add(args[1]);
                    return TypeShape.Dictionary;
                case "System.Collections.Generic.HashSet`1":
                    children.Add(args[0]);
                    return TypeShape.HashSet;
                case "System.Collections.Generic.IList`1":
                case "System.Collections.Generic.ICollection`1":
                case "System.Collections.Generic.IEnumerable`1":
                case "System.Collections.Generic.IReadOnlyList`1":
                case "System.Collections.Generic.IReadOnlyCollection`1":
                    return Adapted("System.Collections.Generic.List`1", args, children, out implementation);
                case "System.Collections.Generic.ISet`1":
                case "System.Collections.Generic.IReadOnlySet`1":
                    return Adapted("System.Collections.Generic.HashSet`1", args, children, out implementation);
                case "System.Collections.Generic.IDictionary`2":
                case "System.Collections.Generic.IReadOnlyDictionary`2":
                    return Adapted("System.Collections.Generic.Dictionary`2", args, children, out implementation);
                default:
                    break;
            }
        }

        if (type is INamedTypeSymbol { TypeKind: TypeKind.Class, IsAbstract: false } concrete
            && concrete.InstanceConstructors.Any(c => c.DeclaredAccessibility == Accessibility.Public && c.Parameters.Length == 0))
        {
            foreach (INamedTypeSymbol i in concrete.AllInterfaces)
            {
                if (i.IsGenericType && MetadataName(i.OriginalDefinition) == "System.Collections.Generic.IDictionary`2")
                {
                    children.Add(i.TypeArguments[0]);
                    children.Add(i.TypeArguments[1]);
                    return TypeShape.CustomDictionary;
                }
            }

            foreach (INamedTypeSymbol i in concrete.AllInterfaces)
            {
                if (i.IsGenericType && MetadataName(i.OriginalDefinition) == "System.Collections.Generic.ICollection`1")
                {
                    children.Add(i.TypeArguments[0]);
                    return TypeShape.CustomCollection;
                }
            }
        }

        return TypeShape.Object;
    }

    // An interface is read through a concrete collection that implements it.
    private TypeShape Adapted(string implementationDefinition, ImmutableArray<ITypeSymbol> args, List<ITypeSymbol> children, out string? implementation)
    {
        INamedTypeSymbol definition = _compilation.GetTypeByMetadataName(implementationDefinition)!;
        INamedTypeSymbol closed = definition.Construct([.. args]);
        children.Add(closed);
        implementation = closed.ToDisplayString(s_typeFormat);
        return TypeShape.Interface;
    }

    private static bool IsLeaf(ITypeSymbol type)
    {
        switch (type.SpecialType)
        {
            case SpecialType.System_Boolean:
            case SpecialType.System_Char:
            case SpecialType.System_SByte:
            case SpecialType.System_Byte:
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
            case SpecialType.System_Int64:
            case SpecialType.System_UInt64:
            case SpecialType.System_IntPtr:
            case SpecialType.System_UIntPtr:
            case SpecialType.System_Single:
            case SpecialType.System_Double:
            case SpecialType.System_Decimal:
            case SpecialType.System_String:
            case SpecialType.System_Object:
            case SpecialType.System_DateTime:
                return true;
            default:
                break;
        }

        if (type is IArrayTypeSymbol { Rank: 1, ElementType.SpecialType: SpecialType.System_Byte })
        {
            return true;
        }

        return type is INamedTypeSymbol named && s_leafNames.Contains(MetadataName(named));
    }

    private static string MetadataName(INamedTypeSymbol type) =>
        type.ContainingNamespace.IsGlobalNamespace ? type.MetadataName : type.ContainingNamespace.ToDisplayString() + "." + type.MetadataName;

    // ---- Names ----

    private string UniqueName(ITypeSymbol type)
    {
        string baseName = Friendly(type);
        string name = baseName;
        int n = 1;
        // Each type gets a property, a backing field named _Name and a method named CreateName; none may meet a member the user declared.
        while (_contextMembers.Contains("_" + name) || _contextMembers.Contains("Create" + name) || !_names.Add(name))
        {
            name = baseName + (++n).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return name;
    }

    private static string Friendly(ITypeSymbol type)
    {
        switch (type)
        {
            case IArrayTypeSymbol array:
                return Friendly(array.ElementType) + "Array";
            case INamedTypeSymbol named:
                var sb = new StringBuilder();
                sb.Append(named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T ? "Nullable" : named.Name);
                foreach (ITypeSymbol argument in named.TypeArguments)
                {
                    sb.Append(Friendly(argument));
                }

                return sb.ToString();
            default:
                return "Type";
        }
    }

    // ---- Objects ----

    private void ProcessObject(TypeSpec spec)
    {
        var type = (INamedTypeSymbol)spec.Symbol;
        Location typeLocation = type.Locations.FirstOrDefault() ?? Location.None;
        var obj = new ObjectSpec { IsStruct = type.IsValueType };
        spec.Object = obj;

        IMethodSymbol? ctor = SelectConstructor(type, out bool parameterless);
        IParameterSymbol[] parameters = ctor is not null && !parameterless ? [.. ctor.Parameters] : [];
        if (ctor is not null && parameterless)
        {
            obj.Creator = CreatorKind.Parameterless;
        }
        else if (type.IsValueType && ctor is null)
        {
            obj.Creator = CreatorKind.Default;
        }
        else if (ctor is not null)
        {
            obj.Creator = CreatorKind.Constructor;
        }
        else
        {
            string reason = type.TypeKind == TypeKind.Interface ? "is an interface"
                : type.IsAbstract ? "is abstract"
                : "has no public constructor to bind through";
            Report(Diagnostics.CannotCreate, typeLocation, Show(type), reason);
        }

        foreach (IParameterSymbol p in parameters)
        {
            string defaultText = p.HasExplicitDefaultValue ? Literal(p.ExplicitDefaultValue) : "null";
            obj.Parameters.Add(new ParameterSpec(
                p.Name,
                p.Type.ToDisplayString(s_typeFormat),
                p.Type.IsValueType,
                p.Type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T },
                p.HasExplicitDefaultValue,
                defaultText));
        }

        if (obj.Creator is CreatorKind.Parameterless or CreatorKind.Constructor && !(ctor is not null && HasAttribute(ctor, _setsRequired)))
        {
            obj.InitializerText = RequiredInitializer(type);
        }

        CollectMembers(type, obj, parameters);
    }

    private IMethodSymbol? SelectConstructor(INamedTypeSymbol type, out bool parameterless)
    {
        parameterless = false;
        if (type.TypeKind == TypeKind.Interface || type.IsAbstract)
        {
            return null;
        }

        // A struct's implicit parameterless constructor is not one reflection lists; the default value stands in.
        List<IMethodSymbol> ctors = type.InstanceConstructors
            // A constructor with a ref, in or out parameter cannot be called with a value from a mapping.
            .Where(c => c.DeclaredAccessibility == Accessibility.Public && !(type.IsValueType && c.IsImplicitlyDeclared) && c.Parameters.All(x => x.RefKind == RefKind.None))
            .ToList();
        IMethodSymbol? marked = ctors.FirstOrDefault(c => HasAttribute(c, _yamlConstructor))
            ?? ctors.FirstOrDefault(c => HasAttribute(c, _jsonConstructor));
        if (marked is not null)
        {
            parameterless = marked.Parameters.Length == 0;
            return marked;
        }

        IMethodSymbol? none = ctors.FirstOrDefault(c => c.Parameters.Length == 0);
        if (none is not null)
        {
            parameterless = true;
            return none;
        }

        if (type.IsValueType)
        {
            return null;
        }

        return ctors.Count == 1 ? ctors[0] : null;
    }

    // Required members must be set where the object is made; the real values are assigned as the mapping is read.
    private static string RequiredInitializer(INamedTypeSymbol type)
    {
        var names = new List<string>();
        for (INamedTypeSymbol? t = type; t is not null && t.SpecialType != SpecialType.System_Object; t = t.BaseType)
        {
            foreach (ISymbol member in t.GetMembers())
            {
                bool required = member switch
                {
                    IPropertySymbol p => p.IsRequired && !p.IsStatic,
                    IFieldSymbol f => f.IsRequired && !f.IsStatic,
                    _ => false,
                };
                bool visible = member.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal;
                if (required && visible && !names.Contains(member.Name))
                {
                    names.Add(member.Name);
                }
            }
        }

        return names.Count == 0 ? string.Empty : " { " + string.Join(", ", names.Select(n => CodeName(n) + " = default")) + " }";
    }

    private void CollectMembers(INamedTypeSymbol type, ObjectSpec obj, IParameterSymbol[] parameters)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        bool BoundByConstructor(string name, ITypeSymbol memberType, Location where)
        {
            IParameterSymbol? parameter = parameters.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (parameter is not null && !SymbolEqualityComparer.Default.Equals(parameter.Type, memberType))
            {
                Report(Diagnostics.ParameterTypeMismatch, where, parameter.Name, Show(type), Show(parameter.Type), Show(memberType));
            }

            return parameter is not null;
        }

        // Derived members first, so a member hidden by a derived one is the one that stays.
        for (INamedTypeSymbol? t = type; t is not null && !IsRootType(t); t = t.BaseType)
        {
            foreach (IPropertySymbol p in t.GetMembers().OfType<IPropertySymbol>())
            {
                if (p.DeclaredAccessibility != Accessibility.Public || p.IsStatic || p.IsIndexer || p.RefKind != RefKind.None
                    || p.ExplicitInterfaceImplementations.Length != 0 || p.Type.IsRefLikeType || p.Type.TypeKind == TypeKind.Pointer)
                {
                    continue;
                }

                // An ignored member still takes its name, so the base member it overrides or hides stays out too.
                if (IsIgnored(p))
                {
                    names.Add(p.Name);
                    continue;
                }

                bool bound = BoundByConstructor(p.Name, p.Type, p.Locations.FirstOrDefault(l => l.IsInSource) ?? type.Locations.FirstOrDefault() ?? Location.None);
                bool publicSetter = p.SetMethod is { DeclaredAccessibility: Accessibility.Public };
                if (!publicSetter && !bound)
                {
                    continue;
                }

                if (!names.Add(p.Name))
                {
                    continue;
                }

                Location where = p.Locations.FirstOrDefault(l => l.IsInSource) ?? type.Locations.FirstOrDefault() ?? Location.None;
                TypeSpec? memberType = Require(p.Type, where, isRoot: false);
                if (memberType is null)
                {
                    continue;
                }

                MemberSpec member = NewMember(p, p.Name, memberType, p.IsRequired);
                member.CanRead = p.GetMethod is { DeclaredAccessibility: Accessibility.Public };
                // A member filled through the constructor is never assigned afterwards.
                member.Setter = !publicSetter || bound ? SetterKind.None : p.SetMethod!.IsInitOnly ? SetterKind.InitOnly : SetterKind.Plain;
                member.DeclaringTypeName = p.ContainingType.ToDisplayString(s_typeFormat);
                member.DeclaringIsGeneric = IsInGenericType(p.ContainingType);
                obj.Members.Add(member);
            }
        }

        for (INamedTypeSymbol? t = type; t is not null && !IsRootType(t); t = t.BaseType)
        {
            foreach (IFieldSymbol f in t.GetMembers().OfType<IFieldSymbol>())
            {
                if (f.DeclaredAccessibility != Accessibility.Public || f.IsStatic || f.IsConst || f.IsImplicitlyDeclared
                    || f.Type.IsRefLikeType || f.Type.TypeKind == TypeKind.Pointer || IsIgnored(f) || names.Contains(f.Name))
                {
                    continue;
                }

                bool bound = BoundByConstructor(f.Name, f.Type, f.Locations.FirstOrDefault(l => l.IsInSource) ?? type.Locations.FirstOrDefault() ?? Location.None);
                if (f.IsReadOnly && !bound)
                {
                    continue;
                }

                names.Add(f.Name);
                Location where = f.Locations.FirstOrDefault(l => l.IsInSource) ?? type.Locations.FirstOrDefault() ?? Location.None;
                TypeSpec? memberType = Require(f.Type, where, isRoot: false);
                if (memberType is null)
                {
                    continue;
                }

                MemberSpec member = NewMember(f, f.Name, memberType, f.IsRequired);
                member.CanRead = true;
                member.Setter = f.IsReadOnly || bound ? SetterKind.None : SetterKind.Plain;
                obj.Members.Add(member);
            }
        }
    }

    private static bool IsInGenericType(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? t = type; t is not null; t = t.ContainingType)
        {
            if (t.IsGenericType)
            {
                return true;
            }
        }

        return false;
    }

    // A member name as it must appear in code: a keyword needs the @ prefix.
    internal static string CodeName(string name) =>
        SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None || SyntaxFacts.GetContextualKeywordKind(name) != SyntaxKind.None ? "@" + name : name;

    private static bool IsRootType(INamedTypeSymbol t) =>
        t.SpecialType is SpecialType.System_Object or SpecialType.System_ValueType or SpecialType.System_Enum;

    private MemberSpec NewMember(ISymbol symbol, string name, TypeSpec spec, bool isRequiredKeyword)
    {
        string? explicitName = StringArgument(FindAttribute(symbol, _yamlName)) ?? StringArgument(FindAttribute(symbol, _jsonName));
        bool required = isRequiredKeyword
            || FindAttribute(symbol, _yamlRequired) is not null
            || FindAttribute(symbol, _jsonRequired) is not null;
        int order = IntArgument(FindAttribute(symbol, _yamlOrder)) ?? IntArgument(FindAttribute(symbol, _jsonOrder)) ?? 0;
        int? condition = IgnoreConditionOf(symbol);
        return new MemberSpec(name, spec.Name, spec)
        {
            SerializedName = explicitName,
            IsRequired = required,
            Order = order,
            IgnoreCondition = condition is 0 or 2 or 3 ? condition : null,
        };
    }

    private bool IsIgnored(ISymbol symbol)
    {
        // The condition 1 (Always) is the default when none is named, in both enums.
        return IgnoreConditionOf(symbol) == 1;
    }

    // The ignore condition a member names: 0 never, 1 always, 2 when writing default, 3 when writing null; null for no attribute.
    private int? IgnoreConditionOf(ISymbol symbol)
    {
        AttributeData? attribute = FindAttribute(symbol, _yamlIgnore) ?? FindAttribute(symbol, _jsonIgnore);
        if (attribute is null)
        {
            return null;
        }

        foreach (KeyValuePair<string, TypedConstant> named in attribute.NamedArguments)
        {
            if (named.Key == "Condition")
            {
                return named.Value.Value is int condition ? condition : 1;
            }
        }

        return 1;
    }

    private static bool HasAttribute(ISymbol symbol, INamedTypeSymbol? attribute) => FindAttribute(symbol, attribute) is not null;

    // Only the attributes on the member itself count, not those on a base member it overrides: that is
    // what the reflection resolver reads and how System.Text.Json treats member attributes.
    private static AttributeData? FindAttribute(ISymbol symbol, INamedTypeSymbol? attribute)
    {
        if (attribute is null)
        {
            return null;
        }

        foreach (AttributeData data in symbol.GetAttributes())
        {
            if (SymbolEqualityComparer.Default.Equals(data.AttributeClass, attribute))
            {
                return data;
            }
        }

        return null;
    }

    private static string? StringArgument(AttributeData? data) =>
        data is { ConstructorArguments.Length: > 0 } ? data.ConstructorArguments[0].Value as string : null;

    private static int? IntArgument(AttributeData? data) =>
        data is { ConstructorArguments.Length: > 0 } && data.ConstructorArguments[0].Value is int value ? value : null;

    // A default value as C# text that boxes to the parameter's own type, as reflection reports it.
    private static string Literal(object? value)
    {
        switch (value)
        {
            case null:
                return "null";
            case string s:
                return SymbolDisplay.FormatLiteral(s, quote: true);
            case bool b:
                return b ? "true" : "false";
            case char c:
                return SymbolDisplay.FormatLiteral(c, quote: true);
            case float f when float.IsNaN(f) || float.IsInfinity(f):
                return float.IsNaN(f) ? "float.NaN" : f > 0 ? "float.PositiveInfinity" : "float.NegativeInfinity";
            case double d when double.IsNaN(d) || double.IsInfinity(d):
                return double.IsNaN(d) ? "double.NaN" : d > 0 ? "double.PositiveInfinity" : "double.NegativeInfinity";
            case sbyte:
            case byte:
            case short:
            case ushort:
            case int:
            case uint:
            case long:
            case ulong:
            case float:
            case double:
            case decimal:
                string keyword = value switch
                {
                    sbyte => "sbyte",
                    byte => "byte",
                    short => "short",
                    ushort => "ushort",
                    int => "int",
                    uint => "uint",
                    long => "long",
                    ulong => "ulong",
                    float => "float",
                    double => "double",
                    _ => "decimal",
                };
                return "unchecked((" + keyword + ")(" + SymbolDisplay.FormatPrimitive(value, quoteStrings: true, useHexadecimalNumbers: false) + "))";
            default:
                return "null";
        }
    }

    // ---- Cycles ----

    // Collection metadata is built from the metadata of its elements when it is first used, so a collection that
    // contains itself would recurse without end. Object members are resolved later, so they cannot cycle.
    private void CheckCollectionCycles(Location location)
    {
        var state = new Dictionary<TypeSpec, int>();
        foreach (TypeSpec start in _order)
        {
            if (state.ContainsKey(start) || start.Shape == TypeShape.Object)
            {
                continue;
            }

            // 1 = on the path, 2 = done. An explicit stack keeps the walk off the call stack.
            var stack = new Stack<(TypeSpec Spec, int Next)>();
            stack.Push((start, 0));
            state[start] = 1;
            while (stack.Count > 0)
            {
                (TypeSpec spec, int next) = stack.Pop();
                if (next >= spec.Dependencies.Count)
                {
                    state[spec] = 2;
                    continue;
                }

                stack.Push((spec, next + 1));
                TypeSpec dependency = spec.Dependencies[next];
                if (dependency.Shape == TypeShape.Object)
                {
                    continue;
                }

                if (state.TryGetValue(dependency, out int s))
                {
                    if (s == 1)
                    {
                        Report(Diagnostics.CollectionCycle, location, Show(dependency.Symbol));
                        return;
                    }

                    continue;
                }

                state[dependency] = 1;
                stack.Push((dependency, 0));
            }
        }
    }
}
