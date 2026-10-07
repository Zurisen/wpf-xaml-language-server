using Microsoft.CodeAnalysis;

namespace WpfXaml.Analysis.TypeSystem;

public sealed record ClrNamespaceMapping(IAssemblySymbol Assembly, string ClrNamespace);

/// <summary>
/// A XAML namespace: either a URI mapped by <c>[XmlnsDefinition]</c> attributes, or a
/// <c>clr-namespace:</c> URI. Maps to one or more CLR namespaces in specific assemblies.
/// </summary>
public sealed class XamlNamespace
{
    private readonly XamlTypeSystem _typeSystem;
    private readonly Lazy<IReadOnlyList<XamlTypeInfo>> _types;

    internal XamlNamespace(XamlTypeSystem typeSystem, string uri, IReadOnlyList<ClrNamespaceMapping> mappings)
    {
        _typeSystem = typeSystem;
        Uri = uri;
        Mappings = mappings;
        _types = new Lazy<IReadOnlyList<XamlTypeInfo>>(LoadTypes);
    }

    public string Uri { get; }

    public IReadOnlyList<ClrNamespaceMapping> Mappings { get; }

    /// <summary>All accessible, non-nested, non-generic types in the namespace.</summary>
    public IReadOnlyList<XamlTypeInfo> Types => _types.Value;

    public IEnumerable<XamlTypeInfo> CreatableTypes => Types.Where(t => t.IsCreatable && t.Symbol.TypeKind != TypeKind.Delegate);

    public IEnumerable<XamlTypeInfo> MarkupExtensions => Types.Where(t => t.IsMarkupExtension && !t.Symbol.IsAbstract);

    public IEnumerable<XamlTypeInfo> AttachableOwners => Types.Where(t => t.HasAttachableMembers);

    /// <summary>Finds a type by its XAML name.</summary>
    public XamlTypeInfo? FindType(string name)
    {
        if (name.Length == 0)
        {
            return null;
        }

        foreach (var mapping in Mappings)
        {
            var metadataName = mapping.ClrNamespace.Length == 0 ? name : mapping.ClrNamespace + "." + name;
            var symbol = mapping.Assembly.GetTypeByMetadataName(metadataName) ?? mapping.Assembly.ResolveForwardedType(metadataName);
            if (symbol is not null && IsVisible(symbol))
            {
                return _typeSystem.GetTypeInfo(symbol);
            }
        }

        return null;
    }

    /// <summary>Finds a type by XAML name, trying the <c>Extension</c> suffix first for markup extensions (<c>x:Type</c> → <c>TypeExtension</c>).</summary>
    public XamlTypeInfo? FindMarkupExtension(string name)
    {
        var withSuffix = FindType(name + "Extension");
        if (withSuffix is { IsMarkupExtension: true })
        {
            return withSuffix;
        }

        var plain = FindType(name);
        return plain is { IsMarkupExtension: true } ? plain : null;
    }

    /// <summary>Element lookup: the plain name, then markup extensions used as elements (<c>&lt;x:Static&gt;</c>).</summary>
    public XamlTypeInfo? FindElementType(string name) => FindType(name) ?? FindMarkupExtension(name);

    private bool IsVisible(INamedTypeSymbol symbol) =>
        symbol.ContainingType is null
        && (symbol.DeclaredAccessibility == Accessibility.Public
            || SymbolEqualityComparer.Default.Equals(symbol.ContainingAssembly, _typeSystem.Compilation.Assembly));

    private IReadOnlyList<XamlTypeInfo> LoadTypes()
    {
        var result = new List<XamlTypeInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mapping in Mappings)
        {
            var ns = FindNamespace(mapping.Assembly.GlobalNamespace, mapping.ClrNamespace);
            if (ns is not null)
            {
                foreach (var type in ns.GetTypeMembers())
                {
                    if (!type.IsGenericType && IsVisible(type) && seen.Add(type.Name))
                    {
                        result.Add(_typeSystem.GetTypeInfo(type));
                    }
                }
            }

            foreach (var forwarded in mapping.Assembly.GetForwardedTypes())
            {
                if (forwarded.ContainingNamespace.ToDisplayString() == mapping.ClrNamespace
                    && !forwarded.IsGenericType && forwarded.ContainingType is null && seen.Add(forwarded.Name))
                {
                    result.Add(_typeSystem.GetTypeInfo(forwarded));
                }
            }
        }

        result.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return result;
    }

    internal static INamespaceSymbol? FindNamespace(INamespaceSymbol global, string clrNamespace)
    {
        if (clrNamespace.Length == 0)
        {
            return global;
        }

        var current = global;
        foreach (var part in clrNamespace.Split('.'))
        {
            current = current.GetNamespaceMembers().FirstOrDefault(n => n.Name == part);
            if (current is null)
            {
                return null;
            }
        }

        return current;
    }

    public override string ToString() => Uri;
}
