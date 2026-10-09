using System.Collections.Concurrent;
using WpfXaml.Language.Syntax;

namespace WpfXaml.Analysis.Resources;

/// <summary>Where a resource dictionary <c>Source</c> URI points.</summary>
/// <param name="FilePath">The file it refers to (which may not exist), when it can be resolved.</param>
/// <param name="UnknownReason">Set when it can't be resolved, e.g. a dictionary in an assembly outside the solution.</param>
public readonly record struct ResourceSourceResolution(string? FilePath, string? UnknownReason)
{
    public static ResourceSourceResolution File(string path) => new(path, null);

    public static ResourceSourceResolution Unknown(string reason) => new(null, reason);
}

/// <summary>
/// What resource lookup needs from the host (editor/project system): other XAML files and how URIs map to them.
/// Keeps the analysis free of file system and project knowledge.
/// </summary>
public interface IXamlResourceHost
{
    /// <summary>The parsed file (the open editor buffer if there is one), or null when it doesn't exist.</summary>
    XamlDocument? GetDocument(string filePath);

    /// <summary>Resolves a <c>Source</c> written in <paramref name="fromFilePath"/> (relative, rooted, or pack URI).</summary>
    ResourceSourceResolution ResolveSource(string? fromFilePath, string source);

    /// <summary>
    /// The <c>App.xaml</c> files whose resources are visible from a file: its project's, or for a library,
    /// those of the applications referencing it. Null when unknown.
    /// </summary>
    IReadOnlyList<string>? GetApplicationDefinitions(string? filePath);

    /// <summary>
    /// Every string <c>x:Key</c> defined in any XAML file of the loaded projects (null when unknown). Used for
    /// deferred content, which can resolve resources from wherever it ends up being used.
    /// </summary>
    IReadOnlySet<string>? GetAllResourceKeys();
}

/// <summary>
/// Resource lookup for one document. Create contexts from the same <see cref="ResourceCache"/> while no XAML
/// file changed, so dictionaries shared by many files are read once.
/// </summary>
public sealed class XamlResourceContext(IXamlResourceHost host, string? filePath, ResourceCache? cache = null)
{
    public IXamlResourceHost Host { get; } = host;

    /// <summary>Path of the document being analyzed, if it has one.</summary>
    public string? FilePath { get; } = filePath;

    public ResourceCache Cache { get; } = cache ?? new ResourceCache();
}

/// <summary>Resource tables of whole files, valid until any XAML file changes.</summary>
public sealed class ResourceCache
{
    internal ConcurrentDictionary<string, ResourceTable> FileTables { get; } = new(StringComparer.OrdinalIgnoreCase);

    internal ConcurrentDictionary<string, ResourceTable> ApplicationTables { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Slot for a host's solution-wide key index, so it is rebuilt only when XAML changes.</summary>
    public IReadOnlySet<string>? AllKeys { get; set; }
}
