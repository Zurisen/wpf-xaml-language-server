using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.VisualStudio.Threading;
using StreamJsonRpc;
using WpfXaml.Analysis.Features;
using WpfXaml.Analysis.Features.Completion;
using WpfXaml.LanguageServer.Protocol;
using WpfXaml.LanguageServer.Workspace;

namespace WpfXaml.LanguageServer;

/// <summary>
/// The LSP endpoint. Public methods are JSON-RPC handlers; everything else is internal.
/// Messages are dispatched in order (document changes must apply in sequence); handlers that do real work
/// continue off the dispatch context so requests can run concurrently.
/// </summary>
public sealed class XamlLanguageServer : IDisposable
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly string[] CompletionTriggers = ["<", " ", ":", ".", "\"", "'", "{", "=", ",", "/"];

    private readonly JsonRpc _rpc;
    private readonly ServerLog _log;
    private readonly DocumentStore _documents = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pendingDiagnostics = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private ProjectSystem _projects;
    private ServerSettings _settings = new();
    private string? _rootPath;
    private bool _snippetSupport;
    private bool _watchedFilesDynamicRegistration;
    private bool _workDoneProgress;

    private XamlLanguageServer(Stream input, Stream output, ServerLog log)
    {
        _log = log;
        _projects = new ProjectSystem(log);
        var formatter = new SystemTextJsonFormatter { JsonSerializerOptions = JsonOptions };
        _rpc = new JsonRpc(new HeaderDelimitedMessageHandler(output, input, formatter))
        {
            SynchronizationContext = new NonConcurrentSynchronizationContext(sticky: false),
        };
        _rpc.AddLocalRpcTarget(this, new JsonRpcTargetOptions { NotifyClientOfEvents = false });
    }

    internal static XamlLanguageServer Create(Stream input, Stream output, ServerLog log) => new(input, output, log);

    /// <summary>Completes when the client sends <c>exit</c> or the connection closes.</summary>
    internal Task Completion => Task.WhenAny(_exit.Task, _rpc.Completion);

    internal bool ShutdownRequested { get; private set; }

    internal void Start() => _rpc.StartListening();

    // ------------------------------------------------------------------ lifecycle

    [JsonRpcMethod("initialize", UseSingleObjectParameterDeserialization = true)]
    public InitializeResult Initialize(InitializeParams parameters)
    {
        if (parameters.Capabilities is { } capabilities)
        {
            _snippetSupport = GetBool(capabilities, "textDocument", "completion", "completionItem", "snippetSupport");
            _watchedFilesDynamicRegistration = GetBool(capabilities, "workspace", "didChangeWatchedFiles", "dynamicRegistration");
            _workDoneProgress = GetBool(capabilities, "window", "workDoneProgress");
        }

        if (parameters.InitializationOptions is { ValueKind: JsonValueKind.Object } options)
        {
            _settings = options.Deserialize<ServerSettings>(JsonOptions) ?? new ServerSettings();
        }

        _rootPath = (parameters.RootUri ?? parameters.WorkspaceFolders?.FirstOrDefault()?.Uri) is { } rootUri
            ? DocumentUris.ToPath(rootUri)
            : parameters.RootPath;

        _projects.Dispose();
        _projects = new ProjectSystem(_log, _settings.FrameworkVersion) { ProgressFactory = CreateProgress };
        _projects.Changed += RefreshAllDiagnostics;
        _log.ClientSink = ForwardLog;
        _log.Info($"wpf-xaml-ls {Version} starting; root: {_rootPath ?? "(none)"}");

        return new InitializeResult(
            new ServerCapabilities
            {
                TextDocumentSync = new TextDocumentSyncOptions(OpenClose: true, Change: TextDocumentSyncKind.Incremental, Save: true),
                CompletionProvider = new CompletionOptions(CompletionTriggers, ResolveProvider: true),
                HoverProvider = true,
                DefinitionProvider = true,
            },
            new ServerInfo("wpf-xaml-ls", Version));
    }

    [JsonRpcMethod("initialized", UseSingleObjectParameterDeserialization = true)]
    public async Task InitializedAsync(JsonElement? parameters)
    {
        // Warm the framework type system so the first request is fast.
        _ = Task.Run(() => _projects.GetTypeSystemAsync(null, CancellationToken.None));

        if (_settings.LoadProjects)
        {
            _projects.StartLoading(ProjectSystem.DiscoverLoadTargets(_settings.Solution, _rootPath));
        }

        if (_watchedFilesDynamicRegistration)
        {
            string[] globs = ["**/*.cs", "**/*.csproj", "**/*.props", "**/*.targets", "**/*.sln", "**/*.slnx"];
            var registration = new Registration(
                "wpf-xaml-ls-watch",
                "workspace/didChangeWatchedFiles",
                new DidChangeWatchedFilesRegistrationOptions(globs.Select(g => new LspFileSystemWatcher(g)).ToArray()));
            try
            {
                await _rpc.InvokeWithParameterObjectAsync("client/registerCapability", new RegistrationParams([registration])).ConfigureAwait(false);
            }
            catch (Exception e) when (e is RemoteInvocationException or ConnectionLostException)
            {
                _log.Warning($"Could not register file watchers: {e.Message}");
            }
        }
    }

    [JsonRpcMethod("shutdown")]
    public object? Shutdown()
    {
        ShutdownRequested = true;
        return null;
    }

    [JsonRpcMethod("exit")]
    public void Exit() => _exit.TrySetResult();

    // ------------------------------------------------------------------ documents

    [JsonRpcMethod("textDocument/didOpen", UseSingleObjectParameterDeserialization = true)]
    public void DidOpen(DidOpenTextDocumentParams parameters)
    {
        var item = parameters.TextDocument;
        ScheduleDiagnostics(_documents.Open(item.Uri, item.Version, item.Text), delayMilliseconds: 0);
    }

    [JsonRpcMethod("textDocument/didChange", UseSingleObjectParameterDeserialization = true)]
    public void DidChange(DidChangeTextDocumentParams parameters)
    {
        var snapshot = _documents.Change(parameters.TextDocument.Uri, parameters.TextDocument.Version, parameters.ContentChanges);
        if (snapshot is not null)
        {
            ScheduleDiagnostics(snapshot, delayMilliseconds: 250);
        }
    }

    [JsonRpcMethod("textDocument/didClose", UseSingleObjectParameterDeserialization = true)]
    public void DidClose(DidCloseTextDocumentParams parameters)
    {
        var uri = parameters.TextDocument.Uri;
        _documents.Close(uri);
        if (_pendingDiagnostics.TryRemove(uri, out var pending))
        {
            pending.Cancel();
        }

        _ = NotifyAsync("textDocument/publishDiagnostics", new PublishDiagnosticsParams(uri, null, []));
    }

    [JsonRpcMethod("textDocument/didSave", UseSingleObjectParameterDeserialization = true)]
    public void DidSave(DidSaveTextDocumentParams parameters)
    {
    }

    [JsonRpcMethod("workspace/didChangeWatchedFiles", UseSingleObjectParameterDeserialization = true)]
    public void DidChangeWatchedFiles(DidChangeWatchedFilesParams parameters)
    {
        var changes = parameters.Changes
            .Select(c => (Path: DocumentUris.ToPath(c.Uri), c.Type))
            .Where(c => c.Path is not null)
            .Select(c => (c.Path!, c.Type))
            .ToList();
        _projects.OnFilesChanged(changes);
    }

    // ------------------------------------------------------------------ language features

    [JsonRpcMethod("textDocument/completion", UseSingleObjectParameterDeserialization = true)]
    public async Task<CompletionList?> CompletionAsync(TextDocumentPositionParams parameters, CancellationToken cancellationToken)
    {
        if (_documents.Get(parameters.TextDocument.Uri) is not { } document)
        {
            return null;
        }

        var types = await _projects.GetTypeSystemAsync(document.Path, cancellationToken).ConfigureAwait(false);
        if (types is null)
        {
            return null;
        }

        var lines = document.Syntax.Lines;
        var list = CompletionService.GetCompletions(document.GetSemanticModel(types), LspConverters.ToOffset(lines, parameters.Position));
        return list is null ? null : LspConverters.ToCompletionList(document.Uri, lines, list, _snippetSupport);
    }

    [JsonRpcMethod("completionItem/resolve", UseSingleObjectParameterDeserialization = true)]
    public async Task<CompletionItem> ResolveCompletionAsync(CompletionItem item, CancellationToken cancellationToken)
    {
        if (item.Documentation is not null || item.Data is not { SymbolId: { } symbolId } data)
        {
            return item;
        }

        var symbol = await _projects.FindSymbolAsync(DocumentUris.ToPath(data.Uri), symbolId, cancellationToken).ConfigureAwait(false);
        var summary = symbol is null ? null : DocumentationFormatter.GetSummary(symbol);
        return summary is null ? item : item with { Documentation = MarkupContent.Markdown(summary) };
    }

    [JsonRpcMethod("textDocument/hover", UseSingleObjectParameterDeserialization = true)]
    public async Task<Hover?> HoverAsync(TextDocumentPositionParams parameters, CancellationToken cancellationToken)
    {
        if (_documents.Get(parameters.TextDocument.Uri) is not { } document
            || await _projects.GetTypeSystemAsync(document.Path, cancellationToken).ConfigureAwait(false) is not { } types)
        {
            return null;
        }

        var lines = document.Syntax.Lines;
        var hover = HoverService.GetHover(document.GetSemanticModel(types), LspConverters.ToOffset(lines, parameters.Position));
        return hover is null ? null : new Hover(MarkupContent.Markdown(hover.Markdown), LspConverters.ToRange(lines, hover.Span));
    }

    [JsonRpcMethod("textDocument/definition", UseSingleObjectParameterDeserialization = true)]
    public async Task<Location[]?> DefinitionAsync(TextDocumentPositionParams parameters, CancellationToken cancellationToken)
    {
        if (_documents.Get(parameters.TextDocument.Uri) is not { } document
            || await _projects.GetTypeSystemAsync(document.Path, cancellationToken).ConfigureAwait(false) is not { } types)
        {
            return null;
        }

        var offset = LspConverters.ToOffset(document.Syntax.Lines, parameters.Position);
        return DefinitionService.GetDefinitions(document.GetSemanticModel(types), offset)
            .Select(l => LspConverters.ToLocation(document.Uri, l))
            .ToArray();
    }

    // ------------------------------------------------------------------ diagnostics

    private void RefreshAllDiagnostics()
    {
        foreach (var document in _documents.All)
        {
            ScheduleDiagnostics(document, delayMilliseconds: 0);
        }
    }

    private void ScheduleDiagnostics(DocumentSnapshot document, int delayMilliseconds)
    {
        var cancellation = new CancellationTokenSource();
        _pendingDiagnostics.AddOrUpdate(document.Uri, cancellation, (_, previous) =>
        {
            previous.Cancel();
            return cancellation;
        });

        _ = Task.Run(async () =>
        {
            try
            {
                if (delayMilliseconds > 0)
                {
                    await Task.Delay(delayMilliseconds, cancellation.Token).ConfigureAwait(false);
                }

                await PublishDiagnosticsAsync(document, cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                _log.Error($"Diagnostics failed for {document.Uri}", e);
            }
        });
    }

    private async Task PublishDiagnosticsAsync(DocumentSnapshot document, CancellationToken cancellationToken)
    {
        var types = await _projects.GetTypeSystemAsync(document.Path, cancellationToken).ConfigureAwait(false);
        var diagnostics = types is null
            ? document.Syntax.Diagnostics
            : document.GetSemanticModel(types).GetDiagnostics(cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        if (_documents.Get(document.Uri)?.Version != document.Version)
        {
            return;
        }

        var lines = document.Syntax.Lines;
        await NotifyAsync("textDocument/publishDiagnostics", new PublishDiagnosticsParams(
            document.Uri,
            document.Version,
            diagnostics.Select(d => LspConverters.ToDiagnostic(lines, d)).ToArray())).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ plumbing

    private static string Version =>
        typeof(XamlLanguageServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    private async Task NotifyAsync(string method, object parameters)
    {
        try
        {
            await _rpc.NotifyWithParameterObjectAsync(method, parameters).ConfigureAwait(false);
        }
        catch (Exception e) when (e is ConnectionLostException or ObjectDisposedException)
        {
        }
    }

    private void ForwardLog(LogLevel level, string message)
    {
        if (level >= LogLevel.Info)
        {
            var type = level switch { LogLevel.Error => 1, LogLevel.Warning => 2, _ => 3 };
            _ = NotifyAsync("window/logMessage", new LogMessageParams(type, message));
        }
    }

    private IProgress<string> CreateProgress(string title) => new WorkDoneProgress(this, title, _workDoneProgress);

    private static bool GetBool(JsonElement element, params string[] path)
    {
        foreach (var name in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out element))
            {
                return false;
            }
        }

        return element.ValueKind == JsonValueKind.True;
    }

    void IDisposable.Dispose()
    {
        _projects.Dispose();
        _rpc.Dispose();
    }

    /// <summary>LSP work-done progress (<c>$/progress</c>); messages are sent in order on a chained task.</summary>
    private sealed class WorkDoneProgress : IProgress<string>, IDisposable
    {
        private readonly XamlLanguageServer _server;
        private readonly string _token = $"wpf-xaml-ls/{Guid.NewGuid():N}";
        private readonly bool _enabled;
        private readonly Lock _gate = new();
        private Task _chain;

        public WorkDoneProgress(XamlLanguageServer server, string title, bool enabled)
        {
            _server = server;
            _enabled = enabled;
            _chain = !enabled ? Task.CompletedTask : Task.Run(async () =>
            {
                await server._rpc.InvokeWithParameterObjectAsync("window/workDoneProgress/create", new WorkDoneProgressCreateParams(_token)).ConfigureAwait(false);
                await Send(new WorkDoneProgressValue { Kind = "begin", Title = title, Cancellable = false }).ConfigureAwait(false);
            });
        }

        public void Report(string value) => Enqueue(new WorkDoneProgressValue { Kind = "report", Message = value });

        public void Dispose() => Enqueue(new WorkDoneProgressValue { Kind = "end" });

        private void Enqueue(WorkDoneProgressValue value)
        {
            if (!_enabled)
            {
                return;
            }

            lock (_gate)
            {
                _chain = _chain.ContinueWith(_ => Send(value), TaskScheduler.Default).Unwrap();
            }
        }

        private Task Send(WorkDoneProgressValue value) =>
            _server.NotifyAsync("$/progress", new ProgressParams<WorkDoneProgressValue>(_token, value));
    }
}
