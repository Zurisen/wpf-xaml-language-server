using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using WpfXaml.Analysis.Semantics;
using WpfXaml.Analysis.TypeSystem;
using WpfXaml.Language.Syntax;
using WpfXaml.Language.Text;

namespace WpfXaml.Analysis.Bindings;

/// <summary>How much a binding source type can be trusted.</summary>
public enum BindingSourceKind
{
    /// <summary>The run-time object has exactly this type (or the type is the declared contract, e.g. d:DataContext).</summary>
    Exact,

    /// <summary>The run-time object is this type or a subclass (AncestorType, declared property types...).</summary>
    LowerBound,

    /// <summary>Informational only, e.g. the DataType of a keyed DataTemplate, which WPF doesn't enforce.</summary>
    Hint,
}

/// <summary>The type a binding resolves its path against.</summary>
/// <param name="Type">Null when it can't be known statically.</param>
/// <param name="Origin">Where it came from, for hover and messages (e.g. <c>d:DataContext</c>).</param>
public sealed record BindingSource(ITypeSymbol? Type, string Origin, BindingSourceKind Kind = BindingSourceKind.Exact)
{
    public static BindingSource Unknown(string why) => new(null, why);
}

/// <param name="Owner">The type the segment was looked up on.</param>
/// <param name="Symbol">The property (or attached member accessor) found.</param>
/// <param name="Type">The type after this segment; null when unknown.</param>
public sealed record ResolvedPathSegment(BindingPathSegment Segment, ITypeSymbol Owner, ISymbol? Symbol, ITypeSymbol? Type);

/// <param name="Segment">The property segment that wasn't found.</param>
/// <param name="Owner">The type it was looked up on.</param>
/// <param name="IsCertain">
/// True when the run-time object can't have the property either: the owner type is exact, or a lower bound
/// that can't be subclassed (sealed class or struct). Only certain misses are reported.
/// </param>
public sealed record MissingPathSegment(BindingPathSegment Segment, ITypeSymbol Owner, bool IsCertain);

/// <param name="Segments">Segments resolved before resolution stopped.</param>
/// <param name="Missing">The first property segment that doesn't exist on its owner.</param>
/// <param name="IsComplete">False when resolution stopped early because a type was unknown or open (object, dynamic, ...).</param>
public sealed record BindingPathResolution(
    BindingSource Source,
    IReadOnlyList<ResolvedPathSegment> Segments,
    MissingPathSegment? Missing,
    bool IsComplete)
{
    /// <summary>The type the whole path produces, when every step is known.</summary>
    public ITypeSymbol? ResultType => !IsComplete || Missing is not null ? null : Segments.Count == 0 ? Source.Type : Segments[^1].Type;
}

/// <summary>A <c>{Binding}</c> in a document: its path argument and resolved source.</summary>
public sealed record BindingInfo(MarkupExtensionSyntax Syntax, MarkupArgumentSyntax? PathArgument, string Path, int PathOffset, BindingSource Source);

/// <summary>
/// Static binding analysis. Infers data context types only from what the XAML declares and stops (unknown) at
/// anything decided at run time, so that what it does report is reliable.
/// </summary>
public sealed class BindingResolver
{
    private readonly XamlSemanticModel _model;
    private readonly ConcurrentDictionary<XamlElement, BindingSource> _dataContexts = new();

    internal BindingResolver(XamlSemanticModel model)
    {
        _model = model;
    }

    private WellKnownTypes Known => _model.Types.Known;

    /// <summary>The data context type in effect for an element.</summary>
    public BindingSource GetDataContext(XamlElement element) =>
        _dataContexts.TryGetValue(element, out var cached) ? cached : _dataContexts.GetOrAdd(element, ComputeDataContext);

    /// <summary>Recognizes <c>{Binding ...}</c> (and subclasses) and resolves its source.</summary>
    public BindingInfo? GetBinding(MarkupExtensionSyntax extension, XamlElement element, XamlAttribute? attribute)
    {
        var type = _model.ResolveMarkupExtensionType(element, extension.TypeName, out _);
        if (type is null || !type.Symbol.DerivesFrom(Known.Binding))
        {
            return null;
        }

        var pathArgument = extension.FindArgument("Path") ?? extension.PositionalArguments.FirstOrDefault();
        var path = pathArgument?.Value.Extension is null ? pathArgument?.Value.Text ?? string.Empty : string.Empty;
        var offset = pathArgument is null ? extension.Span.End : ValueTextOffset(pathArgument.Value);

        // DataContext="{Binding X}" is evaluated against the inherited data context, not the one it sets.
        var isDataContext = attribute is { Name.Prefix: null, Name.LocalName: "DataContext" };
        return new BindingInfo(extension, pathArgument, path, offset, ResolveSource(extension, element, isDataContext));
    }

