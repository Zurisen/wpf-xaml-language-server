using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using WpfXaml.Analysis.Framework;
using WpfXaml.Analysis.TypeSystem;
using WpfXaml.LanguageServer.Protocol;

namespace WpfXaml.LanguageServer.Workspace;

/// <summary>
/// Supplies the <see cref="XamlTypeSystem"/> for a XAML file: from the owning C# project once the solution
/// is loaded through MSBuildWorkspace, or from the framework reference assemblies until then (or when no
/// project is found). C# files are tracked through file-change notifications, so project types stay current
/// without a build.
/// </summary>
public sealed class ProjectSystem : IDisposable
{
    private static readonly string[] ProjectFileExtensions = [".sln", ".slnx", ".csproj", ".props", ".targets"];

    private readonly ServerLog _log;
    private readonly Lazy<XamlTypeSystem?> _framework;
    private readonly ConditionalWeakTable<Compilation, XamlTypeSystem> _typeSystems = new();
    private readonly Lock _gate = new();

    private MSBuildWorkspace? _workspace;
    private Solution? _solution;
    private IReadOnlyList<string> _loadTargets = [];
    private CancellationTokenSource _loadCancellation = new();

    public ProjectSystem(ServerLog log, int? frameworkVersion = null)
    {
        _log = log;
        _framework = new Lazy<XamlTypeSystem?>(() => CreateFrameworkTypeSystem(frameworkVersion));
    }

    /// <summary>Raised after the solution (re)loads or C# sources change: diagnostics should be refreshed.</summary>
    public event Action? Changed;

    public bool IsLoading { get; private set; }

    public Task LoadTask { get; private set; } = Task.CompletedTask;

    /// <summary>Optional progress callbacks (begin, report, end) supplied by the server.</summary>
    public Func<string, IProgress<string>>? ProgressFactory { get; set; }

    /// <summary>Finds what to load: an explicit path, otherwise solutions or projects under the root.</summary>
    public static IReadOnlyList<string> DiscoverLoadTargets(string? explicitPath, string? rootPath)
    {
        if (!string.IsNullOrEmpty(explicitPath))
        {
            var full = rootPath is not null ? Path.GetFullPath(explicitPath, rootPath) : Path.GetFullPath(explicitPath);
            return File.Exists(full) ? [full] : [];
        }

        if (rootPath is null || !Directory.Exists(rootPath))
        {
            return [];
        }

        // Breadth-first, shallow: the first directory level that has a solution wins; otherwise all projects at that level.
        var level = new List<string> { rootPath };
        for (var depth = 0; depth < 3 && level.Count > 0; depth++)
        {
            var solutions = level.SelectMany(d => SafeGetFiles(d, "*.slnx").Concat(SafeGetFiles(d, "*.sln"))).Order(StringComparer.OrdinalIgnoreCase).ToList();
            if (solutions.Count > 0)
            {
                // Prefer .slnx when both formats of the same solution exist.
                return [solutions.OrderBy(s => s.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) ? 0 : 1).First()];
            }

            var projects = level.SelectMany(d => SafeGetFiles(d, "*.csproj")).ToList();
            if (projects.Count > 0)
            {
                return projects;
            }

            level = level.SelectMany(SafeGetDirectories).Where(d => !IsIgnoredDirectory(d)).ToList();
        }

