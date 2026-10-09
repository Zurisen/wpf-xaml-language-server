using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using WpfXaml.Analysis.Resources;
using WpfXaml.Analysis.TypeSystem;
using WpfXaml.Language;
using WpfXaml.Language.Syntax;
using WpfXaml.Language.Text;

namespace WpfXaml.Analysis.Semantics;

/// <summary>
/// Binds a parsed XAML document to a <see cref="XamlTypeSystem"/>. Results are computed lazily and cached;
/// the model is immutable from the outside and safe to share between requests for the same document version.
/// </summary>
public sealed class XamlSemanticModel
{
    private readonly ConcurrentDictionary<XamlElement, ElementInfo> _elements = new();
    private readonly ConcurrentDictionary<XamlAttribute, AttributeInfo> _attributes = new();
    private readonly ConcurrentDictionary<XamlAttribute, MarkupExtensionParseResult> _markupExtensions = new();
    private readonly ConcurrentDictionary<XamlElement, ResourceScope> _resourceScopes = new();
    private readonly Lazy<INamedTypeSymbol?> _classSymbol;
    private readonly Lazy<ResourceScope> _applicationScope;
    private readonly ResourceCollector _resourceCollector;

    /// <param name="resources">Access to other XAML files for resource lookup; without it only this document's resources are known.</param>
    public XamlSemanticModel(XamlDocument document, XamlTypeSystem types, XamlResourceContext? resources = null)
    {
        Document = document;
        Types = types;
        Resources = resources;
        _classSymbol = new Lazy<INamedTypeSymbol?>(FindClassSymbol);
        _resourceCollector = new ResourceCollector(resources, document);
        _applicationScope = new Lazy<ResourceScope>(() => new ResourceScope(_resourceCollector.FromApplications(), null));
    }

    public XamlDocument Document { get; }

    public XamlTypeSystem Types { get; }

    public XamlResourceContext? Resources { get; }

    /// <summary>
    /// The resources visible at <paramref name="element"/>: its own and its ancestors' <c>Resources</c>
    /// (dictionaries, merged dictionaries, <c>Source</c> files), then the application's.
    /// </summary>
    public ResourceScope GetResourceScope(XamlElement element) =>
        _resourceScopes.TryGetValue(element, out var cached) ? cached : _resourceScopes.GetOrAdd(element, CreateResourceScope);

    /// <summary>Where a ResourceDictionary <c>Source</c> in this document points; null without a resource host.</summary>
    public ResourceSourceResolution? ResolveResourceSource(string source) => _resourceCollector.Resolve(source, Resources?.FilePath);

    private ResourceScope CreateResourceScope(XamlElement element)
    {
        var parent = element.Parent is { } p ? GetResourceScope(p) : _applicationScope.Value;
        if (element.IsPropertyElement)
        {
            return parent;
        }

        var local = new ResourceTable();
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Resources?.FilePath is { } path)
        {
            visiting.Add(path);
        }

        if (ResourceCollector.IsDictionaryElement(element))
        {
            local.Merge(_resourceCollector.FromDictionary(element, Document, Resources?.FilePath, visiting));
        }

        foreach (var child in element.ChildElements)
        {
            if (child.IsPropertyElement && child.Name.MemberName == "Resources")
            {
                local.Merge(_resourceCollector.FromResourcesProperty(child, Document, Resources?.FilePath, visiting));
            }
        }