    /// <summary>Resolves a property path against a source, segment by segment.</summary>
    public BindingPathResolution ResolvePath(BindingSource source, string path, int baseOffset, XamlElement scope)
    {
        var segments = BindingPath.Parse(path, baseOffset);
        var resolved = new List<ResolvedPathSegment>();
        if (segments is null)
        {
            return new BindingPathResolution(source, resolved, null, IsComplete: false);
        }

        var current = source.Type;
        foreach (var segment in segments)
        {
            if (current is null || IsOpen(current))
            {
                return new BindingPathResolution(source, resolved, null, IsComplete: false);
            }

            switch (segment.Kind)
            {
                case BindingPathSegmentKind.Property:
                    var property = FindProperty(current, segment.Text);
                    if (property is null)
                    {
                        if (FindDependencyPropertyField(current, segment.Text) is { } field)
                        {
                            // A registered dependency property without a CLR wrapper: WPF resolves it by name.
                            resolved.Add(new ResolvedPathSegment(segment, current, field, null));
                            return new BindingPathResolution(source, resolved, null, IsComplete: false);
                        }

                        // The first segment is looked up on the source; later ones on declared property types,
                        // which the run-time value may subclass.
                        var kind = resolved.Count == 0 ? source.Kind : BindingSourceKind.LowerBound;
                        var certain = kind == BindingSourceKind.Exact
                            || (kind == BindingSourceKind.LowerBound && !MaySubclassHaveProperty(current, segment.Text));
                        return new BindingPathResolution(source, resolved, new MissingPathSegment(segment, current, certain), IsComplete: true);
                    }

                    resolved.Add(new ResolvedPathSegment(segment, current, property, property.Type));
                    current = property.Type;
                    break;

                case BindingPathSegmentKind.Indexer:
                    var indexed = IndexerType(current);
                    resolved.Add(new ResolvedPathSegment(segment, current, null, indexed));
                    current = indexed;
                    break;

                case BindingPathSegmentKind.Attached:
                    var member = ResolveAttached(segment.Text, scope);
                    if (member is null)
                    {
                        // Could be a cast or an owner we can't see: unknown, not an error.
                        return new BindingPathResolution(source, resolved, null, IsComplete: false);
                    }

                    resolved.Add(new ResolvedPathSegment(segment, current, member.Symbol, member.ValueType));
                    current = member.ValueType;
                    break;

                case BindingPathSegmentKind.CurrentItem:
                    var item = ItemType(current);
                    resolved.Add(new ResolvedPathSegment(segment, current, null, item));
                    current = item;
                    break;
            }
        }

        return new BindingPathResolution(source, resolved, null, IsComplete: current is not null || segments.Count == 0);
    }

