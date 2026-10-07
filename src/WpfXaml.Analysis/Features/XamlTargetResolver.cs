using Microsoft.CodeAnalysis;
using WpfXaml.Analysis.Semantics;
using WpfXaml.Analysis.TypeSystem;
using WpfXaml.Language.Syntax;
using WpfXaml.Language.Text;

namespace WpfXaml.Analysis.Features;

public enum XamlTargetKind
{
    /// <summary>An element name, property-element owner, attached-member owner, or type written as a value.</summary>
    Type,
    Member,
    Directive,
    MarkupExtension,
    EnumValue,
    EventHandler,

    /// <summary>The key inside <c>{StaticResource Key}</c>.</summary>
    ResourceReference,

    /// <summary>The value of an <c>x:Key</c>.</summary>
    ResourceKey,

    /// <summary>The value of <c>x:Class</c>.</summary>
    ClassName,

    /// <summary>The <c>Type.Member</c> argument of <c>{x:Static}</c>.</summary>
    StaticMember,
}

/// <summary>What the caret is on: the shared input of hover and go to definition.</summary>
public sealed record XamlTarget(XamlTargetKind Kind, TextSpan Span)
{
    /// <summary>The most specific symbol (type, property, event, method, field).</summary>
    public ISymbol? Symbol { get; init; }

    public XamlTypeInfo? Type { get; init; }

    public XamlMember? Member { get; init; }

    public XamlDirective? Directive { get; init; }

    /// <summary>Resource key, handler name or class name, as written.</summary>
    public string? Name { get; init; }
}

public static class XamlTargetResolver
{
    public static XamlTarget? Resolve(XamlSemanticModel model, int offset)
    {
        var document = model.Document;
        var element = document.FindStartTagAt(offset);
        if (element is null)
        {
            // End tag names resolve like the start tag.
            var closing = document.AllElements().FirstOrDefault(e => e.EndTagName is { IsEmpty: false } n && n.Span.ContainsOrTouches(offset));
            return closing is null ? null : ResolveElementName(model, closing, closing.EndTagName!, offset);
        }

        if (!element.Name.IsEmpty && element.Name.Span.ContainsOrTouches(offset))
        {
            return ResolveElementName(model, element, element.Name, offset);
        }

        foreach (var attribute in element.Attributes)
        {
            if (!attribute.Name.IsEmpty && attribute.Name.Span.ContainsOrTouches(offset))
            {
                return ResolveAttributeName(model, attribute, offset);
            }

            if (attribute.ValueSpan is { } valueSpan && valueSpan.ContainsOrTouches(offset))
            {
                return ResolveAttributeValue(model, attribute, offset);
            }
        }

        return null;
    }

    private static XamlTarget? ResolveElementName(XamlSemanticModel model, XamlElement element, XamlName name, int offset)
    {
        var info = model.GetElementInfo(element);
        return info.Kind switch
        {
            ElementKind.Object when info.Type is { } type => TypeTarget(type, name.Span),
            ElementKind.Directive => new XamlTarget(XamlTargetKind.Directive, name.Span) { Directive = info.Directive },
            ElementKind.PropertyElement when info.OwnerType is { } owner && name.OwnerSpan.ContainsOrTouches(offset) && offset < name.MemberNameSpan.Start
                => TypeTarget(owner, name.OwnerSpan),
            ElementKind.PropertyElement when info.Member is { } member => MemberTarget(member, name.MemberNameSpan),
            _ => null,
        };
    }

    private static XamlTarget? ResolveAttributeName(XamlSemanticModel model, XamlAttribute attribute, int offset)
    {
        var info = model.GetAttributeInfo(attribute);
        var name = attribute.Name;
        switch (info.Kind)
        {
            case AttributeKind.Directive:
                return new XamlTarget(XamlTargetKind.Directive, name.Span) { Directive = info.Directive };
            case AttributeKind.Member when info.Member is { } member:
                if (name.IsDotted && offset < name.MemberNameSpan.Start)
                {
                    return TypeTarget(member.Owner, name.OwnerSpan);
                }

                return MemberTarget(member, name.IsDotted ? name.MemberNameSpan : name.Span);
            default:
                return null;
        }
    }

