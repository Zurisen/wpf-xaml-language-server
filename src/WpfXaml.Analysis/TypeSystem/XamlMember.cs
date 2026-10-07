using Microsoft.CodeAnalysis;

namespace WpfXaml.Analysis.TypeSystem;

public enum XamlMemberKind
{
    Property,
    Event,
    AttachedProperty,
    AttachedEvent,
}

/// <summary>A member usable from XAML: CLR property, CLR event, or attachable property/event (<c>GetX</c>/<c>SetX</c>, <c>AddXHandler</c>).</summary>
public sealed class XamlMember
{
    internal XamlMember(string name, XamlMemberKind kind, XamlTypeInfo owner, ISymbol symbol, ITypeSymbol? valueType, bool isReadOnly)
    {
        Name = name;
        Kind = kind;
        Owner = owner;
        Symbol = symbol;
        ValueType = valueType;
        IsReadOnly = isReadOnly;
    }

    public string Name { get; }

    public XamlMemberKind Kind { get; }

    /// <summary>The type the member was looked up on (for attached members, the owner such as <c>Grid</c>).</summary>
    public XamlTypeInfo Owner { get; }

    /// <summary>Property, event, or the getter/setter/add-handler method of an attached member.</summary>
    public ISymbol Symbol { get; }

    /// <summary>Property type, or the handler delegate type for events.</summary>
    public ITypeSymbol? ValueType { get; }

    /// <summary>No public setter (attached: no <c>SetX</c>). Collections can still be filled through property elements.</summary>
    public bool IsReadOnly { get; }

    public bool IsEvent => Kind is XamlMemberKind.Event or XamlMemberKind.AttachedEvent;

    public bool IsAttached => Kind is XamlMemberKind.AttachedProperty or XamlMemberKind.AttachedEvent;

    /// <summary>The <c>XxxProperty</c> / <c>XxxEvent</c> identifier field, if there is one.</summary>
    public IFieldSymbol? IdentifierField =>
        Owner.Symbol.BaseTypesAndSelf()
            .SelectMany(t => t.GetMembers(Name + (IsEvent ? "Event" : "Property")))
            .OfType<IFieldSymbol>()
            .FirstOrDefault(f => f.IsStatic);

    public override string ToString() => IsAttached ? $"{Owner.Symbol.Name}.{Name}" : Name;
}
