using Microsoft.CodeAnalysis;

namespace WpfXaml.Analysis.TypeSystem;

/// <summary>XAML view of a CLR type: its settable members, attachable members and XAML-relevant attributes.</summary>
public sealed class XamlTypeInfo
{
    private readonly XamlTypeSystem _typeSystem;
    private readonly Lazy<Dictionary<string, XamlMember>> _members;
    private readonly Lazy<Dictionary<string, XamlMember>> _attachableMembers;
    private readonly Lazy<string?> _contentProperty;
    private readonly Lazy<string?> _runtimeNameProperty;

    internal XamlTypeInfo(XamlTypeSystem typeSystem, INamedTypeSymbol symbol)
    {
        _typeSystem = typeSystem;
        Symbol = symbol;
        _members = new Lazy<Dictionary<string, XamlMember>>(BuildMembers);
        _attachableMembers = new Lazy<Dictionary<string, XamlMember>>(BuildAttachableMembers);
        _contentProperty = new Lazy<string?>(() => FindStringAttribute(typeSystem.Known.ContentPropertyAttribute));
        _runtimeNameProperty = new Lazy<string?>(() => FindStringAttribute(typeSystem.Known.RuntimeNamePropertyAttribute));
    }

    public INamedTypeSymbol Symbol { get; }

    public string Name => Symbol.Name;

    /// <summary>The name used in markup: markup extensions drop their <c>Extension</c> suffix.</summary>
    public string XamlName => IsMarkupExtension && Name.EndsWith("Extension", StringComparison.Ordinal) && Name.Length > "Extension".Length
        ? Name[..^"Extension".Length]
        : Name;

    public IEnumerable<XamlMember> Members => _members.Value.Values;

    public IEnumerable<XamlMember> AttachableMembers => _attachableMembers.Value.Values;

    public bool HasAttachableMembers => _attachableMembers.Value.Count > 0;

    /// <summary>From <c>[ContentProperty]</c>, inherited.</summary>
    public string? ContentPropertyName => _contentProperty.Value;

    /// <summary>From <c>[RuntimeNameProperty]</c>, inherited (e.g. <c>Name</c> on FrameworkElement).</summary>
    public string? RuntimeNamePropertyName => _runtimeNameProperty.Value;

    public bool IsMarkupExtension => Symbol.DerivesFrom(_typeSystem.Known.MarkupExtension);

    public bool IsTemplate => Symbol.DerivesFrom(_typeSystem.Known.FrameworkTemplate);

    public bool IsCollection =>
        Symbol.IsAssignableTo(_typeSystem.Known.IList) || Symbol.IsAssignableTo(_typeSystem.Known.IDictionary) || Symbol.IsAssignableTo(_typeSystem.Known.IAddChild);

    /// <summary>Can appear as an object element: concrete, non-generic, with a public parameterless constructor.</summary>
    public bool IsCreatable =>
        Symbol is { IsAbstract: false, IsStatic: false, IsGenericType: false, IsImplicitlyDeclared: false }
        && Symbol.TypeKind is TypeKind.Class or TypeKind.Struct
        && (Symbol.TypeKind == TypeKind.Struct || Symbol.InstanceConstructors.Any(c => c.Parameters.Length == 0 && IsAccessible(c)));

    public XamlMember? GetMember(string name) => _members.Value.GetValueOrDefault(name);

    public XamlMember? GetAttachableMember(string name) => _attachableMembers.Value.GetValueOrDefault(name);

    internal bool IsAccessible(ISymbol symbol) =>
        symbol.DeclaredAccessibility == Accessibility.Public
        || (symbol.DeclaredAccessibility is Accessibility.Internal or Accessibility.ProtectedOrInternal
            && SymbolEqualityComparer.Default.Equals(symbol.ContainingAssembly, _typeSystem.Compilation.Assembly));

    private Dictionary<string, XamlMember> BuildMembers()
    {
        var result = new Dictionary<string, XamlMember>(StringComparer.Ordinal);
        foreach (var type in Symbol.BaseTypesAndSelf())
        {
            foreach (var member in type.GetMembers())
            {
                if (member.IsStatic || !IsAccessible(member) || result.ContainsKey(member.Name))
                {
                    continue;
                }

                switch (member)
                {
                    case IPropertySymbol { IsIndexer: false } p when p.GetMethod is not null || p.SetMethod is not null:
                        var readOnly = p.SetMethod is null || !IsAccessible(p.SetMethod);
                        result[p.Name] = new XamlMember(p.Name, XamlMemberKind.Property, this, p, p.Type, readOnly);
                        break;
                    case IEventSymbol e:
                        result[e.Name] = new XamlMember(e.Name, XamlMemberKind.Event, this, e, e.Type, isReadOnly: false);
                        break;
                }
            }
        }

        return result;
    }