    private static XamlTarget? ResolveAttributeValue(XamlSemanticModel model, XamlAttribute attribute, int offset)
    {
        var element = attribute.Element;
        if (model.GetMarkupExtension(attribute) is { Root: { } root })
        {
            return ResolveMarkupExtension(model, element, root, offset);
        }

        var value = attribute.Value ?? string.Empty;
        var span = attribute.ValueSpan!.Value;
        var info = model.GetAttributeInfo(attribute);

        if (info.Kind == AttributeKind.Directive)
        {
            return info.Directive?.Name switch
            {
                "Class" => new XamlTarget(XamlTargetKind.ClassName, span) { Symbol = model.ClassSymbol, Name = value },
                "Key" => new XamlTarget(XamlTargetKind.ResourceKey, span) { Name = value },
                _ => null,
            };
        }

        if (info.Member is not { } member)
        {
            return null;
        }

        if (member.IsEvent)
        {
            var handler = model.ClassSymbol?.BaseTypesAndSelf().SelectMany(t => t.GetMembers(value.Trim())).OfType<IMethodSymbol>().FirstOrDefault();
            return new XamlTarget(XamlTargetKind.EventHandler, span) { Symbol = handler, Name = value.Trim() };
        }

        return ResolveLiteral(model, element, member, value, span);
    }

    private static XamlTarget? ResolveLiteral(XamlSemanticModel model, XamlElement scope, XamlMember member, string value, TextSpan span)
    {
        var valueType = member.ValueType?.UnwrapNullable();
        if (valueType?.TypeKind == TypeKind.Enum)
        {
            var field = valueType.GetMembers().OfType<IFieldSymbol>().FirstOrDefault(f => string.Equals(f.Name, value.Trim(), StringComparison.OrdinalIgnoreCase));
            return field is null ? null : new XamlTarget(XamlTargetKind.EnumValue, span) { Symbol = field };
        }

        if (IsTypeValued(model, member))
        {
            return model.ResolveTypeReference(scope, value) is { } type ? TypeTarget(type, span) : null;
        }

        if (valueType is not null && valueType.DerivesFrom(model.Types.Known.DependencyProperty)
            && model.FindTargetType(scope) is { } targetType)
        {
            var name = value.Trim();
            var dotted = name.LastIndexOf('.');
            var property = dotted >= 0
                ? model.ResolveTypeReference(scope, name[..dotted])?.GetAttachableMember(name[(dotted + 1)..])
                : targetType.GetMember(name);
            return property is null ? null : MemberTarget(property, span);
        }

        return null;
    }

    private static XamlTarget? ResolveMarkupExtension(XamlSemanticModel model, XamlElement scope, MarkupExtensionSyntax root, int offset)
    {
        var extension = root.DescendantsAndSelf().LastOrDefault(e => e.Span.ContainsOrTouches(offset));
        if (extension is null)
        {
            return null;
        }

        var type = model.ResolveMarkupExtensionType(scope, extension.TypeName, out _);
        if (!extension.TypeName.IsEmpty && extension.TypeName.Span.ContainsOrTouches(offset))
        {
            return type is null ? null : new XamlTarget(XamlTargetKind.MarkupExtension, extension.TypeName.Span) { Symbol = type.Symbol, Type = type };
        }

        if (type is null)
        {
            return null;
        }

        foreach (var argument in extension.Arguments)
        {
            if (argument.NameSpan is { } nameSpan && nameSpan.ContainsOrTouches(offset))
            {
                return type.GetMember(argument.Name!) is { } member ? MemberTarget(member, nameSpan) : null;
            }

            if (argument.Value.Extension is null && argument.Value.Span.ContainsOrTouches(offset))
            {
                return ResolveMarkupArgumentValue(model, scope, type, argument);
            }
        }

        return null;
    }

