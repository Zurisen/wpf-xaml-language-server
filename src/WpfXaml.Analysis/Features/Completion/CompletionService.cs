using Microsoft.CodeAnalysis;
using WpfXaml.Analysis.Semantics;
using WpfXaml.Analysis.TypeSystem;
using WpfXaml.Language.Syntax;
using WpfXaml.Language.Text;

namespace WpfXaml.Analysis.Features.Completion;

/// <summary>
/// Context-sensitive completion. The context is read from the error-tolerant syntax tree:
/// element names, attribute names, attribute values, markup extensions, and closing tags.
/// </summary>
public sealed class CompletionService
{
    private readonly XamlSemanticModel _model;
    private readonly XamlDocument _document;

    private CompletionService(XamlSemanticModel model)
    {
        _model = model;
        _document = model.Document;
    }

    public static XamlCompletionList? GetCompletions(XamlSemanticModel model, int offset) =>
        new CompletionService(model).Complete(offset);

    private XamlCompletionList? Complete(int offset)
    {
        if (TryCloseTag(offset) is { } closing)
        {
            return closing;
        }

        var element = _document.FindStartTagAt(offset);
        if (element is null)
        {
            return null;
        }

        if (element.Name.Span.ContainsOrTouches(offset) || offset == element.StartTagSpan.Start + 1)
        {
            return new XamlCompletionList(element.Name.Span, ElementNameItems(element).ToList());
        }

        foreach (var attribute in element.Attributes)
        {
            if (!attribute.Name.IsEmpty && attribute.Name.Span.ContainsOrTouches(offset))
            {
                return new XamlCompletionList(attribute.Name.Span, AttributeNameItems(element, attribute).ToList());
            }

            if (attribute.ValueSpan is { } valueSpan && valueSpan.ContainsOrTouches(offset))
            {
                return AttributeValue(attribute, valueSpan, offset);
            }
        }

        if (offset > 0 && char.IsWhiteSpace(_document.Text[offset - 1]))
        {
            return new XamlCompletionList(new TextSpan(offset, 0), AttributeNameItems(element, null).ToList());
        }

        return null;
    }

    // ---------------------------------------------------------------- closing tags

