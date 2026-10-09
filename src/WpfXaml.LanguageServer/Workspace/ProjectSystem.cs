using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using WpfXaml.Analysis.Framework;
using WpfXaml.Analysis.TypeSystem;
using WpfXaml.LanguageServer.Protocol;

namespace WpfXaml.LanguageServer.Workspace;

public enum XamlFileStatus
{
    /// <summary>The file is a Page/ApplicationDefinition item of a loaded project.</summary>
    InProject,

    /// <summary>The file sits in a loaded project's folder but the project excludes it from its build.</summary>
    ExcludedFromProject,

    /// <summary>The owning project is still loading; framework types are used meanwhile.</summary>
    ProjectLoading,

    /// <summary>No project owns the file; framework types are used.</summary>
    NoProject,
}

/// <param name="Types">The type system to analyze the file with (null when no WPF targeting pack is available).</param>
/// <param name="ProjectFilePath">The owning (or excluding) project, when known.</param>
public sealed record XamlFileContext(XamlTypeSystem? Types, XamlFileStatus Status, string? ProjectFilePath);

/// <summary>
/// Supplies the <see cref="XamlTypeSystem"/> for a XAML file.
/// <list type="bullet">
/// <item>The solution (or projects) found under the workspace root loads in the background through MSBuildWorkspace.</item>
/// <item>A XAML file's project is known exactly: the design-time build exposes each project's Page items
/// (see WpfXamlLs.DesignTime.targets). Files whose nearest project isn't loaded yet trigger loading that project.</item>
/// <item>Until a project is available, framework reference assemblies are used.</item>
/// <item>Project references whose built DLL is up to date are read from the DLL instead of compiled from source.</item>
/// <item>C# changes arrive as file notifications and are applied to the in-memory solution.</item>
/// </list>
/// </summary>
public sealed class ProjectSystem : IDisposable
{
    private const string DesignTimeTargetsFile = "WpfXamlLs.DesignTime.targets";
    private static readonly string[] ProjectFileExtensions = [".sln", ".slnx", ".csproj", ".props", ".targets"];
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string?> NearestProjectCache = new(DocumentUris.PathComparer);

    private readonly ServerLog _log;
    private readonly bool _loadOnDemand;
    private readonly bool _waitForGenerators;
    private readonly Lazy<XamlTypeSystem?> _framework;
    private readonly ConditionalWeakTable<Compilation, XamlTypeSystem> _typeSystems = new();
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    // All guarded by _gate.
    private MSBuildWorkspace? _workspace;
    private Solution? _loadedSolution;
    private readonly Dictionary<string, SourceText?> _changedSources = new(DocumentUris.PathComparer);
    private readonly HashSet<string> _onDemandProjects = new(DocumentUris.PathComparer);
    private Snapshot? _snapshot;
    private IReadOnlyList<string> _loadTargets = [];
    private CancellationTokenSource _lifetime = new();
    private int _pendingLoads;
    private Task _loads = Task.CompletedTask;

    /// <param name="loadOnDemand">Load the project of an opened XAML file when it isn't in the loaded solution.</param>
    /// <param name="waitForGenerators">Always use the complete compilation (for batch checks, where latency doesn't matter).</param>
    public ProjectSystem(ServerLog log, int? frameworkVersion = null, bool loadOnDemand = true, bool waitForGenerators = false)
    {
        _log = log;
        _loadOnDemand = loadOnDemand;
        _waitForGenerators = waitForGenerators;
        _framework = new Lazy<XamlTypeSystem?>(() => CreateFrameworkTypeSystem(frameworkVersion));
    }

    /// <summary>Raised after projects load or C# sources change: diagnostics should be refreshed.</summary>
    public event Action? Changed;

    public bool IsLoading => Volatile.Read(ref _pendingLoads) > 0;

    /// <summary>Optional progress reporting supplied by the server (dispose to end).</summary>
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