        return [];
    }

    public void StartLoading(IReadOnlyList<string> targets)
    {
        _loadTargets = targets;
        if (targets.Count == 0)
        {
            _log.Info("No solution or project found; using framework types only.");
            return;
        }

        Reload();
    }

    /// <summary>The type system for a XAML file. Falls back to framework types while loading or when no project owns the file.</summary>
    public async Task<XamlTypeSystem?> GetTypeSystemAsync(string? xamlPath, CancellationToken cancellationToken)
    {
        Solution? solution;
        lock (_gate)
        {
            solution = _solution;
        }

        if (solution is not null && xamlPath is not null && FindProject(solution, xamlPath) is { } project)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is not null)
            {
                return _typeSystems.GetValue(compilation, c => XamlTypeSystem.Create(c));
            }
        }

        return _framework.Value;
    }

    /// <summary>Resolves a documentation comment id (from completion data) against the type system of a file.</summary>
    public async Task<ISymbol?> FindSymbolAsync(string? xamlPath, string symbolId, CancellationToken cancellationToken)
    {
        var types = await GetTypeSystemAsync(xamlPath, cancellationToken).ConfigureAwait(false);
        return types is null ? null : DocumentationCommentId.GetFirstSymbolForDeclarationId(symbolId, types.Compilation);
    }

    public void OnFilesChanged(IReadOnlyList<(string Path, int Type)> changes)
    {
        if (changes.Any(c => ProjectFileExtensions.Contains(Path.GetExtension(c.Path), StringComparer.OrdinalIgnoreCase)))
        {
            _log.Info("Project files changed; reloading.");
            Reload();
            return;
        }

        var changed = false;
        lock (_gate)
        {
            if (_solution is null)
            {
                return;
            }

            var solution = _solution;
            foreach (var (path, type) in changes.Where(c => c.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
            {
                var ids = solution.GetDocumentIdsWithFilePath(path);
                if (type == FileChangeType.Deleted)
                {
                    solution = solution.RemoveDocuments(ids);
                    changed |= ids.Length > 0;
                    continue;
                }

                if (TryReadText(path) is not { } text)
                {
                    continue;
                }

                if (ids.Length > 0)
                {
                    foreach (var id in ids)
                    {
                        solution = solution.WithDocumentText(id, text, PreservationMode.PreserveIdentity);
                    }

                    changed = true;
                }
                else if (!IsInIgnoredDirectory(path))
                {
                    foreach (var project in FindProjects(solution, path))
                    {
                        solution = project.AddDocument(Path.GetFileName(path), text, filePath: path).Project.Solution;
                        changed = true;
                    }
                }
            }

            _solution = solution;
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    public void Dispose()
    {
        _loadCancellation.Cancel();
        _workspace?.Dispose();
    }

    private void Reload()
    {
        var targets = _loadTargets;
        if (targets.Count == 0)
        {
            return;
        }

        CancellationToken token;
        lock (_gate)
        {
            _loadCancellation.Cancel();
            _loadCancellation = new CancellationTokenSource();
            token = _loadCancellation.Token;
            IsLoading = true;
        }

        LoadTask = Task.Run(() => LoadAsync(targets, token), token);
    }

    private async Task LoadAsync(IReadOnlyList<string> targets, CancellationToken cancellationToken)
    {
        var progress = ProgressFactory?.Invoke("Loading projects");
        var stopwatch = Stopwatch.StartNew();
        try
        {
            // EnableWindowsTargeting lets net*-windows projects evaluate on Linux/macOS.
            // NuGetAudit would query package feeds (often private and unauthenticated here) on every load.
            var properties = new Dictionary<string, string>
            {
                ["EnableWindowsTargeting"] = "true",
                ["NuGetAudit"] = "false",
            };
            var workspace = MSBuildWorkspace.Create(properties);
            var loadMessages = 0;
            workspace.RegisterWorkspaceFailedHandler(e =>
            {
                // Mostly MSBuild warnings (package compatibility, architecture mismatch); projects still load.
                Interlocked.Increment(ref loadMessages);
                _log.Debug($"MSBuild: {e.Diagnostic.Message}");
            });

            var msbuildProgress = new Progress<ProjectLoadProgress>(p =>
            {
                if (p.Operation == ProjectLoadOperation.Resolve)
                {
                    progress?.Report(Path.GetFileNameWithoutExtension(p.FilePath));
                }
            });

            foreach (var target in targets)
            {
                _log.Info($"Loading {target}");
                if (target.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || target.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
                {
                    await workspace.OpenSolutionAsync(target, msbuildProgress, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await workspace.OpenProjectAsync(target, msbuildProgress, cancellationToken).ConfigureAwait(false);
                }
            }

            MSBuildWorkspace? previous;
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                previous = _workspace;
                _workspace = workspace;
                _solution = workspace.CurrentSolution;
                IsLoading = false;
            }

            previous?.Dispose();
            var count = workspace.CurrentSolution.Projects.Count(p => p.Language == LanguageNames.CSharp);
            var messages = loadMessages > 0 ? $" ({loadMessages} MSBuild messages; --log-level debug shows them)" : string.Empty;
            _log.Info($"Loaded {count} C# projects in {stopwatch.Elapsed.TotalSeconds:F1}s{messages}.");
            Changed?.Invoke();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            lock (_gate)
            {
                IsLoading = false;
            }

            _log.Error("Failed to load projects; using framework types only", e);
        }
        finally
        {
            (progress as IDisposable)?.Dispose();
        }
    }

    /// <summary>The C# project whose directory most specifically contains the file (XAML items are not Roslyn documents).</summary>
    private static Project? FindProject(Solution solution, string filePath) => FindProjects(solution, filePath).FirstOrDefault();

    private static IEnumerable<Project> FindProjects(Solution solution, string filePath)
    {
        var candidates = solution.Projects
            .Where(p => p.Language == LanguageNames.CSharp && p.FilePath is not null)
            .Select(p => (Project: p, Directory: Path.GetDirectoryName(p.FilePath!)! + Path.DirectorySeparatorChar))
            .Where(p => filePath.StartsWith(p.Directory, DocumentUris.PathComparison))
            .ToList();
        if (candidates.Count == 0)
        {
            return [];
        }

        // Multi-targeted projects appear once per target framework; keep only the deepest directory, first framework.
        var deepest = candidates.Max(c => c.Directory.Length);
        return candidates
            .Where(c => c.Directory.Length == deepest)
            .GroupBy(c => c.Project.FilePath, DocumentUris.PathComparer)
            .Select(g => g.OrderBy(c => c.Project.Name, StringComparer.Ordinal).First().Project);
    }

    private XamlTypeSystem? CreateFrameworkTypeSystem(int? frameworkVersion)
    {
        var references = FrameworkReferences.FindWpfReferencePaths(frameworkVersion);
        if (references.Count == 0)
        {
            _log.Warning("No WPF targeting pack (Microsoft.WindowsDesktop.App.Ref) found; framework types are unavailable until a project loads.");
            return null;
        }

        _log.Info($"Framework references: {Path.GetDirectoryName(references[^1])}");
        return XamlTypeSystem.Create(FrameworkReferences.CreateFrameworkCompilation(references), isFrameworkOnly: true);
    }

    private static SourceText? TryReadText(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return SourceText.From(stream);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsIgnoredDirectory(string directory) =>
        Path.GetFileName(directory) is "bin" or "obj" or ".git" or ".vs" or "node_modules" or "packages" or "artifacts";

    private static bool IsInIgnoredDirectory(string path) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is "bin" or "obj");

    private static string[] SafeGetFiles(string directory, string pattern)
    {
        try
        {
            return Directory.GetFiles(directory, pattern);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string[] SafeGetDirectories(string directory)
    {
        try
        {
            return Directory.GetDirectories(directory);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