    private XamlCompletionList? TryCloseTag(int offset)
    {
        var text = _document.Text;
        var start = offset;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] is '.' or ':' or '_' or '-'))
        {
            start--;
        }

        if (start < 2 || text[start - 1] != '/' || text[start - 2] != '<')
        {
            return null;
        }

        var tagStart = start - 2;
        var open = _document.AllElements()
            .Where(e => !e.IsSelfClosing && e.StartTagSpan.End <= tagStart && (e.EndTagSpan is null || e.EndTagSpan.Value.Start >= tagStart))
            .MaxBy(e => e.StartTagSpan.Start);
        if (open is null || open.Name.IsEmpty)
        {
            return null;
        }

        var end = offset;
        while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] is '.' or ':' or '_' or '-'))
        {
            end++;
        }

        var hasBracket = end < text.Length && text[end] == '>';
        var name = open.Name.FullName;
        return new XamlCompletionList(
            TextSpan.FromBounds(start, end),
            [new XamlCompletionItem(name, XamlCompletionKind.ClosingTag) { InsertText = hasBracket ? name : name + ">", Detail = "Close element" }]);
    }

    // ---------------------------------------------------------------- element names

    private IEnumerable<XamlCompletionItem> ElementNameItems(XamlElement element)
    {
        var parent = element.Parent;
        if (parent is not null && !parent.IsPropertyElement && _model.GetElementInfo(parent).Type is { } parentType)
        {
            // Property elements of the parent: <Grid.RowDefinitions>
            var ownerName = parent.Name.FullName;
            foreach (var member in parentType.Members.Where(m => !m.IsEvent).OrderBy(m => m.Name, StringComparer.Ordinal))
            {
                yield return new XamlCompletionItem($"{ownerName}.{member.Name}", XamlCompletionKind.Property)
                {
                    Detail = DocumentationFormatter.ShortTypeName(member.ValueType),
                    Symbol = member.Symbol,
                    SortText = "0" + member.Name,
                };
            }
        }

        foreach (var item in TypeItems(element, t => t.IsCreatable, XamlCompletionKind.Type))
        {
            yield return item with { SortText = "1" + item.Label };
        }

        if (_model.GetXamlLanguagePrefix(element) is { } x)
        {
            foreach (var directive in XamlDirectives.All.Where(d => d.Usage == DirectiveUsage.Element))
            {
                yield return new XamlCompletionItem($"{x}:{directive.Name}", XamlCompletionKind.Directive)
                {
                    Documentation = directive.Description,
                    SortText = "2" + directive.Name,
                };
            }
        }
    }

    /// <summary>Types from every namespace in scope, labelled with the prefix the document uses.</summary>
    private IEnumerable<XamlCompletionItem> TypeItems(XamlElement scope, Func<XamlTypeInfo, bool> filter, XamlCompletionKind kind)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (prefix, ns) in NamespacesInScope(scope))
        {
            foreach (var type in ns.Types)
            {
                if (!filter(type))
                {
                    continue;
                }

                var label = Qualify(prefix, type.XamlName);
                if (seen.Add(label))
                {
                    yield return new XamlCompletionItem(label, kind)
                    {
                        Detail = type.Symbol.ContainingNamespace?.ToDisplayString(),
                        Symbol = type.Symbol,
                    };
                }
            }
        }
    }

    private IEnumerable<(string? Prefix, XamlNamespace Namespace)> NamespacesInScope(XamlElement scope)
    {
        foreach (var (prefix, uri) in scope.GetNamespacesInScope())
        {
            if (_model.IsIgnorableNamespace(scope, uri))
            {
                continue;
            }

            if (_model.Types.GetNamespace(uri) is { } ns)
            {
                yield return (prefix, ns);
            }
        }
    }

    private static string Qualify(string? prefix, string name) => prefix is null ? name : $"{prefix}:{name}";

    // ---------------------------------------------------------------- attribute names

    private IEnumerable<XamlCompletionItem> AttributeNameItems(XamlElement element, XamlAttribute? current)
    {
        var info = _model.GetElementInfo(element);
        if (info.Kind is ElementKind.PropertyElement or ElementKind.Ignored or ElementKind.Directive)
        {
            yield break;
        }

        var existing = element.Attributes.Where(a => a != current).Select(a => a.Name.FullName).ToHashSet(StringComparer.Ordinal);
        var needsValue = current is null || !current.HasEquals;

        XamlCompletionItem Attribute(string label, XamlCompletionKind kind, string sort) => new(label, kind)
        {
            InsertText = needsValue ? $"{label}=\"\"" : label,
            SnippetText = needsValue ? $"{label}=\"$1\"" : null,
            SortText = sort + label,
        };

        if (info.Type is { } type)
        {
            foreach (var member in type.Members)
            {
                if ((member.IsReadOnly && !member.IsEvent) || existing.Contains(member.Name))
                {
                    continue;
                }

                yield return Attribute(member.Name, member.IsEvent ? XamlCompletionKind.Event : XamlCompletionKind.Property, "0") with
                {
                    Detail = DocumentationFormatter.ShortTypeName(member.ValueType),
                    Symbol = member.Symbol,
                };
            }
        }

        foreach (var (prefix, ns) in NamespacesInScope(element))
        {
            if (ns.Uri == XmlNamespaces.Xaml2006)
            {
                continue;
            }

            foreach (var owner in ns.AttachableOwners)
            {
                foreach (var member in owner.AttachableMembers)
                {
                    // Inherited attachables (every control re-exposing UIElement's routed events) are valid
                    // but would multiply the list; offer each under its declaring type only.
                    if (member.IsReadOnly || !SymbolEqualityComparer.Default.Equals(member.Symbol.ContainingType, owner.Symbol))
                    {
                        continue;
                    }

                    var label = Qualify(prefix, $"{owner.Name}.{member.Name}");
                    if (!existing.Contains(label))
                    {
                        var kind = member.IsEvent ? XamlCompletionKind.Event : XamlCompletionKind.AttachedProperty;
                        yield return Attribute(label, kind, "1") with
                        {
                            Detail = DocumentationFormatter.ShortTypeName(member.ValueType),
                            Symbol = member.Symbol,
                        };
                    }
                }
            }
        }

        if (_model.GetXamlLanguagePrefix(element) is { } x)
        {
            foreach (var directive in XamlDirectives.All)
            {
                var allowed = directive.Usage == DirectiveUsage.AnyElement || (directive.Usage == DirectiveUsage.Root && element.Parent is null);
                var label = $"{x}:{directive.Name}";
                if (allowed && !existing.Contains(label))
                {
                    yield return Attribute(label, XamlCompletionKind.Directive, "2") with { Documentation = directive.Description };
                }
            }
        }

        var designPrefix = element.GetNamespacesInScope().FirstOrDefault(n => n.Namespace == XmlNamespaces.Blend2008).Prefix;
        if (designPrefix is not null)
        {
            foreach (var (name, description) in XamlDirectives.DesignTimeAttributes)
            {
                var label = $"{designPrefix}:{name}";
                if (!existing.Contains(label))
                {
                    yield return Attribute(label, XamlCompletionKind.Directive, "3") with { Documentation = description };
                }
            }
        }
    }

    // ---------------------------------------------------------------- attribute values

    private XamlCompletionList? AttributeValue(XamlAttribute attribute, TextSpan valueSpan, int offset)
    {
        var element = attribute.Element;
        var raw = attribute.RawValue ?? string.Empty;
        // "{}" is the literal escape, except right after an editor auto-paired the braces: "{|}".
        if (raw.StartsWith('{') && (!raw.StartsWith("{}", StringComparison.Ordinal) || offset == valueSpan.Start + 1))
        {
            return MarkupExtension(element, attribute, raw, valueSpan, offset);
        }

        if (attribute.IsNamespaceDeclaration)
        {
            return new XamlCompletionList(valueSpan, NamespaceUriItems().ToList());
        }

        var info = _model.GetAttributeInfo(attribute);
        if (info.Kind == AttributeKind.Directive)
        {
            return new XamlCompletionList(valueSpan, DirectiveValueItems(info.Directive!).ToList());
        }

        if (info.Member is not { } member)
        {
            return null;
        }

        return new XamlCompletionList(valueSpan, MemberValueItems(element, member).ToList());
    }

    private IEnumerable<XamlCompletionItem> NamespaceUriItems()
    {
        foreach (var uri in _model.Types.XmlnsDefinitionUris.Order(StringComparer.Ordinal))
        {
            yield return new XamlCompletionItem(uri, XamlCompletionKind.Namespace) { Detail = _model.Types.GetPreferredPrefix(uri) };
        }

        foreach (var ns in _model.Types.GetSourceNamespaces().Order(StringComparer.Ordinal))
        {
            yield return new XamlCompletionItem(XmlNamespaces.ClrNamespacePrefix + ns, XamlCompletionKind.Namespace) { Detail = "project" };
        }
    }

    private static IEnumerable<XamlCompletionItem> DirectiveValueItems(XamlDirective directive)
    {
        string[] values = directive.Name switch
        {
            "Shared" => ["True", "False"],
            "FieldModifier" => ["public", "internal", "private", "protected"],
            "ClassModifier" => ["public", "internal"],
            _ => [],
        };

        return values.Select(v => new XamlCompletionItem(v, XamlCompletionKind.Value));
    }

    private IEnumerable<XamlCompletionItem> MemberValueItems(XamlElement scope, XamlMember member)
    {
        if (member.IsEvent)
        {
            return HandlerItems();
        }

        if (XamlTargetResolver.IsTypeValued(_model, member))
        {
            return TypeItems(scope, _ => true, XamlCompletionKind.Type);
        }

        if (member.ValueType is { } valueType && valueType.DerivesFrom(_model.Types.Known.DependencyProperty))
        {
            return TargetTypePropertyItems(scope);
        }

        return ValueItems(member.ValueType);
    }

    private IEnumerable<XamlCompletionItem> HandlerItems()
    {
        if (_model.ClassSymbol is not { } classSymbol)
        {
            return [];
        }

        return classSymbol.GetMembers().OfType<IMethodSymbol>()
            .Where(m => m.MethodKind == MethodKind.Ordinary && m.Parameters.Length == 2 && m.ReturnsVoid)
            .Select(m => new XamlCompletionItem(m.Name, XamlCompletionKind.Method) { Symbol = m, Detail = m.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) });
    }

    /// <summary>Properties of the Style/ControlTemplate TargetType, for <c>Setter.Property</c>, <c>Trigger.Property</c> and <c>TemplateBinding</c>.</summary>
    private IEnumerable<XamlCompletionItem> TargetTypePropertyItems(XamlElement scope)
    {
        if (_model.FindTargetType(scope) is not { } target)
        {
            return [];
        }

        return target.Members
            .Where(m => !m.IsEvent && m.IdentifierField is not null)
            .Select(m => new XamlCompletionItem(m.Name, XamlCompletionKind.Property)
            {
                Symbol = m.Symbol,
                Detail = DocumentationFormatter.ShortTypeName(m.ValueType),
            });
    }

    /// <summary>Values for types with a known set: enums, booleans, and "plural" static holders (Colors, Brushes, Cursors, FontWeights...).</summary>
    private IEnumerable<XamlCompletionItem> ValueItems(ITypeSymbol? type)
    {
        if (type is null)
        {
            yield break;
        }

        type = type.UnwrapNullable();
        if (type.TypeKind == TypeKind.Enum)
        {
            foreach (var field in type.GetMembers().OfType<IFieldSymbol>().Where(f => f.IsStatic && f.HasConstantValue))
            {
                yield return new XamlCompletionItem(field.Name, XamlCompletionKind.EnumValue) { Symbol = field, Detail = type.Name };
            }

            yield break;
        }

        if (type.SpecialType == SpecialType.System_Boolean)
        {
            yield return new XamlCompletionItem("True", XamlCompletionKind.Value);
            yield return new XamlCompletionItem("False", XamlCompletionKind.Value);
            yield break;
        }

        var ns = type.ContainingNamespace?.ToDisplayString();
        if (ns is null)
        {
            yield break;
        }

        var holder = _model.Types.Compilation.GetTypeByMetadataName($"{ns}.{type.Name}s")
            ?? _model.Types.Compilation.GetTypeByMetadataName($"{ns}.{type.Name}es");
        if (holder is null)
        {
            yield break;
        }

        foreach (var property in holder.GetMembers().OfType<IPropertySymbol>())
        {
            if (property.IsStatic && property.DeclaredAccessibility == Accessibility.Public && property.Type.IsAssignableTo(type))
            {
                yield return new XamlCompletionItem(property.Name, XamlCompletionKind.Value) { Symbol = property, Detail = holder.Name };
            }
        }
    }

    // ---------------------------------------------------------------- markup extensions

    private XamlCompletionList? MarkupExtension(XamlElement scope, XamlAttribute attribute, string raw, TextSpan valueSpan, int offset)
    {
        var root = MarkupExtensionParser.Parse(raw, valueSpan.Start).Root;
        var extension = root?.DescendantsAndSelf()
            .Where(e => e.Span.Start < offset && (offset < e.Span.End || !e.IsClosed))
            .MaxBy(e => e.Span.Start);
        if (extension is null)
        {
            return null;
        }

        // {Bin|  or  {|
        if (extension.TypeName.Span.ContainsOrTouches(offset) || (extension.TypeName.IsEmpty && offset == extension.Span.Start + 1))
        {
            var span = extension.TypeName.IsEmpty ? new TextSpan(offset, 0) : extension.TypeName.Span;
            return new XamlCompletionList(span, TypeItems(scope, t => t.IsMarkupExtension && !t.Symbol.IsAbstract, XamlCompletionKind.MarkupExtension).ToList());
        }

        var type = _model.ResolveMarkupExtensionType(scope, extension.TypeName, out _);
        if (type is null)
        {
            return null;
        }

        foreach (var argument in extension.Arguments)
        {
            if (argument.NameSpan is { } nameSpan && nameSpan.ContainsOrTouches(offset))
            {
                return new XamlCompletionList(nameSpan, ArgumentNameItems(type, extension, argument).ToList());
            }

            if (argument.Value.Extension is null && argument.Value.Span.ContainsOrTouches(offset))
            {
                if (type.Symbol.DerivesFrom(_model.Types.Known.Binding))
                {
                    if (argument.Name == "Path" || (argument.IsPositional && argument == extension.PositionalArguments.First()))
                    {
                        return BindingPathCompletion(scope, attribute, extension, argument, type, offset);
                    }

                    if (argument.Name == "ElementName")
                    {
                        return new XamlCompletionList(argument.Value.Span, NamedElementItems().ToList());
                    }
                }

                var items = ArgumentValueItems(scope, type, argument).ToList();
                if (argument.IsPositional)
                {
                    // A positional value may really be the start of a named argument: {Binding Path=A, Mo|
                    items.AddRange(ArgumentNameItems(type, extension, argument));
                }

                return new XamlCompletionList(argument.Value.Span, items);
            }
        }

        // Between arguments: {Binding |} or {Binding Path=A, |}
        var empty = new TextSpan(offset, 0);
        var all = ArgumentNameItems(type, extension, null).ToList();
        if (!extension.Arguments.Any())
        {
            all.InsertRange(0, PositionalValueItems(scope, type, 0, 1));
            if (type.Symbol.DerivesFrom(_model.Types.Known.Binding) && _model.Bindings.GetBinding(extension, scope, attribute)?.Source.Type is { } source)
            {
                // {Binding |}: the first positional argument is the path.
                all.InsertRange(0, PathPropertyItems(source));
            }
        }

        return new XamlCompletionList(empty, all);
    }

    /// <summary>
    /// <c>{Binding Customer.Na|}</c>: properties of the type the path so far resolves to, replacing only the
    /// segment being typed.
    /// </summary>
    private XamlCompletionList? BindingPathCompletion(
        XamlElement scope, XamlAttribute attribute, MarkupExtensionSyntax extension, MarkupArgumentSyntax argument, XamlTypeInfo type, int offset)
    {
        if (_model.Bindings.GetBinding(extension, scope, attribute) is not { } binding)
        {
            return null;
        }

        var path = argument.Value.Text;
        var start = binding.PathOffset;
        var caret = Math.Clamp(offset - start, 0, path.Length);
        var separator = path.LastIndexOfAny(['.', '/'], Math.Max(0, caret - 1));
        if (caret == 0)
        {
            separator = -1;
        }

        var owner = separator < 0
            ? binding.Source.Type
            : _model.Bindings.ResolvePath(binding.Source, path[..(separator + 1)], start, scope).ResultType;

        var segmentStart = separator + 1;
        var segmentEnd = segmentStart;
        while (segmentEnd < path.Length && (char.IsLetterOrDigit(path[segmentEnd]) || path[segmentEnd] == '_'))
        {
            segmentEnd++;
        }

        var items = owner is null || _model.Bindings.IsOpen(owner) ? [] : PathPropertyItems(owner).ToList();
        if (argument.IsPositional && separator < 0)
        {
            // A positional value may really be the start of a named argument: {Binding Mo|
            items.AddRange(ArgumentNameItems(type, extension, argument));
        }

        return new XamlCompletionList(TextSpan.FromBounds(start + segmentStart, start + Math.Max(segmentEnd, caret)), items);
    }

    private static IEnumerable<XamlCompletionItem> PathPropertyItems(Microsoft.CodeAnalysis.ITypeSymbol owner) =>
        Bindings.BindingResolver.GetBindableProperties(owner).Select(p => new XamlCompletionItem(p.Name, XamlCompletionKind.Property)
        {
            Detail = DocumentationFormatter.ShortTypeName(p.Type),
            Symbol = p,
            SortText = "0" + p.Name,
        });

    /// <summary>Names declared with x:Name or Name, for ElementName.</summary>
    private IEnumerable<XamlCompletionItem> NamedElementItems() =>
        _document.AllElements()
            .SelectMany(e => e.Attributes
                .Where(a => a.Name.LocalName == "Name" && a.Value is { Length: > 0 }
                    && (a.Name.Prefix is null || e.LookupNamespace(a.Name.Prefix) == XmlNamespaces.Xaml2006))
                .Select(a => (Name: a.Value!, Element: e)))
            .DistinctBy(n => n.Name)
            .Select(n => new XamlCompletionItem(n.Name, XamlCompletionKind.Value) { Detail = n.Element.Name.FullName, SortText = "0" + n.Name });

    private static IEnumerable<XamlCompletionItem> ArgumentNameItems(XamlTypeInfo type, MarkupExtensionSyntax extension, MarkupArgumentSyntax? current)
    {
        var used = extension.Arguments.Where(a => a != current && a.Name is not null).Select(a => a.Name!).ToHashSet(StringComparer.Ordinal);
        foreach (var member in type.Members)
        {
            if (member.IsEvent || member.IsReadOnly || used.Contains(member.Name))
            {
                continue;
            }

            yield return new XamlCompletionItem(member.Name, XamlCompletionKind.Property)
            {
                InsertText = current?.Name is null ? member.Name + "=" : member.Name,
                Detail = DocumentationFormatter.ShortTypeName(member.ValueType),
                Symbol = member.Symbol,
                SortText = "1" + member.Name,
            };
        }
    }

    private IEnumerable<XamlCompletionItem> ArgumentValueItems(XamlElement scope, XamlTypeInfo type, MarkupArgumentSyntax argument)
    {
        if (XamlTargetResolver.IsResourceExtension(_model, type) && (argument.IsPositional || argument.Name == "ResourceKey"))
        {
            return ResourceKeyItems(scope);
        }

        if (argument.IsPositional)
        {
            var positional = argument.Owner.PositionalArguments.ToList();
            return PositionalValueItems(scope, type, positional.IndexOf(argument), positional.Count);
        }

        if (type.GetMember(argument.Name!) is { } member)
        {
            return MemberValueItems(scope, member);
        }

        return [];
    }

    private IEnumerable<XamlCompletionItem> PositionalValueItems(XamlElement scope, XamlTypeInfo type, int index, int count)
    {
        if (XamlTargetResolver.IsResourceExtension(_model, type))
        {
            return ResourceKeyItems(scope);
        }

        var constructor = type.Symbol.InstanceConstructors
            .Where(c => c.DeclaredAccessibility == Accessibility.Public)
            .FirstOrDefault(c => c.Parameters.Length == count);
        if (constructor is null || index < 0 || index >= constructor.Parameters.Length)
        {
            return [];
        }

        var parameterType = constructor.Parameters[index].Type;
        if (SymbolEqualityComparer.Default.Equals(parameterType, _model.Types.Known.Type))
        {
            return TypeItems(scope, _ => true, XamlCompletionKind.Type).Select(i => i with { SortText = "0" + i.Label });
        }

        if (parameterType.DerivesFrom(_model.Types.Known.DependencyProperty))
        {
            return TargetTypePropertyItems(scope).Select(i => i with { SortText = "0" + i.Label });
        }

        return ValueItems(parameterType).Select(i => i with { SortText = "0" + i.Label });
    }

    private IEnumerable<XamlCompletionItem> ResourceKeyItems(XamlElement scope) =>
        _model.GetResourceScope(scope).AllDefinitions()
            .Select(d => new XamlCompletionItem(d.Key, XamlCompletionKind.ResourceKey)
            {
                Detail = d.FilePath is null ? d.ElementName : $"{d.ElementName} ({Path.GetFileName(d.FilePath)})",
                SortText = "0" + d.Key,
            });
}