    /// <summary>
    /// Public properties a binding path can name, including inherited and interface ones. Static properties count:
    /// WPF's property path lookup finds them through an instance too.
    /// </summary>
    public static IEnumerable<IPropertySymbol> GetBindableProperties(ITypeSymbol type)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        IEnumerable<ITypeSymbol> types = type.TypeKind == TypeKind.Interface
            ? [type, .. type.AllInterfaces]
            : type is INamedTypeSymbol named ? named.BaseTypesAndSelf() : [type];
        foreach (var t in types)
        {
            foreach (var property in t.GetMembers().OfType<IPropertySymbol>())
            {
                if (!property.IsIndexer && property.DeclaredAccessibility == Accessibility.Public
                    && property.ExplicitInterfaceImplementations.IsEmpty && seen.Add(property.Name))
                {
                    yield return property;
                }
            }
        }
    }

    /// <summary>True for types whose members can't be known statically (object, dynamic, custom type descriptors...).</summary>
    public bool IsOpen(ITypeSymbol type) =>
        type.SpecialType == SpecialType.System_Object
        || type.TypeKind is TypeKind.Dynamic or TypeKind.Error or TypeKind.TypeParameter
        || type.IsAssignableTo(Known.ICustomTypeDescriptor)
        || type.IsAssignableTo(Known.IDynamicMetaObjectProvider);

    private readonly ConcurrentDictionary<ITypeSymbol, HashSet<string>> _subclassProperties = new(SymbolEqualityComparer.Default);

    /// <summary>
    /// Whether a run-time object typed as (a subclass of) <paramref name="type"/> could have the property.
    /// Sealed types and structs can't. For application and library types, the known subclasses (in the project
    /// and in the type's own assembly) are checked. Framework types and interfaces could be anything.
    /// </summary>
    private bool MaySubclassHaveProperty(ITypeSymbol type, string name)
    {
        if (type.IsSealed || type.IsValueType)
        {
            return false;
        }

        if (IsFrameworkType(type))
        {
            return true;
        }

        return _subclassProperties.GetOrAdd(type, CollectSubclassProperties).Contains(name);
    }

    private HashSet<string> CollectSubclassProperties(ITypeSymbol type)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        IEnumerable<IAssemblySymbol> assemblies = [_model.Types.Compilation.Assembly, type.ContainingAssembly];
        foreach (var assembly in assemblies.Where(a => a is not null).Distinct<IAssemblySymbol>(SymbolEqualityComparer.Default))
        {
            foreach (var candidate in AllTypes(assembly.GlobalNamespace))
            {
                if (!SymbolEqualityComparer.Default.Equals(candidate, type) && candidate.IsAssignableTo(type))
                {
                    foreach (var property in GetBindableProperties(candidate))
                    {
                        names.Add(property.Name);
                    }
                }
            }
        }

        return names;
    }

    private static IEnumerable<INamedTypeSymbol> AllTypes(INamespaceSymbol ns)
    {
        foreach (var type in ns.GetTypeMembers())
        {
            yield return type;
        }

        foreach (var child in ns.GetNamespaceMembers())
        {
            foreach (var type in AllTypes(child))
            {
                yield return type;
            }
        }
    }

    private static bool IsFrameworkType(ITypeSymbol type) =>
        type.ContainingAssembly?.Name is { } name
        && (name is "mscorlib" or "netstandard" or "WindowsBase" or "PresentationCore" or "PresentationFramework"
            || name.StartsWith("System", StringComparison.Ordinal) || name.StartsWith("Microsoft.", StringComparison.Ordinal)
            || name.StartsWith("PresentationFramework.", StringComparison.Ordinal));

    private IFieldSymbol? FindDependencyPropertyField(ITypeSymbol type, string name) =>
        type is INamedTypeSymbol named
            ? named.BaseTypesAndSelf().SelectMany(t => t.GetMembers(name + "Property")).OfType<IFieldSymbol>()
                .FirstOrDefault(f => f.IsStatic && SymbolEqualityComparer.Default.Equals(f.Type, Known.DependencyProperty))
            : null;

    /// <summary>The type an element creates: the x:Class for the root, otherwise the element's type.</summary>
    private INamedTypeSymbol? InstanceType(XamlElement element) =>
        element.Parent is null && _model.ClassSymbol is { } classSymbol ? classSymbol : _model.GetElementInfo(element).Type?.Symbol;

    /// <summary>Setters, triggers and conditions: their bindings target the styled or templated element, not themselves.</summary>
    private bool IsStyleInfrastructure(XamlElement element) =>
        _model.GetElementInfo(element).Type?.Symbol is { } type
        && (type.DerivesFrom(Known.SetterBase) || type.DerivesFrom(Known.TriggerBase) || type.DerivesFrom(Known.Condition));

    /// <summary>Element type of a collection (arrays and <c>IEnumerable&lt;T&gt;</c>).</summary>
    public static ITypeSymbol? ItemType(ITypeSymbol collection)
    {
        if (collection is IArrayTypeSymbol array)
        {
            return array.ElementType;
        }

        var enumerable = collection.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T
            ? (INamedTypeSymbol)collection
            : collection.AllInterfaces.FirstOrDefault(i => i.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T);
        return enumerable?.TypeArguments[0];
    }

    // ------------------------------------------------------------------ data context

    private BindingSource ComputeDataContext(XamlElement element)
    {
        if (OwnDataContext(element) is { } own)
        {
            return own;
        }

        var info = _model.GetElementInfo(element);
        if (info.Type is { } type)
        {
            if (type.Symbol.DerivesFrom(Known.DataTemplate))
            {
                return DataTemplateContext(element);
            }

            if (type.IsTemplate || type.Symbol.DerivesFrom(Known.Style))
            {
                return BindingSource.Unknown("the data context of template and style content is set where they are applied");
            }
        }

        if (element.Parent is { IsPropertyElement: true } property && property.Name.MemberName == "Resources")
        {
            return BindingSource.Unknown("resources don't inherit a data context");
        }

        var parent = _model.GetParentObjectElement(element);
        return parent is null ? BindingSource.Unknown("no data context is declared") : GetDataContext(parent);
    }

    /// <summary>A DataContext the element sets itself: DataContext, &lt;X.DataContext&gt; or d:DataContext.</summary>
    private BindingSource? OwnDataContext(XamlElement element)
    {
        BindingSource? declared = null;
        if (element.FindAttribute(null, "DataContext") is { } attribute && _model.GetAttributeInfo(attribute).Member is not null)
        {
            declared = DataContextValue(element, attribute);
        }
        else if (element.ChildElements.FirstOrDefault(c => c.IsPropertyElement && c.Name.MemberName == "DataContext") is { } property)
        {
            var value = property.ChildElements.FirstOrDefault(c => !c.IsPropertyElement);
            declared = value is not null && _model.GetElementInfo(value).Type is { } valueType
                ? new BindingSource(valueType.Symbol, $"<{element.Name.LocalName}.DataContext>")
                : BindingSource.Unknown("the DataContext element can't be resolved");
        }

        if (declared?.Type is not null)
        {
            return declared;
        }

        // d:DataContext states the designer's intent; prefer it over a DataContext we can't type.
        return DesignDataContext(element) ?? declared;
    }

    private BindingSource DataContextValue(XamlElement element, XamlAttribute attribute)
    {
        if (_model.GetMarkupExtension(attribute)?.Root is not { } extension)
        {
            return BindingSource.Unknown("DataContext is set to a literal");
        }

        if (GetBinding(extension, element, attribute) is { } binding)
        {
            var resolution = ResolvePath(binding.Source, binding.Path, binding.PathOffset, element);
            if (resolution.ResultType is not { } type)
            {
                return BindingSource.Unknown("the DataContext binding can't be resolved");
            }

            // {Binding} keeps the source's precision; a property's declared type is only a lower bound.
            var kind = resolution.Segments.Count == 0 ? binding.Source.Kind : BindingSourceKind.LowerBound;
            return new BindingSource(type, $"DataContext=\"{{Binding {binding.Path.Trim()}}}\"", kind);
        }

        return MarkupValue(element, extension) is { Type: not null } value
            ? value with { Origin = $"DataContext=\"{{{extension.TypeName.FullName} ...}}\"" }
            : BindingSource.Unknown("DataContext is set to a value that can't be typed");
    }

    private BindingSource? DesignDataContext(XamlElement element)
    {
        var attribute = element.Attributes.FirstOrDefault(a => a.Name.LocalName == "DataContext" && a.Name.Prefix is { } p
            && element.LookupNamespace(p) == XmlNamespaces.Blend2008);
        if (attribute is null)
        {
            return null;
        }

        if (_model.GetMarkupExtension(attribute)?.Root is { TypeName.LocalName: "DesignInstance" } design
            && (design.FindArgument("Type") ?? design.PositionalArguments.FirstOrDefault()) is { Value.Extension: null } typeArgument
            && _model.ResolveTypeReference(element, typeArgument.Value.Text) is { } type)
        {
            // The declared view model; the run-time one may be a subclass.
            return new BindingSource(type.Symbol, "d:DataContext", BindingSourceKind.LowerBound);
        }

        return BindingSource.Unknown("d:DataContext can't be resolved");
    }

    private BindingSource DataTemplateContext(XamlElement template)
    {
        // <ItemsControl ItemsSource="{Binding Items}"><ItemsControl.ItemTemplate><DataTemplate>: an explicitly
        // assigned item template receives the ItemsSource items, whatever its DataType says.
        if (template.Parent is { IsPropertyElement: true, Name.MemberName: "ItemTemplate" } property
            && property.Parent is { } itemsControl
            && itemsControl.FindAttribute(null, "ItemsSource") is { } itemsSource
            && _model.GetMarkupExtension(itemsSource)?.Root is { } extension
            && GetBinding(extension, itemsControl, itemsSource) is { } binding
            && ResolvePath(binding.Source, binding.Path, binding.PathOffset, itemsControl).ResultType is { } collection
            && ItemType(collection) is { } item)
        {
            return new BindingSource(item, $"item of {itemsControl.Name.LocalName}.ItemsSource", BindingSourceKind.LowerBound);
        }

        if (template.FindAttribute(null, "DataType") is { Value: { } dataType })
        {
            // Only {x:Type T} is a CLR type; a plain string DataType names an XML element (XmlDataProvider).
            if (!MarkupExtensionParser.IsMarkupExtension(dataType))
            {
                return BindingSource.Unknown("a DataType without {x:Type} is an XML element name");
            }

            // WPF enforces DataType only when it picks an implicit template: unkeyed, in resources (and then for
            // subclasses too). Keyed or explicitly assigned templates can be given any item.
            var isImplicit = Resources.ResourceSyntax.GetStringKey(template) is null && IsInResources(template);
            return _model.ResolveTypeReference(template, dataType) is { } type
                ? new BindingSource(type.Symbol, $"{template.Name.LocalName}.DataType", isImplicit ? BindingSourceKind.LowerBound : BindingSourceKind.Hint)
                : BindingSource.Unknown("DataType can't be resolved");
        }

        return BindingSource.Unknown("the DataTemplate has no DataType");
    }

    private static bool IsInResources(XamlElement element) =>
        element.Parent is { IsPropertyElement: true, Name.MemberName: "Resources" }
        || (element.Parent is { IsPropertyElement: false } parent && Resources.ResourceCollector.IsDictionaryElement(parent));

    // ------------------------------------------------------------------ binding sources

    private BindingSource ResolveSource(MarkupExtensionSyntax binding, XamlElement element, bool isDataContext)
    {
        if (binding.FindArgument("XPath") is not null)
        {
            return BindingSource.Unknown("XPath bindings are not checked");
        }

        if (binding.FindArgument("ElementName") is { Value: { Extension: null, Text: { Length: > 0 } name } })
        {
            return FindNamedElement(name.Trim()) is { } named && InstanceType(named) is { } namedType
                ? new BindingSource(namedType, $"element '{name.Trim()}'")
                : BindingSource.Unknown($"element '{name.Trim()}' can't be found");
        }

        if (binding.FindArgument("RelativeSource")?.Value.Extension is { } relative)
        {
            return RelativeSourceType(relative, element);
        }

        if (binding.FindArgument("Source") is { } source)
        {
            return source.Value.Extension is { } sourceExtension && MarkupValue(element, sourceExtension) is { Type: not null } value
                ? value with { Origin = "Source" }
                : BindingSource.Unknown("the binding Source can't be typed");
        }

        if (isDataContext)
        {
            var parent = _model.GetParentObjectElement(element);
            return parent is null ? BindingSource.Unknown("no data context is declared") : GetDataContext(parent);
        }

        return GetDataContext(element);
    }

    private BindingSource RelativeSourceType(MarkupExtensionSyntax relative, XamlElement element)
    {
        var mode = (relative.FindArgument("Mode") ?? relative.PositionalArguments.FirstOrDefault())?.Value.Text.Trim();
        var ancestor = relative.FindArgument("AncestorType")?.Value;
        if (ancestor is not null)
        {
            var text = ancestor.Extension is { } typeExtension
                ? (typeExtension.FindArgument("TypeName") ?? typeExtension.PositionalArguments.FirstOrDefault())?.Value.Text
                : ancestor.Text;
            return text is not null && _model.ResolveTypeReference(element, text) is { } type
                ? new BindingSource(type.Symbol, $"ancestor {type.Name}", BindingSourceKind.LowerBound)
                : BindingSource.Unknown("AncestorType can't be resolved");
        }

        return mode switch
        {
            // In setters and triggers, Self is the styled element, known only up to the TargetType.
            "Self" when IsStyleInfrastructure(element) => _model.FindTargetType(element) is { } styled
                ? new BindingSource(styled.Symbol, "Self (styled element)", BindingSourceKind.LowerBound)
                : BindingSource.Unknown("the styled element's type is unknown"),
            "Self" => InstanceType(element) is { } self
                ? new BindingSource(self, "Self")
                : BindingSource.Unknown("the element type is unknown"),
            "TemplatedParent" => _model.FindTargetType(element) is { } target
                ? new BindingSource(target.Symbol, "TemplatedParent", BindingSourceKind.LowerBound)
                : BindingSource.Unknown("the template has no TargetType"),
            _ => BindingSource.Unknown($"RelativeSource {mode} is not checked"),
        };
    }

    /// <summary>A value given as {StaticResource} (defined in this document; exact) or {x:Static} (declared type; lower bound).</summary>
    private BindingSource? MarkupValue(XamlElement element, MarkupExtensionSyntax extension) =>
        MarkupValueType(element, extension, out var exact) is { } type
            ? new BindingSource(type, extension.TypeName.FullName, exact ? BindingSourceKind.Exact : BindingSourceKind.LowerBound)
            : null;

    private ITypeSymbol? MarkupValueType(XamlElement element, MarkupExtensionSyntax extension, out bool exact)
    {
        exact = false;
        var type = _model.ResolveMarkupExtensionType(element, extension.TypeName, out _);
        if (type is null)
        {
            return null;
        }

        var argument = extension.PositionalArguments.FirstOrDefault()?.Value.Text.Trim();
        if (type.Symbol.DerivesFrom(Known.StaticResourceExtension) || type.Symbol.DerivesFrom(Known.DynamicResourceExtension))
        {
            argument = (extension.FindArgument("ResourceKey") ?? extension.PositionalArguments.FirstOrDefault())?.Value.Text.Trim();
            var definition = argument is null ? null : _model.GetResourceScope(element).Find(argument).FirstOrDefault(d => d.FilePath is null);
            exact = true; // a resource element creates exactly its type
            return definition is null ? null : _model.ResolveTypeReference(element, definition.ElementName)?.Symbol;
        }

        if (type.Name == "StaticExtension" && (extension.FindArgument("Member")?.Value.Text.Trim() ?? argument) is { } member)
        {
            var dot = member.LastIndexOf('.');
            var owner = dot < 0 ? null : _model.ResolveTypeReference(element, member[..dot]);
            var symbol = owner?.Symbol.BaseTypesAndSelf().SelectMany(t => t.GetMembers(member[(dot + 1)..])).FirstOrDefault(s => s.IsStatic);
            return symbol switch
            {
                IPropertySymbol p => p.Type,
                IFieldSymbol f => f.Type,
                _ => null,
            };
        }

        return null;
    }

    private XamlElement? FindNamedElement(string name) =>
        _model.Document.AllElements().FirstOrDefault(e => e.Attributes.Any(a =>
            a.Name.LocalName == "Name" && a.Value == name
            && (a.Name.Prefix is null || e.LookupNamespace(a.Name.Prefix) == XmlNamespaces.Xaml2006)));

    // ------------------------------------------------------------------ path helpers

    private static IPropertySymbol? FindProperty(ITypeSymbol type, string name) =>
        GetBindableProperties(type).FirstOrDefault(p => p.Name == name);

    private static ITypeSymbol? IndexerType(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol array)
        {
            return array.ElementType;
        }

        IEnumerable<ITypeSymbol> types = type is INamedTypeSymbol named ? [.. named.BaseTypesAndSelf(), .. type.AllInterfaces] : [type];
        return types.SelectMany(t => t.GetMembers().OfType<IPropertySymbol>()).FirstOrDefault(p => p.IsIndexer && !p.IsStatic)?.Type;
    }

    private XamlMember? ResolveAttached(string text, XamlElement scope)
    {
        var dot = text.LastIndexOf('.');
        if (dot <= 0)
        {
            return null;
        }

        var owner = _model.ResolveTypeReference(scope, text[..dot]);
        var name = text[(dot + 1)..];
        return owner?.GetAttachableMember(name) ?? owner?.GetMember(name);
    }

    /// <summary>Offset of the value's text: inside the quotes when the markup-extension value is quoted.</summary>
    private int ValueTextOffset(MarkupValueSyntax value)
    {
        var text = _model.Document.Text;
        return value.Span.Length > 0 && value.Span.Start < text.Length && text[value.Span.Start] is '\'' or '"'
            ? value.Span.Start + 1
            : value.Span.Start;
    }
}
