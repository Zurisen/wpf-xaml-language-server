using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using WpfXaml.Language.Syntax;

namespace WpfXaml.Analysis.TypeSystem;

/// <summary>
/// The XAML type system for one Roslyn compilation (a project, or the framework-only fallback).
/// Thread-safe; build once per compilation and reuse.
/// </summary>
public sealed class XamlTypeSystem
{
    private readonly Dictionary<string, List<ClrNamespaceMapping>> _xmlnsDefinitions;
    private readonly Dictionary<string, string> _preferredPrefixes;
    private readonly ConcurrentDictionary<string, NamespaceResolution> _namespaces = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<INamedTypeSymbol, XamlTypeInfo> _types = new(SymbolEqualityComparer.Default);

    private XamlTypeSystem(Compilation compilation, bool isFrameworkOnly, bool hasGeneratedCode)
    {
        Compilation = compilation;
        IsFrameworkOnly = isFrameworkOnly;
        HasGeneratedCode = hasGeneratedCode;
        Known = new WellKnownTypes(compilation);
        (_xmlnsDefinitions, _preferredPrefixes) = ReadXmlnsAttributes();
    }

    public Compilation Compilation { get; }

    public WellKnownTypes Known { get; }

    /// <summary>
    /// True when the compilation only has framework references (no project loaded). In that mode,
    /// anything from non-framework namespaces is unknown rather than wrong, so it is not reported.
    /// </summary>
    public bool IsFrameworkOnly { get; }

    public IReadOnlyCollection<string> XmlnsDefinitionUris => _xmlnsDefinitions.Keys;

    /// <summary>
    /// False when the compilation was created without running source generators (a fast first pass). Members
    /// generators add (e.g. [ObservableProperty]) are then missing, so checks that depend on them stay silent.
    /// </summary>
    public bool HasGeneratedCode { get; }

    public static XamlTypeSystem Create(Compilation compilation, bool isFrameworkOnly = false, bool hasGeneratedCode = true) =>
        new(compilation, isFrameworkOnly, hasGeneratedCode);

    public string? GetPreferredPrefix(string uri) => _preferredPrefixes.GetValueOrDefault(uri);

    public XamlTypeInfo GetTypeInfo(INamedTypeSymbol symbol) => _types.GetOrAdd(symbol, s => new XamlTypeInfo(this, s));

    public XamlNamespace? GetNamespace(string uri) => ResolveNamespace(uri).Namespace;

    /// <summary>Resolves a namespace URI; <see cref="NamespaceResolution.Error"/> explains failures.</summary>
    public NamespaceResolution ResolveNamespace(string uri) => _namespaces.GetOrAdd(uri, CreateNamespace);

    public IAssemblySymbol? FindAssembly(string name)
    {
        if (string.Equals(Compilation.Assembly.Name, name, StringComparison.OrdinalIgnoreCase))
        {
            return Compilation.Assembly;
        }

        return Compilation.SourceModule.ReferencedAssemblySymbols
            .FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Namespaces declared in the project's own source, for <c>clr-namespace:</c> completion.</summary>
    public IEnumerable<string> GetSourceNamespaces()
    {
        if (IsFrameworkOnly)
        {
            yield break;
        }

        var stack = new Stack<INamespaceSymbol>();
        stack.Push(Compilation.Assembly.GlobalNamespace);
        while (stack.Count > 0)
        {
            var ns = stack.Pop();
            if (!ns.IsGlobalNamespace && ns.GetTypeMembers().Length > 0)
            {
                yield return ns.ToDisplayString();
            }

            foreach (var child in ns.GetNamespaceMembers())
            {
                stack.Push(child);
            }
        }
    }

    private NamespaceResolution CreateNamespace(string uri)
    {
        if (uri.StartsWith(XmlNamespaces.ClrNamespacePrefix, StringComparison.Ordinal))
        {
            return ResolveClrNamespace(uri);
        }

        if (_xmlnsDefinitions.TryGetValue(uri, out var mappings))
        {
            return new NamespaceResolution(new XamlNamespace(this, uri, mappings), null);
        }

        return new NamespaceResolution(null, $"No referenced assembly defines the XAML namespace '{uri}'.");
    }

    private NamespaceResolution ResolveClrNamespace(string uri)
    {
        // clr-namespace:Some.Namespace;assembly=Some.Assembly
        var body = uri[XmlNamespaces.ClrNamespacePrefix.Length..];
        string clrNamespace;
        string? assemblyName = null;
        var semicolon = body.IndexOf(';');
        if (semicolon < 0)
        {
            clrNamespace = body.Trim();
        }
        else
        {
            clrNamespace = body[..semicolon].Trim();
            var rest = body[(semicolon + 1)..].Trim();
            if (rest.StartsWith("assembly=", StringComparison.OrdinalIgnoreCase))
            {
                assemblyName = rest["assembly=".Length..].Trim();
            }
        }

        IAssemblySymbol? assembly;
        if (string.IsNullOrEmpty(assemblyName))
        {
            assembly = Compilation.Assembly;
        }
        else
        {
            // "assembly=Foo, Version=..." is allowed; only the simple name matters.
            var simpleName = assemblyName.Split(',')[0].Trim();
            assembly = FindAssembly(simpleName);
            if (assembly is null)
            {
                return new NamespaceResolution(null, $"Assembly '{simpleName}' is not referenced by the project.");
            }
        }

        return new NamespaceResolution(new XamlNamespace(this, uri, [new ClrNamespaceMapping(assembly, clrNamespace)]), null);
    }

    private (Dictionary<string, List<ClrNamespaceMapping>>, Dictionary<string, string>) ReadXmlnsAttributes()
    {
        var definitions = new Dictionary<string, List<ClrNamespaceMapping>>(StringComparer.Ordinal);
        var prefixes = new Dictionary<string, string>(StringComparer.Ordinal);
        var definitionAttribute = Known.XmlnsDefinitionAttribute;
        var prefixAttribute = Known.XmlnsPrefixAttribute;
        if (definitionAttribute is null)
        {
            return (definitions, prefixes);
        }

        var assemblies = Compilation.SourceModule.ReferencedAssemblySymbols.Prepend(Compilation.Assembly);
        foreach (var assembly in assemblies)
        {
            foreach (var attribute in assembly.GetAttributes())
            {
                if (SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, definitionAttribute)
                    && attribute.ConstructorArguments is [{ Value: string xmlns }, { Value: string clrNamespace }])
                {
                    var target = assembly;
                    foreach (var named in attribute.NamedArguments)
                    {
                        if (named is { Key: "AssemblyName", Value.Value: string otherAssembly } && FindAssembly(otherAssembly.Split(',')[0]) is { } other)
                        {
                            target = other;
                        }
                    }

                    if (!definitions.TryGetValue(xmlns, out var list))
                    {
                        definitions[xmlns] = list = [];
                    }

                    if (!list.Any(m => SymbolEqualityComparer.Default.Equals(m.Assembly, target) && m.ClrNamespace == clrNamespace))
                    {
                        list.Add(new ClrNamespaceMapping(target, clrNamespace));
                    }
                }
                else if (prefixAttribute is not null
                    && SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, prefixAttribute)
                    && attribute.ConstructorArguments is [{ Value: string prefixXmlns }, { Value: string prefix }])
                {
                    prefixes.TryAdd(prefixXmlns, prefix);
                }
            }
        }

        return (definitions, prefixes);
    }
}

public sealed record NamespaceResolution(XamlNamespace? Namespace, string? Error);
