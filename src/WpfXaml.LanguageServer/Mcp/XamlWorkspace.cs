using WpfXaml.Analysis.Semantics;
using WpfXaml.Analysis.TypeSystem;
using WpfXaml.Language.Syntax;
using WpfXaml.LanguageServer.Workspace;

namespace WpfXaml.LanguageServer.Mcp;

/// <summary>Options for <c>wpf-xaml-ls mcp</c>.</summary>
/// <param name="Root">Directory whose solution or projects are loaded (default: the current directory).</param>
/// <param name="Solution">Explicit .sln/.slnx/.csproj to load instead of discovering one.</param>
public sealed record McpOptions(string Root, string? Solution);

/// <summary>One analyzed XAML file: its syntax, its project context and (when types are known) its semantic model.</summary>
public sealed record AnalyzedFile(string Path, XamlDocument Document, XamlFileContext Context, XamlSemanticModel? Model);

/// <summary>
/// The projects and resources the MCP tools analyze against. Loading starts when the server starts; tools wait for
/// it, because agents want accurate answers more than fast ones (and generators always run, unlike in the editor's
/// first pass).
/// </summary>
public sealed class XamlWorkspace : IDisposable
{
    private readonly ProjectSystem _projects;
    private readonly ProjectResourceHost _resources;

    public XamlWorkspace(McpOptions options, ServerLog log)
    {
        Root = Path.GetFullPath(options.Root);
        _projects = new ProjectSystem(log, loadOnDemand: true, waitForGenerators: true);
        _resources = new ProjectResourceHost(() => _projects, documents: null);
        _projects.Changed += _resources.Invalidate;
        _projects.StartLoading(ProjectSystem.DiscoverLoadTargets(options.Solution, Root));
    }

    public string Root { get; }

    /// <summary>Resolves a path given by an agent (absolute, or relative to the root).</summary>
    public string ResolvePath(string path) => Path.GetFullPath(path, Root);

    public async Task<AnalyzedFile> AnalyzeAsync(string path, string? content, CancellationToken cancellationToken)
    {
        var fullPath = ResolvePath(path);
        await _projects.WaitForLoadsAsync().ConfigureAwait(false);
        _projects.EnsureOwnerLoading(fullPath);
        await _projects.WaitForLoadsAsync().ConfigureAwait(false);

        var text = content ?? await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
        var document = XamlDocument.Parse(text);
        var context = await _projects.GetContextAsync(fullPath, cancellationToken).ConfigureAwait(false);
        var model = context.Types is null || context.Status == XamlFileStatus.ExcludedFromProject
            ? null
            : new XamlSemanticModel(document, context.Types, _resources.CreateContext(fullPath));
        return new AnalyzedFile(fullPath, document, context, model);
    }

    /// <summary>The type system for a file's project, or the WPF framework types when no file is given.</summary>
    public async Task<XamlTypeSystem?> GetTypeSystemAsync(string? contextPath, CancellationToken cancellationToken)
    {
        await _projects.WaitForLoadsAsync().ConfigureAwait(false);
        var fullPath = contextPath is null ? null : ResolvePath(contextPath);
        if (fullPath is not null)
        {
            _projects.EnsureOwnerLoading(fullPath);
            await _projects.WaitForLoadsAsync().ConfigureAwait(false);
        }

        return await _projects.GetTypeSystemAsync(fullPath, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _projects.Dispose();
}
