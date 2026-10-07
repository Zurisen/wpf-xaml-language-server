using System.Text.Json;
using System.Text.Json.Serialization;

namespace WpfXaml.LanguageServer.Protocol;

// The subset of the LSP 3.17 protocol this server uses. Names map to camelCase JSON.

public sealed record Position(int Line, int Character);

public sealed record Range(Position Start, Position End);

public sealed record Location(string Uri, Range Range);

public sealed record TextDocumentIdentifier(string Uri);

public sealed record VersionedTextDocumentIdentifier(string Uri, int Version);

public sealed record TextDocumentItem(string Uri, string LanguageId, int Version, string Text);

public sealed record TextDocumentContentChangeEvent(Range? Range, string Text);

public sealed record DidOpenTextDocumentParams(TextDocumentItem TextDocument);

public sealed record DidChangeTextDocumentParams(VersionedTextDocumentIdentifier TextDocument, TextDocumentContentChangeEvent[] ContentChanges);

public sealed record DidCloseTextDocumentParams(TextDocumentIdentifier TextDocument);

public sealed record DidSaveTextDocumentParams(TextDocumentIdentifier TextDocument);

public record TextDocumentPositionParams(TextDocumentIdentifier TextDocument, Position Position);

public sealed record WorkspaceFolder(string Uri, string Name);

public sealed record InitializeParams(
    int? ProcessId,
    string? RootUri,
    string? RootPath,
    WorkspaceFolder[]? WorkspaceFolders,
    JsonElement? Capabilities,
    JsonElement? InitializationOptions);

public sealed record ServerInfo(string Name, string Version);

public sealed record InitializeResult(ServerCapabilities Capabilities, ServerInfo ServerInfo);

public sealed record ServerCapabilities
{
    public TextDocumentSyncOptions? TextDocumentSync { get; init; }

    public CompletionOptions? CompletionProvider { get; init; }

    public bool HoverProvider { get; init; }

    public bool DefinitionProvider { get; init; }
}

public sealed record TextDocumentSyncOptions(bool OpenClose, int Change, bool Save);

public sealed record CompletionOptions(string[] TriggerCharacters, bool ResolveProvider);

public static class TextDocumentSyncKind
{
    public const int Full = 1;
    public const int Incremental = 2;
}

public sealed record CompletionList(bool IsIncomplete, CompletionItem[] Items);

public sealed record CompletionItem
{
    public required string Label { get; init; }

    public int? Kind { get; init; }

    public string? Detail { get; init; }

    public MarkupContent? Documentation { get; init; }

    public string? SortText { get; init; }

    public string? FilterText { get; init; }

    public int? InsertTextFormat { get; init; }

    public TextEdit? TextEdit { get; init; }

    public CompletionItemData? Data { get; init; }
}

/// <summary>What completionItem/resolve needs to find the documentation again.</summary>
public sealed record CompletionItemData(string Uri, string? SymbolId);

public static class InsertTextFormat
{
    public const int PlainText = 1;
    public const int Snippet = 2;
}

public static class CompletionItemKind
{
    public const int Method = 2;
    public const int Module = 9;
    public const int Property = 10;
    public const int Value = 12;
    public const int Keyword = 14;
    public const int Class = 7;
    public const int Constant = 21;
    public const int EnumMember = 20;
    public const int Event = 23;
}

public sealed record TextEdit(Range Range, string NewText);

public sealed record MarkupContent(string Kind, string Value)
{
    public static MarkupContent Markdown(string value) => new("markdown", value);
}

public sealed record Hover(MarkupContent Contents, Range? Range);

public sealed record Diagnostic(Range Range, int Severity, string Code, string Source, string Message);

public sealed record PublishDiagnosticsParams(string Uri, int? Version, Diagnostic[] Diagnostics);

public sealed record LogMessageParams(int Type, string Message);

public sealed record FileEvent(string Uri, int Type);

public sealed record DidChangeWatchedFilesParams(FileEvent[] Changes);

public static class FileChangeType
{
    public const int Created = 1;
    public const int Changed = 2;
    public const int Deleted = 3;
}

public sealed record Registration(string Id, string Method, object? RegisterOptions);

public sealed record RegistrationParams(Registration[] Registrations);

public sealed record LspFileSystemWatcher(string GlobPattern);

public sealed record DidChangeWatchedFilesRegistrationOptions(LspFileSystemWatcher[] Watchers);

public sealed record WorkDoneProgressCreateParams(string Token);

public sealed record ProgressParams<T>(string Token, T Value);

public sealed record WorkDoneProgressValue
{
    public required string Kind { get; init; }

    public string? Title { get; init; }

    public string? Message { get; init; }

    [JsonPropertyName("cancellable")]
    public bool? Cancellable { get; init; }
}

/// <summary>Options passed by the client in <c>initializationOptions</c>.</summary>
public sealed record ServerSettings
{
    /// <summary>Path to a .sln/.slnx/.csproj to load. Default: discovered from the workspace root.</summary>
    public string? Solution { get; init; }

    /// <summary>When false, only framework types are used (no MSBuild). Default: true.</summary>
    public bool LoadProjects { get; init; } = true;

    /// <summary>Major version of the WPF targeting pack for the framework-only fallback (e.g. 8). Default: highest installed.</summary>
    public int? FrameworkVersion { get; init; }
}
