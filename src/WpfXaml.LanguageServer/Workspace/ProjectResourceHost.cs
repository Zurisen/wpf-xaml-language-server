using System.Collections.Concurrent;
using WpfXaml.Analysis.Resources;
using WpfXaml.Language.Syntax;
using WpfXaml.LanguageServer.Protocol;

namespace WpfXaml.LanguageServer.Workspace;

/// <summary>
/// Gives resource lookup access to other XAML files: open editor buffers first, then files on disk, and maps
/// resource dictionary URIs (relative, rooted, <c>pack://application:,,,/</c>, <c>/Assembly;component/</c>)
/// to files using the loaded projects. Call <see cref="Invalidate"/> whenever a XAML file changes.
/// </summary>
internal sealed class ProjectResourceHost(Func<ProjectSystem> projects, DocumentStore? documents) : IXamlResourceHost
{
    private const string ApplicationPackPrefix = "pack://application:,,,";
    private const string ComponentMarker = ";component/";

    private readonly ConcurrentDictionary<string, (DateTime Stamp, XamlDocument? Document)> _disk = new(DocumentUris.PathComparer);
    private ResourceCache _cache = new();
    private int _version;

    /// <summary>Changes whenever cached resource information may be stale.</summary>
    public int Version => Volatile.Read(ref _version);

    public XamlResourceContext CreateContext(string? filePath) => new(this, filePath, Volatile.Read(ref _cache));

    public void Invalidate()
    {
        Volatile.Write(ref _cache, new ResourceCache());
        Interlocked.Increment(ref _version);
    }

    public XamlDocument? GetDocument(string filePath)
    {
        if (documents?.FindByPath(filePath) is { } open)
        {
            return open.Syntax;
        }

        DateTime stamp;
        try
        {
            if (!File.Exists(filePath))
            {
                return null;
            }

            stamp = File.GetLastWriteTimeUtc(filePath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (_disk.TryGetValue(filePath, out var cached) && cached.Stamp == stamp)
        {
            return cached.Document;
        }

        XamlDocument? document;
        try
        {
            document = XamlDocument.Parse(File.ReadAllText(filePath));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            document = null;
        }

        _disk[filePath] = (stamp, document);
        return document;
    }

    public ResourceSourceResolution ResolveSource(string? fromFilePath, string source)
    {
        var uri = source.Trim();
        if (uri.StartsWith(ApplicationPackPrefix, StringComparison.OrdinalIgnoreCase))
        {
            uri = uri[ApplicationPackPrefix.Length..];
        }
        else if (uri.Contains("://", StringComparison.Ordinal))
        {
            return ResourceSourceResolution.Unknown($"'{source}' is not an application resource");
        }

        var component = uri.IndexOf(ComponentMarker, StringComparison.OrdinalIgnoreCase);
        if (component >= 0)
        {
            // "/Assembly;component/Path" (optionally "/Assembly;v1.0.0.0;key;component/Path").
            var assembly = uri[..component].TrimStart('/').Split(';')[0];
            var directory = projects().FindProjectDirectoryByAssemblyName(assembly);
            return directory is null
                ? ResourceSourceResolution.Unknown($"'{assembly}' is not a project in the loaded solution")
                : ResourceSourceResolution.File(Combine(directory, uri[(component + ComponentMarker.Length)..]));
        }

        if (uri.StartsWith('/'))
        {
            var projectDirectory = fromFilePath is null ? null : projects().GetProjectDirectory(fromFilePath);
            return projectDirectory is null
                ? ResourceSourceResolution.Unknown($"the project of '{source}' is unknown")
                : ResourceSourceResolution.File(Combine(projectDirectory, uri.TrimStart('/')));
        }

        return fromFilePath is null || Path.GetDirectoryName(fromFilePath) is not { } fromDirectory
            ? ResourceSourceResolution.Unknown($"'{source}' is relative to a file without a path")
            : ResourceSourceResolution.File(Combine(fromDirectory, uri));
    }

    public IReadOnlyList<string>? GetApplicationDefinitions(string? filePath) =>
        filePath is null ? null : projects().GetApplicationDefinitions(filePath);

    public IReadOnlySet<string>? GetAllResourceKeys()
    {
        var cache = Volatile.Read(ref _cache);
        if (cache.AllKeys is { } cached)
        {
            return cached;
        }

        var files = projects().GetAllXamlFiles();
        if (files.Count == 0)
        {
            return null;
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (GetDocument(file) is { } document)
            {
                foreach (var element in document.AllElements())
                {
                    if (ResourceSyntax.GetStringKey(element) is { } key)
                    {
                        keys.Add(key);
                    }
                }
            }
        }

        cache.AllKeys = keys;
        return keys;
    }

    private static string Combine(string directory, string relative) =>
        Path.GetFullPath(Path.Combine(directory, Uri.UnescapeDataString(relative).Replace('/', Path.DirectorySeparatorChar)));
}