    private Dictionary<string, XamlMember> BuildAttachableMembers()
    {
        var result = new Dictionary<string, XamlMember>(StringComparer.Ordinal);
        var getters = new Dictionary<string, IMethodSymbol>(StringComparer.Ordinal);
        var setters = new Dictionary<string, IMethodSymbol>(StringComparer.Ordinal);

        foreach (var type in Symbol.BaseTypesAndSelf())
        {
            foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
            {
                if (!method.IsStatic || method.MethodKind != MethodKind.Ordinary || !IsAccessible(method) || method.IsGenericMethod)
                {
                    continue;
                }

                var name = method.Name;
                if (name.Length > 3 && name.StartsWith("Get", StringComparison.Ordinal) && method.Parameters.Length == 1
                    && !method.ReturnsVoid && IsAttachTarget(method.Parameters[0].Type))
                {
                    getters.TryAdd(name[3..], method);
                }
                else if (name.Length > 3 && name.StartsWith("Set", StringComparison.Ordinal) && method.Parameters.Length == 2
                    && IsAttachTarget(method.Parameters[0].Type))
                {
                    setters.TryAdd(name[3..], method);
                }
                else if (name.Length > "AddHandler".Length && name.StartsWith("Add", StringComparison.Ordinal)
                    && name.EndsWith("Handler", StringComparison.Ordinal) && method.Parameters.Length == 2
                    && IsAttachTarget(method.Parameters[0].Type) && method.Parameters[1].Type.TypeKind == TypeKind.Delegate)
                {
                    var eventName = name["Add".Length..^"Handler".Length];
                    result.TryAdd(eventName, new XamlMember(eventName, XamlMemberKind.AttachedEvent, this, method, method.Parameters[1].Type, isReadOnly: false));
                }
            }
        }

        foreach (var (name, setter) in setters)
        {
            // Prefer the getter as the documented symbol: its summary reads "Gets the value of the X attached property".
            var symbol = (ISymbol?)getters.GetValueOrDefault(name) ?? setter;
            result.TryAdd(name, new XamlMember(name, XamlMemberKind.AttachedProperty, this, symbol, setter.Parameters[1].Type, isReadOnly: false));
        }

        foreach (var (name, getter) in getters)
        {
            result.TryAdd(name, new XamlMember(name, XamlMemberKind.AttachedProperty, this, getter, getter.ReturnType, isReadOnly: true));
        }

        // Any routed event can be attached through its identifier field: <StackPanel Button.Click="..." />.
        if (_typeSystem.Known.RoutedEvent is { } routedEvent)
        {
            foreach (var type in Symbol.BaseTypesAndSelf())
            {
                foreach (var field in type.GetMembers().OfType<IFieldSymbol>())
                {
                    if (field.IsStatic && field.Name.Length > "Event".Length && field.Name.EndsWith("Event", StringComparison.Ordinal)
                        && IsAccessible(field) && SymbolEqualityComparer.Default.Equals(field.Type, routedEvent))
                    {
                        var eventName = field.Name[..^"Event".Length];
                        var clrEvent = type.GetMembers(eventName).OfType<IEventSymbol>().FirstOrDefault();
                        result.TryAdd(eventName, new XamlMember(eventName, XamlMemberKind.AttachedEvent, this, (ISymbol?)clrEvent ?? field, clrEvent?.Type, isReadOnly: false));
                    }
                }
            }
        }

        return result;
    }

    private bool IsAttachTarget(ITypeSymbol type) =>
        type.SpecialType == SpecialType.System_Object
        || type.TypeKind == TypeKind.Interface
        || type.DerivesFrom(_typeSystem.Known.DependencyObject);

    private string? FindStringAttribute(INamedTypeSymbol? attributeType)
    {
        if (attributeType is null)
        {
            return null;
        }

        foreach (var type in Symbol.BaseTypesAndSelf())
        {
            foreach (var attribute in type.GetAttributes())
            {
                if (SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, attributeType)
                    && attribute.ConstructorArguments is [{ Value: string value }])
                {
                    return value;
                }
            }
        }

        return null;
    }

    public override string ToString() => Symbol.ToDisplayString();
}