        return local.IsEmpty ? parent : new ResourceScope(local, parent);
    }

    /// <summary>The code-behind class named by <c>x:Class</c> on the root, if it exists in the compilation.</summary>
    public INamedTypeSymbol? ClassSymbol => _classSymbol.Value;

    public ElementInfo GetElementInfo(XamlElement element) => _elements.GetOrAdd(element, ResolveElement);

    public AttributeInfo GetAttributeInfo(XamlAttribute attribute) => _attributes.GetOrAdd(attribute, ResolveAttribute);

    /// <summary>The parsed markup extension of an attribute value, or null when the value is a literal.</summary>
    public MarkupExtensionParseResult? GetMarkupExtension(XamlAttribute attribute)
    {
        if (!MarkupExtensionParser.IsMarkupExtension(attribute.RawValue) || attribute.ValueSpan is not { } span)
        {
            return null;
        }

        return _markupExtensions.GetOrAdd(attribute, a => MarkupExtensionParser.Parse(a.RawValue!, span.Start));
    }

    /// <summary>The nearest ancestor that creates an object (skipping property elements).</summary>
    public XamlElement? GetParentObjectElement(XamlElement element)
    {
        foreach (var ancestor in element.Ancestors())
        {
            if (!ancestor.IsPropertyElement)
            {
                return ancestor;
            }
        }

        return null;
    }

    public bool IsXamlLanguageNamespace(XamlElement scope, string? prefix) =>
        scope.LookupNamespace(prefix) == XmlNamespaces.Xaml2006;

    /// <summary>Prefix bound to the XAML language namespace in scope (usually <c>x</c>).</summary>
    public string? GetXamlLanguagePrefix(XamlElement scope) =>
        scope.GetNamespacesInScope().FirstOrDefault(n => n.Namespace == XmlNamespaces.Xaml2006).Prefix;

    /// <summary>True for <c>mc:</c> itself and any namespace listed in an <c>mc:Ignorable</c> in scope.</summary>
    public bool IsIgnorableNamespace(XamlElement scope, string uri)
    {
        if (uri == XmlNamespaces.MarkupCompatibility)
        {
            return true;
        }

        for (XamlElement? e = scope; e is not null; e = e.Parent)
        {
            foreach (var a in e.Attributes)
            {
                if (a.Name.LocalName == "Ignorable" && a.Name.Prefix is { } p && e.LookupNamespace(p) == XmlNamespaces.MarkupCompatibility
                    && a.Value is { } value)
                {
                    foreach (var prefix in value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (e.LookupNamespace(prefix) == uri)
                        {
                            return true;
                        }
                    }
                }
            }
        }

        return false;
    }

    /// <summary>Resolves a namespace prefix at <paramref name="scope"/>.</summary>
    public NamespaceLookup LookupNamespace(XamlElement scope, string? prefix, TextSpan errorSpan)
    {
        var uri = scope.LookupNamespace(prefix);
        if (uri is null)
        {
            var message = prefix is null ? "No default XAML namespace is declared." : $"The namespace prefix '{prefix}' is not declared.";
            return new NamespaceLookup(null, null, false, true, Error(DiagnosticCodes.UnknownNamespacePrefix, message, errorSpan));
        }

        if (uri == XmlNamespaces.Xml)
        {
            return new NamespaceLookup(uri, null, false, true, null);
        }

        if (IsIgnorableNamespace(scope, uri))
        {
            return new NamespaceLookup(uri, null, true, false, null);
        }

        var resolution = Types.ResolveNamespace(uri);
        if (resolution.Namespace is null)
        {
            // Without a project we cannot know third-party or project namespaces: stay silent.
            var error = Types.IsFrameworkOnly ? null : Error(DiagnosticCodes.UnresolvedAssembly, resolution.Error!, errorSpan);
            return new NamespaceLookup(uri, null, false, false, error);
        }

        return new NamespaceLookup(uri, resolution.Namespace, false, IsAuthoritative(resolution.Namespace), null);
    }

    /// <summary>Resolves the type of a markup extension name such as <c>Binding</c> or <c>x:Static</c>.</summary>
    public XamlTypeInfo? ResolveMarkupExtensionType(XamlElement scope, XamlName name, out XamlDiagnostic? error)
    {
        error = null;
        if (name.IsEmpty)
        {
            return null;
        }

        var lookup = LookupNamespace(scope, name.Prefix, name.Span);
        if (lookup.Error is not null || lookup.Namespace is null)
        {
            error = lookup.Error;
            return null;
        }

        var type = lookup.Namespace.FindMarkupExtension(name.LocalName);
        if (type is null && lookup.IsAuthoritative)
        {
            var suggestion = NameSuggestions.Closest(name.LocalName, lookup.Namespace.MarkupExtensions.Select(t => t.XamlName));
            error = Error(DiagnosticCodes.UnknownMarkupExtension,
                $"The markup extension '{name.FullName}' was not found.{NameSuggestions.DidYouMean(suggestion)}", name.Span);
        }

        return type;
    }

    /// <summary>Resolves a type written as <c>prefix:Name</c> (as in <c>TargetType="local:Foo"</c>) or <c>{x:Type prefix:Name}</c>.</summary>
    public XamlTypeInfo? ResolveTypeReference(XamlElement scope, string text)
    {
        text = text.Trim();
        if (MarkupExtensionParser.IsMarkupExtension(text))
        {
            var parsed = MarkupExtensionParser.Parse(text, 0).Root;
            if (parsed?.PositionalArguments.FirstOrDefault() is { } positional)
            {
                text = positional.Value.Text;
            }
            else if (parsed?.FindArgument("TypeName") is { } named)
            {
                text = named.Value.Text;
            }
            else
            {
                return null;
            }
        }

        var colon = text.IndexOf(':');
        var prefix = colon < 0 ? null : text[..colon];
        var name = colon < 0 ? text : text[(colon + 1)..];
        var uri = scope.LookupNamespace(prefix);
        return uri is null ? null : Types.GetNamespace(uri)?.FindType(name);
    }

    /// <summary>
    /// The <c>TargetType</c> in effect for an element inside a Style or ControlTemplate
    /// (used for <c>Setter.Property</c> and <c>TemplateBinding</c>).
    /// </summary>
    public XamlTypeInfo? FindTargetType(XamlElement element)
    {
        for (XamlElement? e = element; e is not null; e = e.Parent)
        {
            if (!e.IsPropertyElement && GetElementInfo(e).Type?.GetMember("TargetType") is not null
                && e.FindAttribute(null, "TargetType") is { Value: { } targetType })
            {
                return ResolveTypeReference(e, targetType);
            }
        }

        return null;
    }

    public IReadOnlyList<XamlDiagnostic> GetDiagnostics(CancellationToken cancellationToken = default) =>
        new XamlDiagnosticAnalyzer(this).Analyze(cancellationToken);

    internal static XamlDiagnostic Error(string code, string message, TextSpan span) =>
        new(code, message, span, XamlDiagnosticSeverity.Error);

    private bool IsAuthoritative(XamlNamespace ns) =>
        !Types.IsFrameworkOnly || ns.Mappings.All(m => !SymbolEqualityComparer.Default.Equals(m.Assembly, Types.Compilation.Assembly));

    private INamedTypeSymbol? FindClassSymbol()
    {
        if (Document.Root is not { } root)
        {
            return null;
        }

        var attribute = root.Attributes.FirstOrDefault(a => a.Name.LocalName == "Class" && a.Name.Prefix is not null && IsXamlLanguageNamespace(root, a.Name.Prefix));
        return attribute?.Value is { Length: > 0 } name ? Types.Compilation.GetTypeByMetadataName(name.Trim()) : null;
    }

    private ElementInfo ResolveElement(XamlElement element)
    {
        var name = element.Name;
        if (name.IsEmpty)
        {
            return new ElementInfo(ElementKind.Unknown);
        }

        if (element.Parent is { } parent && GetElementInfo(parent).Kind == ElementKind.Ignored)
        {
            return new ElementInfo(ElementKind.Ignored);
        }

        var prefixSpan = name.Prefix is null ? name.Span : new TextSpan(name.Span.Start, name.Prefix.Length);
        var lookup = LookupNamespace(element, name.Prefix, prefixSpan);
        if (lookup.Error is not null)
        {
            return new ElementInfo(ElementKind.Unknown, Error: lookup.Error);
        }

        if (lookup.IsIgnored)
        {
            return new ElementInfo(ElementKind.Ignored);
        }

        if (lookup.Uri == XmlNamespaces.Xaml2006 && !name.IsDotted && XamlDirectives.Find(name.LocalName) is { Usage: DirectiveUsage.Element } directive)
        {
            return new ElementInfo(ElementKind.Directive, Directive: directive);
        }

        if (lookup.Namespace is not { } ns)
        {
            return new ElementInfo(ElementKind.Unknown);
        }

        if (name.IsDotted)
        {
            var owner = ns.FindType(name.OwnerName!);
            if (owner is null)
            {
                return new ElementInfo(ElementKind.Unknown, Error: lookup.IsAuthoritative ? UnknownTypeError(ns, name.OwnerName!, name.OwnerSpan) : null);
            }

            var parentObject = GetParentObjectElement(element) is { } p ? GetElementInfo(p).Type : null;
            var member = ResolveDottedMember(owner, name.MemberName, parentObject);
            if (member is null)
            {
                return new ElementInfo(ElementKind.PropertyElement, OwnerType: owner, Error: UnknownMemberError(owner, name.MemberName, name.MemberNameSpan));
            }

            XamlDiagnostic? warning = null;
            if (!member.IsAttached && parentObject is not null && !parentObject.Symbol.DerivesFrom(owner.Symbol))
            {
                warning = new XamlDiagnostic(DiagnosticCodes.PropertyElementOwnerMismatch,
                    $"'{owner.Name}' is not the type of the parent element '{parentObject.Name}' or one of its base types.",
                    name.OwnerSpan, XamlDiagnosticSeverity.Warning);
            }

            return new ElementInfo(ElementKind.PropertyElement, Member: member, OwnerType: owner, Error: warning);
        }

        var type = ns.FindElementType(name.LocalName);
        if (type is null)
        {
            return new ElementInfo(ElementKind.Unknown, Error: lookup.IsAuthoritative ? UnknownTypeError(ns, name.LocalName, name.Span) : null);
        }

        return new ElementInfo(ElementKind.Object, Type: type);
    }

    private AttributeInfo ResolveAttribute(XamlAttribute attribute)
    {
        var element = attribute.Element;
        var name = attribute.Name;

        if (attribute.IsNamespaceDeclaration)
        {
            XamlDiagnostic? error = null;
            if (!Types.IsFrameworkOnly && attribute.Value is { } uri && uri.StartsWith(XmlNamespaces.ClrNamespacePrefix, StringComparison.Ordinal)
                && Types.ResolveNamespace(uri).Error is { } message && attribute.ValueSpan is { } valueSpan)
            {
                error = Error(DiagnosticCodes.UnresolvedAssembly, message, valueSpan);
            }

            return new AttributeInfo(AttributeKind.NamespaceDeclaration, Error: error);
        }

        var elementInfo = GetElementInfo(element);
        if (elementInfo.Kind == ElementKind.Ignored)
        {
            return new AttributeInfo(AttributeKind.Ignored);
        }

        if (name.IsEmpty)
        {
            return new AttributeInfo(AttributeKind.Unknown);
        }

        if (name.Prefix is not null)
        {
            var lookup = LookupNamespace(element, name.Prefix, new TextSpan(name.Span.Start, name.Prefix.Length));
            if (lookup.Error is not null)
            {
                return new AttributeInfo(AttributeKind.Unknown, Error: lookup.Error);
            }

            if (lookup.Uri == XmlNamespaces.Xml)
            {
                return new AttributeInfo(AttributeKind.XmlAttribute);
            }

            if (lookup.IsIgnored)
            {
                return new AttributeInfo(AttributeKind.Ignored);
            }

            if (lookup.Uri == XmlNamespaces.Xaml2006)
            {
                return ResolveDirective(attribute);
            }

            if (lookup.Namespace is not { } ns)
            {
                return new AttributeInfo(AttributeKind.Unknown);
            }

            if (!name.IsDotted)
            {
                // A prefixed, non-attached attribute is only meaningful in the element's own namespace.
                if (elementInfo.Type is { } elementType && element.LookupNamespace(element.Name.Prefix) == lookup.Uri)
                {
                    return ResolveMember(elementType, name.LocalName, name.LocalNameSpan);
                }

                return new AttributeInfo(AttributeKind.Unknown,
                    Error: Error(DiagnosticCodes.UnknownMember, $"'{name.FullName}' is not an attached property or event (expected 'Owner.Member').", name.Span));
            }

            return ResolveDottedAttribute(ns, lookup.IsAuthoritative, name, elementInfo.Type);
        }

        if (name.IsDotted)
        {
            var lookup = LookupNamespace(element, null, name.OwnerSpan);
            if (lookup.Error is not null || lookup.Namespace is null)
            {
                return new AttributeInfo(AttributeKind.Unknown, Error: lookup.Error);
            }

            return ResolveDottedAttribute(lookup.Namespace, lookup.IsAuthoritative, name, elementInfo.Type);
        }

        if (elementInfo.Kind == ElementKind.PropertyElement)
        {
            return new AttributeInfo(AttributeKind.Unknown,
                Error: Error(DiagnosticCodes.UnknownMember, "Property elements cannot have attributes.", name.Span));
        }

        if (elementInfo.Type is not { } type)
        {
            return new AttributeInfo(AttributeKind.Unknown);
        }

        return ResolveMember(type, name.LocalName, name.Span);
    }

    private AttributeInfo ResolveMember(XamlTypeInfo type, string memberName, TextSpan span)
    {
        // WPF also accepts an attachable member declared on the element's own type without the owner prefix:
        // <VirtualizingStackPanel IsVirtualizing="True" />, <Grid ZIndex="1" />.
        var member = type.GetMember(memberName) ?? type.GetAttachableMember(memberName);
        return member is null
            ? new AttributeInfo(AttributeKind.Unknown, Error: UnknownMemberError(type, memberName, span))
            : new AttributeInfo(AttributeKind.Member, member);
    }

    private AttributeInfo ResolveDottedAttribute(XamlNamespace ns, bool authoritative, XamlName name, XamlTypeInfo? elementType)
    {
        var owner = ns.FindType(name.OwnerName!);
        if (owner is null)
        {
            return new AttributeInfo(AttributeKind.Unknown, Error: authoritative ? UnknownTypeError(ns, name.OwnerName!, name.OwnerSpan) : null);
        }

        var member = ResolveDottedMember(owner, name.MemberName, elementType);
        return member is null
            ? new AttributeInfo(AttributeKind.Unknown, Error: UnknownMemberError(owner, name.MemberName, name.MemberNameSpan, attached: true))
            : new AttributeInfo(AttributeKind.Member, member);
    }

    /// <summary><c>Owner.Member</c>: an attachable member of Owner, or a regular member when the target derives from Owner.</summary>
    private static XamlMember? ResolveDottedMember(XamlTypeInfo owner, string memberName, XamlTypeInfo? target)
    {
        var attached = owner.GetAttachableMember(memberName);
        if (attached is not null)
        {
            return attached;
        }

        return target is null || target.Symbol.DerivesFrom(owner.Symbol) ? owner.GetMember(memberName) : null;
    }

    private AttributeInfo ResolveDirective(XamlAttribute attribute)
    {
        var directive = XamlDirectives.Find(attribute.Name.LocalName);
        if (directive is null || directive.Usage == DirectiveUsage.Element)
        {
            var suggestion = NameSuggestions.Closest(attribute.Name.LocalName,
                XamlDirectives.All.Where(d => d.Usage != DirectiveUsage.Element).Select(d => d.Name));
            var qualified = suggestion is null ? null : $"{attribute.Name.Prefix}:{suggestion}";
            return new AttributeInfo(AttributeKind.Unknown,
                Error: Error(DiagnosticCodes.UnknownDirective, $"'{attribute.Name.FullName}' is not a XAML directive.{NameSuggestions.DidYouMean(qualified)}", attribute.Name.Span));
        }

        if (directive.Usage == DirectiveUsage.Root && attribute.Element.Parent is not null)
        {
            return new AttributeInfo(AttributeKind.Directive, Directive: directive,
                Error: Error(DiagnosticCodes.UnknownDirective, $"'{attribute.Name.FullName}' is only allowed on the root element.", attribute.Name.Span));
        }

        return new AttributeInfo(AttributeKind.Directive, Directive: directive);
    }

    private static XamlDiagnostic UnknownTypeError(XamlNamespace ns, string typeName, TextSpan span)
    {
        var suggestion = NameSuggestions.Closest(typeName, ns.Types.Select(t => t.XamlName));
        return Error(DiagnosticCodes.UnknownType, $"The type '{typeName}' was not found in '{ns.Uri}'.{NameSuggestions.DidYouMean(suggestion)}", span);
    }

    private static XamlDiagnostic UnknownMemberError(XamlTypeInfo type, string memberName, TextSpan span, bool attached = false)
    {
        var candidates = attached ? type.AttachableMembers.Concat(type.Members) : type.Members;
        var suggestion = NameSuggestions.Closest(memberName, candidates.Select(m => m.Name));
        var what = attached ? "an attachable property or event" : "a property or event";
        return Error(DiagnosticCodes.UnknownMember, $"'{memberName}' is not {what} of '{type.Name}'.{NameSuggestions.DidYouMean(suggestion)}", span);
    }
}

/// <param name="Uri">The resolved URI, or null when the prefix is undeclared.</param>
/// <param name="Namespace">The type-system namespace, when it resolved.</param>
/// <param name="IsIgnored">The namespace is ignorable (mc:Ignorable): skip all checks.</param>
/// <param name="IsAuthoritative">Failed lookups in this namespace are real errors, not gaps in what we loaded.</param>
public sealed record NamespaceLookup(string? Uri, XamlNamespace? Namespace, bool IsIgnored, bool IsAuthoritative, XamlDiagnostic? Error);
