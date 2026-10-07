using System.Collections.Concurrent;
using System.Text;
using WpfXaml.Analysis.Semantics;
using WpfXaml.Analysis.TypeSystem;
using WpfXaml.Language.Syntax;
using WpfXaml.LanguageServer.Protocol;

namespace WpfXaml.LanguageServer.Workspace;

/// <summary>An immutable snapshot of an open document. Parsing and binding are lazy and cached per snapshot.</summary>
public sealed class DocumentSnapshot
{
    private readonly Lazy<XamlDocument> _syntax;
    private (XamlTypeSystem Types, XamlSemanticModel Model)? _semantic;

    public DocumentSnapshot(string uri, int version, string text)
    {
        Uri = uri;
        Version = version;
        Text = text;
        Path = DocumentUris.ToPath(uri);
        _syntax = new Lazy<XamlDocument>(() => XamlDocument.Parse(text));
    }

    public string Uri { get; }

    public string? Path { get; }

    public int Version { get; }

    public string Text { get; }

    public XamlDocument Syntax => _syntax.Value;

    public XamlSemanticModel GetSemanticModel(XamlTypeSystem types)
    {
        var cached = _semantic;
        if (cached is { } c && ReferenceEquals(c.Types, types))
        {
            return c.Model;
        }

        var model = new XamlSemanticModel(Syntax, types);
        _semantic = (types, model);
        return model;
    }
}

public sealed class DocumentStore
{
    private readonly ConcurrentDictionary<string, DocumentSnapshot> _documents = new(StringComparer.Ordinal);

    public IEnumerable<DocumentSnapshot> All => _documents.Values;

    public DocumentSnapshot? Get(string uri) => _documents.GetValueOrDefault(uri);

    public DocumentSnapshot Open(string uri, int version, string text) => _documents[uri] = new DocumentSnapshot(uri, version, text);

    public void Close(string uri) => _documents.TryRemove(uri, out _);

    /// <summary>Applies full or incremental changes, in order.</summary>
    public DocumentSnapshot? Change(string uri, int version, IReadOnlyList<TextDocumentContentChangeEvent> changes)
    {
        if (!_documents.TryGetValue(uri, out var current))
        {
            return null;
        }

        var text = current.Text;
        foreach (var change in changes)
        {
            if (change.Range is null)
            {
                text = change.Text;
                continue;
            }

            var lines = new WpfXaml.Language.Text.LineIndex(text);
            var start = LspConverters.ToOffset(lines, change.Range.Start);
            var end = LspConverters.ToOffset(lines, change.Range.End);
            text = new StringBuilder(text.Length - (end - start) + change.Text.Length)
                .Append(text, 0, start)
                .Append(change.Text)
                .Append(text, end, text.Length - end)
                .ToString();
        }

        var snapshot = new DocumentSnapshot(uri, version, text);
        _documents[uri] = snapshot;
        return snapshot;
    }
}
