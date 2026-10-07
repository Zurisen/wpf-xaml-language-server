using System.Text.Json;
using System.Threading.Channels;
using Nerdbank.Streams;
using StreamJsonRpc;
using WpfXaml.LanguageServer.Protocol;

namespace WpfXaml.LanguageServer.Tests;

/// <summary>An LSP client talking to an in-process server over an in-memory duplex stream.</summary>
internal sealed class TestClient : IAsyncDisposable
{
    private readonly JsonRpc _rpc;
    private readonly XamlLanguageServer _server;
    private readonly ClientTarget _target = new();

    private TestClient(JsonRpc rpc, XamlLanguageServer server)
    {
        _rpc = rpc;
        _server = server;
    }

    public static async Task<TestClient> StartAsync(object initializationOptions, string? rootPath = null)
    {
        var (clientStream, serverStream) = FullDuplexStream.CreatePair();
        var server = XamlLanguageServer.Create(serverStream, serverStream, new ServerLog(LogLevel.Warning));
        server.Start();

        var formatter = new SystemTextJsonFormatter { JsonSerializerOptions = XamlLanguageServer.JsonOptions };
        var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(clientStream, clientStream, formatter));
        var client = new TestClient(rpc, server);
        rpc.AddLocalRpcTarget(client._target);
        rpc.StartListening();

        await client.RequestAsync<JsonElement>("initialize", new
        {
            processId = Environment.ProcessId,
            rootUri = rootPath is null ? null : DocumentUris.FromPath(rootPath),
            capabilities = new
            {
                textDocument = new { completion = new { completionItem = new { snippetSupport = true } } },
                window = new { workDoneProgress = true },
                workspace = new { didChangeWatchedFiles = new { dynamicRegistration = true } },
            },
            initializationOptions,
        });
        await client.NotifyAsync("initialized", new { });
        return client;
    }

    public Task<T> RequestAsync<T>(string method, object parameters) =>
        _rpc.InvokeWithParameterObjectAsync<T>(method, parameters, TestContext.Current.CancellationToken);

    public Task NotifyAsync(string method, object parameters) => _rpc.NotifyWithParameterObjectAsync(method, parameters);

    public Task OpenAsync(string uri, string text, int version = 1) =>
        NotifyAsync("textDocument/didOpen", new DidOpenTextDocumentParams(new TextDocumentItem(uri, "xaml", version, text)));

    public static object Position(string uri, int line, int character) =>
        new TextDocumentPositionParams(new TextDocumentIdentifier(uri), new Position(line, character));

    /// <summary>Waits for a diagnostics notification for <paramref name="uri"/> that satisfies <paramref name="predicate"/>.</summary>
    public async Task<PublishDiagnosticsParams> WaitForDiagnosticsAsync(string uri, Func<PublishDiagnosticsParams, bool> predicate, TimeSpan? timeout = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
        PublishDiagnosticsParams? last = null;
        try
        {
            await foreach (var diagnostics in _target.Diagnostics.Reader.ReadAllAsync(cts.Token))
            {
                if (diagnostics.Uri == uri)
                {
                    last = diagnostics;
                    if (predicate(diagnostics))
                    {
                        return diagnostics;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }

        var seen = last is null ? "nothing" : string.Join("; ", last.Diagnostics.Select(d => $"{d.Code} {d.Message}"));
        throw new TimeoutException($"No matching diagnostics for {uri}. Last: {seen}");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await RequestAsync<JsonElement?>("shutdown", new { });
            await NotifyAsync("exit", new { });
        }
        catch (Exception e) when (e is ConnectionLostException or ObjectDisposedException)
        {
        }

        _rpc.Dispose();
        ((IDisposable)_server).Dispose();
    }

    private sealed class ClientTarget
    {
        public Channel<PublishDiagnosticsParams> Diagnostics { get; } = Channel.CreateUnbounded<PublishDiagnosticsParams>();

        [JsonRpcMethod("textDocument/publishDiagnostics", UseSingleObjectParameterDeserialization = true)]
        public void PublishDiagnostics(PublishDiagnosticsParams parameters) => Diagnostics.Writer.TryWrite(parameters);

        [JsonRpcMethod("window/logMessage", UseSingleObjectParameterDeserialization = true)]
        public void LogMessage(JsonElement parameters)
        {
        }

        [JsonRpcMethod("$/progress", UseSingleObjectParameterDeserialization = true)]
        public void Progress(JsonElement parameters)
        {
        }

        [JsonRpcMethod("window/workDoneProgress/create", UseSingleObjectParameterDeserialization = true)]
        public object? CreateProgress(JsonElement parameters) => null;

        [JsonRpcMethod("client/registerCapability", UseSingleObjectParameterDeserialization = true)]
        public object? RegisterCapability(JsonElement parameters) => null;
    }
}
