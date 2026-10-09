using WpfXaml.Language.Text;

namespace WpfXaml.Analysis.Resources;

public static class ResourceSyntax
{
    /// <summary>The string <c>x:Key</c> of an element; null when it has none or a non-string key such as <c>{x:Type T}</c>.</summary>
    public static string? GetStringKey(Language.Syntax.XamlElement element) =>
        ResourceCollector.FindKey(element) is { Value: { Length: > 0 } key } && !Language.Syntax.MarkupExtensionParser.IsMarkupExtension(key)
            ? key
            : null;
}

/// <summary>A keyed resource (<c>x:Key="Name"</c>).</summary>
/// <param name="FilePath">The file that defines it; null when it is the document being analyzed.</param>
/// <param name="KeySpan">Span of the <c>x:Key</c> value in that file.</param>
/// <param name="ElementName">The resource element as written, e.g. <c>SolidColorBrush</c> or <c>local:MyConverter</c>.</param>
public sealed record ResourceDefinition(string Key, string? FilePath, TextSpan KeySpan, LinePosition Start, LinePosition End, string ElementName);

/// <summary>
/// The string keys a dictionary provides, merged dictionaries included. <see cref="IsComplete"/> is false when
/// some of its content could not be seen (a dictionary from an assembly outside the solution, a dictionary type
/// that may add resources in code), in which case a missing key is not an error.
/// </summary>
public sealed class ResourceTable
{
    private readonly Dictionary<string, List<ResourceDefinition>> _keys = new(StringComparer.Ordinal);
    private readonly List<string> _incompleteReasons = [];

    public bool IsComplete => _incompleteReasons.Count == 0;

    public bool IsEmpty => _keys.Count == 0 && IsComplete;

    /// <summary>Why some resources may be missing (shown in hover when a key isn't found).</summary>
    public IReadOnlyList<string> IncompleteReasons => _incompleteReasons;

    public IEnumerable<ResourceDefinition> Definitions => _keys.Values.SelectMany(v => v);

    public IReadOnlyList<ResourceDefinition> Find(string key) => _keys.TryGetValue(key, out var list) ? list : [];

    internal void Add(ResourceDefinition definition)
    {
        if (!_keys.TryGetValue(definition.Key, out var list))
        {
            _keys[definition.Key] = list = [];
        }

        list.Add(definition);
    }

    internal void MarkIncomplete(string reason)
    {
        if (!_incompleteReasons.Contains(reason))
        {
            _incompleteReasons.Add(reason);
        }
    }

    internal void Merge(ResourceTable other)
    {
        foreach (var definition in other.Definitions)
        {
            Add(definition);
        }

        foreach (var reason in other._incompleteReasons)
        {
            MarkIncomplete(reason);
        }
    }
}

/// <summary>
/// The resources visible at an element: its own and its ancestors' resources, then the application's.
/// Lookups return the innermost definitions, like WPF's resource lookup.
/// </summary>
public sealed class ResourceScope
{
    internal ResourceScope(ResourceTable local, ResourceScope? parent)
    {
        Local = local;
        Parent = parent;
    }

    public ResourceTable Local { get; }

    public ResourceScope? Parent { get; }

    /// <summary>True when every dictionary in scope could be read, so a missing key really is missing.</summary>
    public bool IsComplete => Local.IsComplete && (Parent?.IsComplete ?? true);

    public IEnumerable<string> IncompleteReasons =>
        Local.IncompleteReasons.Concat(Parent?.IncompleteReasons ?? []).Distinct(StringComparer.Ordinal);

    public IReadOnlyList<ResourceDefinition> Find(string key)
    {
        for (var scope = this; scope is not null; scope = scope.Parent)
        {
            var found = scope.Local.Find(key);
            if (found.Count > 0)
            {
                return found;
            }
        }

        return [];
    }

    /// <summary>Every key in scope once (the innermost definition wins).</summary>
    public IEnumerable<ResourceDefinition> AllDefinitions()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var scope = this; scope is not null; scope = scope.Parent)
        {
            foreach (var definition in scope.Local.Definitions)
            {
                if (seen.Add(definition.Key))
                {
                    yield return definition;
                }
            }
        }
    }
}
