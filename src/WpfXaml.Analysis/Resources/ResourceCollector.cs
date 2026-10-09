using WpfXaml.Language.Syntax;
using WpfXaml.Language.Text;

namespace WpfXaml.Analysis.Resources;

/// <summary>
/// Collects keyed resources from dictionaries, following <c>MergedDictionaries</c> and <c>Source</c> URIs into
/// other files. Works on syntax only: other files never need a type system, which keeps cross-file lookups cheap.
/// </summary>
internal sealed class ResourceCollector(XamlResourceContext? context, XamlDocument currentDocument)
{
    /// <summary>Content of a <c>Owner.Resources</c> property element.</summary>
    public ResourceTable FromResourcesProperty(XamlElement property, XamlDocument document, string? path, HashSet<string> visiting)
    {
        var children = property.ChildElements.Where(c => !c.IsPropertyElement).ToList();

        // <X.Resources><ResourceDictionary>...</ResourceDictionary></X.Resources> sets the dictionary itself (WPF
        // assigns a lone dictionary child as the property value, ignoring any x:Key on it); otherwise the children
        // are added to an implicit one.
        if (children is [var single] && IsDictionaryElement(single))
        {
            return FromDictionary(single, document, path, visiting);
        }

        var table = new ResourceTable();
        foreach (var child in children)
        {
            AddKeyed(table, child, document, path);
        }

        return table;
    }

    /// <summary>Content of a ResourceDictionary element: its Source, merged dictionaries and keyed children.</summary>
    public ResourceTable FromDictionary(XamlElement dictionary, XamlDocument document, string? path, HashSet<string> visiting)
    {
        var table = new ResourceTable();
        var isPlainDictionary = dictionary.Name.LocalName == "ResourceDictionary"
            && dictionary.LookupNamespace(dictionary.Name.Prefix) == XmlNamespaces.WpfPresentation;

        if (dictionary.FindAttribute(null, "Source") is { Value: { Length: > 0 } source } && !MarkupExtensionParser.IsMarkupExtension(source))
        {
            table.Merge(FromSource(source, path, visiting));
        }
        else if (!isPlainDictionary)
        {
            // A ResourceDictionary subclass (themes, bundles) may add resources in code.
            table.MarkIncomplete($"'{dictionary.Name.FullName}' may define resources in code");
        }

        foreach (var child in dictionary.ChildElements)
        {
            if (!child.IsPropertyElement)
            {
                AddKeyed(table, child, document, path);
            }
            else if (child.Name.MemberName == "MergedDictionaries")
            {
                foreach (var merged in child.ChildElements)
                {
                    table.Merge(FromDictionary(merged, document, path, visiting));
                }
            }
        }

        return table;
    }

    /// <summary>All resources of a dictionary file (cached per file while no XAML file changes).</summary>
    public ResourceTable FromFile(string filePath, HashSet<string> visiting)
    {
        if (context is null)
        {
            return Incomplete("resources from other files are not available");
        }

        if (context.Cache.FileTables.TryGetValue(filePath, out var cached))
        {
            return cached;
        }

        if (!visiting.Add(filePath))
        {
            return new ResourceTable(); // a cycle; its content is already being collected
        }

        try
        {
            var document = context.Host.GetDocument(filePath);
            ResourceTable table;
            if (document?.Root is not { } root)
            {
                table = Incomplete($"'{Path.GetFileName(filePath)}' could not be read");
            }
            else if (IsDictionaryElement(root))
            {
                table = FromDictionary(root, document, filePath, visiting);
            }
            else
            {
                table = Incomplete($"'{Path.GetFileName(filePath)}' is not a resource dictionary");
            }

            context.Cache.FileTables.TryAdd(filePath, table);
            return table;
        }
        finally
        {
            visiting.Remove(filePath);
        }
    }

    /// <summary>The <c>Application.Resources</c> of the applications whose resources reach the current file.</summary>
    public ResourceTable FromApplications()
    {
        if (context is null)
        {
            return Incomplete("application resources are not available without a project");
        }

        var applications = context.Host.GetApplicationDefinitions(context.FilePath);
        if (applications is null)
        {
            return Incomplete("no application (App.xaml) is known for this file");
        }

        var table = new ResourceTable();
        foreach (var app in applications)
        {
            if (context.FilePath is not null && string.Equals(app, context.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                continue; // the current document; its resources are part of its own element tree
            }

            table.Merge(context.Cache.ApplicationTables.GetOrAdd(app, path =>
            {
                var document = context.Host.GetDocument(path);
                var resources = document?.Root?.ChildElements.FirstOrDefault(c => c.IsPropertyElement && c.Name.MemberName == "Resources");
                return resources is null || document is null
                    ? new ResourceTable()
                    : FromResourcesProperty(resources, document, path, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            }));
        }

        return table;
    }

    public ResourceSourceResolution? Resolve(string source, string? fromPath) =>
        context?.Host.ResolveSource(fromPath, source);

    /// <summary>Elements that act as dictionaries: ResourceDictionary and (by naming convention) its subclasses.</summary>
    public static bool IsDictionaryElement(XamlElement element) =>
        !element.IsPropertyElement && element.Name.LocalName.EndsWith("Dictionary", StringComparison.Ordinal);

    /// <summary>The string value of <c>x:Key</c>; null for none or for non-string keys such as <c>{x:Type ...}</c>.</summary>
    public static XamlAttribute? FindKey(XamlElement element) =>
        element.Attributes.FirstOrDefault(a => a.Name.LocalName == "Key" && a.Name.Prefix is { } p
            && element.LookupNamespace(p) == XmlNamespaces.Xaml2006);

    private ResourceTable FromSource(string source, string? fromPath, HashSet<string> visiting)
    {
        var resolution = Resolve(source, fromPath);
        if (resolution is null)
        {
            return Incomplete("resources from other files are not available");
        }

        if (resolution.Value.UnknownReason is { } reason)
        {
            return Incomplete(reason);
        }

        return FromFile(resolution.Value.FilePath!, visiting);
    }

    private void AddKeyed(ResourceTable table, XamlElement element, XamlDocument document, string? path)
    {
        if (FindKey(element) is not { Value: { Length: > 0 } key, ValueSpan: { } span } || MarkupExtensionParser.IsMarkupExtension(key))
        {
            return;
        }

        var lines = document.Lines;
        table.Add(new ResourceDefinition(key, DocumentPath(document, path), span, lines.GetPosition(span.Start), lines.GetPosition(span.End), element.Name.FullName));
    }

    /// <summary>Definitions in the document being analyzed carry a null path (it may be unsaved or have no path).</summary>
    private string? DocumentPath(XamlDocument document, string? path) => ReferenceEquals(document, currentDocument) ? null : path;

    private static ResourceTable Incomplete(string reason)
    {
        var table = new ResourceTable();
        table.MarkIncomplete(reason);
        return table;
    }
}