    private static XamlTarget? ResolveMarkupArgumentValue(XamlSemanticModel model, XamlElement scope, XamlTypeInfo extensionType, MarkupArgumentSyntax argument)
    {
        var text = argument.Value.Text.Trim();
        var span = argument.Value.Span;
        var known = model.Types.Known;

        if (IsResourceExtension(model, extensionType) && (argument.IsPositional || argument.Name == "ResourceKey"))
        {
            return new XamlTarget(XamlTargetKind.ResourceReference, span) { Name = text };
        }

        if (argument.IsPositional)
        {
            var parameterType = GetPositionalParameterType(extensionType, argument);
            if (SymbolEqualityComparer.Default.Equals(parameterType, known.Type))
            {
                return model.ResolveTypeReference(scope, text) is { } type ? TypeTarget(type, span) : null;
            }

            if (extensionType.Name == "StaticExtension")
            {
                return ResolveStaticMember(model, scope, text, span);
            }

            if (parameterType?.TypeKind == TypeKind.Enum)
            {
                var field = parameterType.GetMembers().OfType<IFieldSymbol>().FirstOrDefault(f => string.Equals(f.Name, text, StringComparison.OrdinalIgnoreCase));
                return field is null ? null : new XamlTarget(XamlTargetKind.EnumValue, span) { Symbol = field };
            }

            return null;
        }

        if (extensionType.GetMember(argument.Name!) is { } member)
        {
            if (extensionType.Name == "StaticExtension" && member.Name == "Member")
            {
                return ResolveStaticMember(model, scope, text, span);
            }

            return ResolveLiteral(model, scope, member, text, span);
        }

        return null;
    }

    private static XamlTarget? ResolveStaticMember(XamlSemanticModel model, XamlElement scope, string text, TextSpan span)
    {
        var dot = text.LastIndexOf('.');
        if (dot < 0 || model.ResolveTypeReference(scope, text[..dot]) is not { } type)
        {
            return null;
        }

        var symbol = type.Symbol.BaseTypesAndSelf().SelectMany(t => t.GetMembers(text[(dot + 1)..])).FirstOrDefault(s => s.IsStatic);
        return symbol is null ? null : new XamlTarget(XamlTargetKind.StaticMember, span) { Symbol = symbol, Type = type };
    }

    internal static bool IsResourceExtension(XamlSemanticModel model, XamlTypeInfo type) =>
        type.Symbol.DerivesFrom(model.Types.Known.StaticResourceExtension) || type.Symbol.DerivesFrom(model.Types.Known.DynamicResourceExtension);

    /// <summary>
    /// Members whose string value names a type: real <c>System.Type</c> properties, plus <c>DataType</c>
    /// (typed as object on DataTemplate but always given a type in practice).
    /// </summary>
    internal static bool IsTypeValued(XamlSemanticModel model, XamlMember member) =>
        SymbolEqualityComparer.Default.Equals(member.ValueType, model.Types.Known.Type)
        || (member.Name == "DataType" && member.ValueType?.SpecialType == SpecialType.System_Object);

    /// <summary>Type of the constructor parameter a positional argument maps to.</summary>
    internal static ITypeSymbol? GetPositionalParameterType(XamlTypeInfo extensionType, MarkupArgumentSyntax argument)
    {
        var positional = argument.Owner.PositionalArguments.ToList();
        var index = positional.IndexOf(argument);
        var constructor = extensionType.Symbol.InstanceConstructors
            .Where(c => c.DeclaredAccessibility == Accessibility.Public)
            .FirstOrDefault(c => c.Parameters.Length == positional.Count);
        return constructor is null || index < 0 ? null : constructor.Parameters[index].Type;
    }

    private static XamlTarget TypeTarget(XamlTypeInfo type, TextSpan span) =>
        new(XamlTargetKind.Type, span) { Symbol = type.Symbol, Type = type };

    private static XamlTarget MemberTarget(XamlMember member, TextSpan span) =>
        new(XamlTargetKind.Member, span) { Symbol = member.Symbol, Member = member, Type = member.Owner };
}