            level = level.SelectMany(SafeGetDirectories).Where(d => !IsIgnoredDirectory(Path.GetFileName(d))).ToList();
        }

        return [];
    }

    /// <summary>The project file in the nearest directory at or above the file (what owns it under SDK globbing).</summary>
    public static string? FindNearestProjectFile(string filePath) =>
        Path.GetDirectoryName(filePath) is { } directory ? NearestProjectInDirectory(directory) : null;

    private static string? NearestProjectInDirectory(string directory) =>
        NearestProjectCache.GetOrAdd(directory, static d =>
        {
            var projects = SafeGetFiles(d, "*.csproj");
            if (projects.Length > 0)
            {
                return projects.Order(StringComparer.OrdinalIgnoreCase).First();
            }

            return Path.GetDirectoryName(d) is { } parent ? NearestProjectInDirectory(parent) : null;
        });

    public void StartLoading(IReadOnlyList<string> targets)
    {
        _loadTargets = targets;
        if (targets.Count == 0)
        {
            _log.Info("No solution or project found under the root; projects will load when their XAML files are opened.");
            return;
        }

        Reload();
    }

    /// <summary>Completes when no project loads are pending.</summary>
    public async Task WaitForLoadsAsync()
    {
        while (true)
        {
            Task loads;
            lock (_gate)
            {
                loads = _loads;
            }

            await loads.ConfigureAwait(false);
            lock (_gate)
            {
                if (ReferenceEquals(loads, _loads) && _pendingLoads == 0)
                {
                    return;
                }
            }
        }
    }

    public async Task<XamlFileContext> GetContextAsync(string? xamlPath, CancellationToken cancellationToken)
    {
        if (xamlPath is null)
        {
            return new XamlFileContext(_framework.Value, XamlFileStatus.NoProject, null);
        }

        var (project, status, projectFile) = ResolveOwner(xamlPath);
        var types = project is null ? _framework.Value : await GetTypesAsync(project, cancellationToken).ConfigureAwait(false);
        return new XamlFileContext(types, status, projectFile);
    }

    /// <summary>
    /// Finds the project a XAML file belongs to without compiling anything; starts loading the owning project
    /// when it isn't loaded yet.
    /// </summary>
    public XamlFileStatus EnsureOwnerLoading(string xamlPath) => ResolveOwner(xamlPath).Status;

    private (Project? Project, XamlFileStatus Status, string? ProjectFile) ResolveOwner(string xamlPath)
    {
        var snapshot = GetSnapshot();
        if (snapshot is not null && snapshot.XamlOwners.TryGetValue(xamlPath, out var ownerId))
        {
            var owner = snapshot.Effective.GetProject(ownerId)!;
            return (owner, XamlFileStatus.InProject, owner.FilePath);
        }

        var projectFile = FindNearestProjectFile(xamlPath);
        if (projectFile is not null && snapshot is not null && snapshot.ProjectsByPath.TryGetValue(projectFile, out var projectId))
        {
            // If the design-time build reported XAML items for this project, their absence means the project
            // excludes this file. Projects without any (e.g. non-WPF, or the targets hook didn't run) fall back
            // to directory ownership.
            var status = snapshot.ProjectsWithXamlItems.Contains(projectId) ? XamlFileStatus.ExcludedFromProject : XamlFileStatus.InProject;
            return (snapshot.Effective.GetProject(projectId), status, projectFile);
        }

        if (projectFile is not null && _loadOnDemand)
        {
            RequestProjectLoad(projectFile);
            return (null, XamlFileStatus.ProjectLoading, projectFile);
        }

        return (null, IsLoading ? XamlFileStatus.ProjectLoading : XamlFileStatus.NoProject, projectFile);
    }

    public async Task<XamlTypeSystem?> GetTypeSystemAsync(string? xamlPath, CancellationToken cancellationToken) =>
        (await GetContextAsync(xamlPath, cancellationToken).ConfigureAwait(false)).Types;

    /// <summary>Resolves a documentation comment id (from completion data) against the type system of a file.</summary>
    public async Task<ISymbol?> FindSymbolAsync(string? xamlPath, string symbolId, CancellationToken cancellationToken)
    {
        var types = await GetTypeSystemAsync(xamlPath, cancellationToken).ConfigureAwait(false);
        return types is null ? null : DocumentationCommentId.GetFirstSymbolForDeclarationId(symbolId, types.Compilation);
    }

    // ------------------------------------------------------------------ resource lookup support

    /// <summary>Every XAML file built by a loaded project.</summary>
    public IReadOnlyCollection<string> GetAllXamlFiles() => GetSnapshot()?.XamlOwners.Keys ?? (IReadOnlyCollection<string>)[];
    /// <summary>The directory of the project that owns (or excludes) a file.</summary>
    public string? GetProjectDirectory(string filePath) =>
        ResolveOwner(filePath).ProjectFile is { } projectFile ? Path.GetDirectoryName(projectFile) : null;

    /// <summary>The directory of the loaded project producing an assembly (for <c>/Assembly;component/...</c> URIs).</summary>
    public string? FindProjectDirectoryByAssemblyName(string assemblyName)
    {
        var project = GetSnapshot()?.Source.Projects.FirstOrDefault(p =>
            p.Language == LanguageNames.CSharp && string.Equals(p.AssemblyName, assemblyName, StringComparison.OrdinalIgnoreCase));
        return project?.FilePath is { } path ? Path.GetDirectoryName(path) : null;
    }

    /// <summary>
    /// The App.xaml files whose resources a file can see: its own project's, or for a library, those of every
    /// application project that references it. Null when unknown (no project, or no application references it).
    /// </summary>
    public IReadOnlyList<string>? GetApplicationDefinitions(string filePath)
    {
        if (GetSnapshot() is not { } snapshot || ResolveOwner(filePath) is not { Project: { } owner })
        {
            return null;
        }

        if (FindApplicationXaml(snapshot, owner) is { } own)
        {
            return [own];
        }

        var applications = snapshot.Source.GetProjectDependencyGraph()
            .GetProjectsThatTransitivelyDependOnThisProject(owner.Id)
            .Select(id => snapshot.Source.GetProject(id))
            .OfType<Project>()
            .Select(p => FindApplicationXaml(snapshot, p))
            .OfType<string>()
            .Distinct(DocumentUris.PathComparer)
            .ToList();
        return applications.Count > 0 ? applications : null;
    }

    private static string? FindApplicationXaml(Snapshot snapshot, Project project) =>
        project.FilePath is null ? null : snapshot.ApplicationXaml.GetOrAdd(project.FilePath, _ =>
            snapshot.XamlOwners
                .Where(o => snapshot.Source.GetProject(o.Value)?.FilePath is { } owner && string.Equals(owner, project.FilePath, DocumentUris.PathComparison))
                .Select(o => o.Key)
                .OrderBy(p => p, DocumentUris.PathComparer)
                .FirstOrDefault(p => ReadRootElementName(p) == "Application"));

    /// <summary>The local name of a XAML file's root element, read from the start of the file.</summary>
    private static string? ReadRootElementName(string path)
    {
        try
        {
            using var reader = new StreamReader(path);
            var buffer = new char[4096];
            var length = reader.ReadBlock(buffer, 0, buffer.Length);
            var match = System.Text.RegularExpressions.Regex.Match(
                new string(buffer, 0, length), @"^\s*(?:(?:<\?.*?\?>|<!--.*?-->)\s*)*<(?:[\w.-]+:)?([\w.-]+)", System.Text.RegularExpressions.RegexOptions.Singleline);
            return match.Success ? match.Groups[1].Value : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
    /// <summary>
    /// For a symbol read from a built project assembly, the same symbol in that project's source
    /// (so go to definition can land in the code). Null when the symbol is not from a loaded project.
    /// </summary>
    public async Task<ISymbol?> FindSourceSymbolAsync(ISymbol symbol, CancellationToken cancellationToken)
    {
        if (symbol.HasSourceLocation() || GetSnapshot() is not { } snapshot || symbol.ContainingAssembly is not { } assembly
            || symbol.GetDocumentationCommentId() is not { } id)
        {
            return null;
        }

        var project = snapshot.Effective.Projects.FirstOrDefault(p => p.Language == LanguageNames.CSharp && p.AssemblyName == assembly.Name);
        var compilation = project is null ? null : await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
        return compilation is null ? null : DocumentationCommentId.GetFirstSymbolForDeclarationId(id, compilation);
    }

    public void OnFilesChanged(IReadOnlyList<(string Path, int Type)> changes)
    {
        if (changes.Any(c => ProjectFileExtensions.Contains(Path.GetExtension(c.Path), StringComparer.OrdinalIgnoreCase)))
        {
            _log.Info("Project files changed; reloading.");
            Reload();
            return;
        }

        var sources = changes.Where(c => c.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && !IsInIgnoredDirectory(c.Path)).ToList();
        if (sources.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            foreach (var (path, type) in sources)
            {
                _changedSources[path] = type == FileChangeType.Deleted ? null : TryReadText(path);
            }

            _snapshot = null;
        }

        Changed?.Invoke();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _lifetime.Cancel();
            _workspace?.Dispose();
            _workspace = null;
        }
    }

    // ------------------------------------------------------------------ loading

    private void Reload()
    {
        NearestProjectCache.Clear();
        lock (_gate)
        {
            _lifetime.Cancel();
            _lifetime = new CancellationTokenSource();
            _workspace?.Dispose();
            _workspace = null;
            _loadedSolution = null;
            _changedSources.Clear();
            _snapshot = null;
        }

        var targets = _loadTargets.Concat(_onDemandProjects).ToList();
        if (targets.Count > 0)
        {
            EnqueueLoad(targets, "Loading projects");
        }
    }

    private void RequestProjectLoad(string projectFile)
    {
        lock (_gate)
        {
            if (!_onDemandProjects.Add(projectFile))
            {
                return;
            }
        }

        _log.Info($"Loading {Path.GetFileName(projectFile)} (owns an opened XAML file but isn't in the loaded solution).");
        EnqueueLoad([projectFile], $"Loading {Path.GetFileNameWithoutExtension(projectFile)}");
    }

    private void EnqueueLoad(IReadOnlyList<string> targets, string title)
    {
        CancellationToken token;
        lock (_gate)
        {
            token = _lifetime.Token;
            _pendingLoads++;
            var previous = _loads;
            _loads = Task.Run(async () =>
            {
                await previous.ConfigureAwait(false);
                await LoadAsync(targets, title, token).ConfigureAwait(false);
            });
        }
    }

    private async Task LoadAsync(IReadOnlyList<string> targets, string title, CancellationToken cancellationToken)
    {
        var progress = ProgressFactory?.Invoke(title);
        var stopwatch = Stopwatch.StartNew();
        await _loadLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var workspace = GetOrCreateWorkspace();
            var loadMessages = 0;
            using var _ = workspace.RegisterWorkspaceFailedHandler(e =>
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
                cancellationToken.ThrowIfCancellationRequested();
                if (workspace.CurrentSolution.Projects.Any(p => string.Equals(p.FilePath, target, DocumentUris.PathComparison)))
                {
                    continue;
                }

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

            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _loadedSolution = workspace.CurrentSolution;
                _snapshot = null;
            }

            // Build the snapshot now (it checks every source file's timestamp) rather than on the first request.
            GetSnapshot();

            var count = workspace.CurrentSolution.Projects.Count(p => p.Language == LanguageNames.CSharp);
            var messages = loadMessages > 0 ? $" ({loadMessages} MSBuild messages; --log-level debug shows them)" : string.Empty;
            _log.Info($"Loaded {count} C# projects in {stopwatch.Elapsed.TotalSeconds:F1}s{messages}.");
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // Superseded by a reload (its workspace may already be disposed).
        }
        catch (Exception e)
        {
            _log.Error($"Failed to load {string.Join(", ", targets.Select(Path.GetFileName))}; using framework types for its files", e);
        }
        finally
        {
            _loadLock.Release();
            lock (_gate)
            {
                _pendingLoads--;
            }

            (progress as IDisposable)?.Dispose();
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            Changed?.Invoke();
        }
    }

    private MSBuildWorkspace GetOrCreateWorkspace()
    {
        lock (_gate)
        {
            if (_workspace is not null)
            {
                return _workspace;
            }

            // EnableWindowsTargeting lets net*-windows projects evaluate on Linux/macOS.
            // NuGetAudit would query package feeds (often private and unauthenticated here) on every load.
            var properties = new Dictionary<string, string>
            {
                ["EnableWindowsTargeting"] = "true",
                ["NuGetAudit"] = "false",
            };

            var targets = Path.Combine(AppContext.BaseDirectory, DesignTimeTargetsFile);
            if (File.Exists(targets))
            {
                properties["CustomAfterMicrosoftCommonTargets"] = targets;
            }
            else
            {
                _log.Warning($"{DesignTimeTargetsFile} not found next to the server; XAML file ownership falls back to directories.");
            }

            _workspace = MSBuildWorkspace.Create(properties);
            return _workspace;
        }
    }

    // ------------------------------------------------------------------ snapshots

    /// <summary>The loaded solution plus C# changes, with ownership indexes and built-reference substitution.</summary>
    private sealed class Snapshot
    {
        /// <summary>The loaded solution plus C# changes, with project references intact (for dependency queries).</summary>
        public required Solution Source { get; init; }

        public required Solution Effective { get; init; }

        /// <summary>Project file → its App.xaml (null when it has none). Filled lazily.</summary>
        public System.Collections.Concurrent.ConcurrentDictionary<string, string?> ApplicationXaml { get; } = new(DocumentUris.PathComparer);

        public required Dictionary<string, ProjectId> XamlOwners { get; init; }

        public required HashSet<ProjectId> ProjectsWithXamlItems { get; init; }

        public required Dictionary<string, ProjectId> ProjectsByPath { get; init; }
    }

    private Snapshot? GetSnapshot()
    {
        lock (_gate)
        {
            if (_snapshot is not null || _loadedSolution is null)
            {
                return _snapshot;
            }

            var stopwatch = Stopwatch.StartNew();
            var source = ApplySourceChanges(_loadedSolution);
            var owners = new Dictionary<string, ProjectId>(DocumentUris.PathComparer);
            var withXaml = new HashSet<ProjectId>();
            var byPath = new Dictionary<string, ProjectId>(DocumentUris.PathComparer);

            // Multi-targeted projects appear once per framework; the first (by name) wins consistently.
            foreach (var project in source.Projects.Where(p => p.Language == LanguageNames.CSharp).OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                if (project.FilePath is not null)
                {
                    byPath.TryAdd(project.FilePath, project.Id);
                }

                foreach (var document in project.AdditionalDocuments)
                {
                    if (document.FilePath is { } path && path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                    {
                        owners.TryAdd(path, project.Id);
                        withXaml.Add(project.Id);
                    }
                }
            }

            Snapshot? snapshot = null;
            var effective = BuiltReferences.Substitute(source, id => snapshot?.Effective.GetProject(id)?.GetCompilationAsync().GetAwaiter().GetResult(), _log.Debug, out var built);
            snapshot = new Snapshot { Source = source, Effective = effective, XamlOwners = owners, ProjectsWithXamlItems = withXaml, ProjectsByPath = byPath };
            _snapshot = snapshot;
            _log.Debug($"Snapshot: {byPath.Count} projects, {owners.Count} XAML files, {built} built references ({stopwatch.ElapsedMilliseconds} ms).");
            return snapshot;
        }
    }

    /// <summary>Applies C# files changed on disk since the load (edited, created, deleted).</summary>
    private Solution ApplySourceChanges(Solution solution)
    {
        foreach (var (path, text) in _changedSources)
        {
            var ids = solution.GetDocumentIdsWithFilePath(path);
            if (text is null)
            {
                solution = solution.RemoveDocuments(ids);
            }
            else if (ids.Length > 0)
            {
                foreach (var id in ids)
                {
                    solution = solution.WithDocumentText(id, text, PreservationMode.PreserveIdentity);
                }
            }
            else if (FindNearestProjectFile(path) is { } projectFile)
            {
                foreach (var project in solution.Projects.Where(p => string.Equals(p.FilePath, projectFile, DocumentUris.PathComparison)).ToList())
                {
                    solution = project.AddDocument(Path.GetFileName(path), text, filePath: path).Project.Solution;
                }
            }
        }

        return solution;
    }

    private async Task<XamlTypeSystem?> GetTypesAsync(Project project, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var compilation = await GetCompilationAsync(project, cancellationToken).ConfigureAwait(false);
        if (compilation is null)
        {
            return _framework.Value;
        }

        var compiled = stopwatch.Elapsed;
        var types = _typeSystems.GetValue(compilation, c => XamlTypeSystem.Create(c));
        if (stopwatch.ElapsedMilliseconds > 500)
        {
            _log.Debug($"Type system for {project.Name}: compilation {compiled.TotalMilliseconds:F0} ms, total {stopwatch.ElapsedMilliseconds} ms.");
        }
        if (types.GetNamespace(Language.Syntax.XmlNamespaces.WpfPresentation) is not null)
        {
            return types;
        }

        // Loaded, but without WPF references: not restored, or a legacy .NET Framework project whose
        // reference assemblies aren't installed. Framework types (with silence about the rest) beat that.
        if (project.FilePath is { } path && _projectsWithoutWpf.TryAdd(path, 0))
        {
            _log.Warning($"{Path.GetFileName(path)} has no WPF references after loading (not restored, or a .NET Framework project without reference assemblies); using framework types for its XAML.");
        }

        return _framework.Value;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _projectsWithoutWpf = new(DocumentUris.PathComparer);
    private readonly ConditionalWeakTable<Project, Task<Compilation?>> _fullCompilations = new();
    private readonly ConditionalWeakTable<Project, Task<Compilation?>> _quickCompilations = new();

    /// <summary>
    /// The project's compilation. Running source generators is most of the cost of compiling a large project
    /// (8 of 10 seconds on the motivating solution), and they rarely produce anything XAML refers to. So unless
    /// <see cref="_waitForGenerators"/> is set, a generator-free compilation answers first, the full one runs in
    /// the background, and <see cref="Changed"/> fires when it is ready so diagnostics are recomputed with it.
    /// </summary>
    private async Task<Compilation?> GetCompilationAsync(Project project, CancellationToken cancellationToken)
    {
        var full = _fullCompilations.GetValue(project, p => Task.Run(() => p.GetCompilationAsync(CancellationToken.None)));
        if (full.IsCompletedSuccessfully || _waitForGenerators || !project.AnalyzerReferences.Any())
        {
            return await full.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        var quick = _quickCompilations.GetValue(project, p =>
        {
            _ = full.ContinueWith(_ => Changed?.Invoke(), CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
            return Task.Run(() => p.WithAnalyzerReferences([]).GetCompilationAsync(CancellationToken.None));
        });

        // Whichever is ready first: on small projects the full compilation can win.
        await Task.WhenAny(full, quick).WaitAsync(cancellationToken).ConfigureAwait(false);
        return full.IsCompletedSuccessfully ? full.Result : await quick.ConfigureAwait(false);
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

    private static bool IsIgnoredDirectory(string name) =>
        name is "bin" or "obj" or ".git" or ".vs" or "node_modules" or "packages" or "artifacts";

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
