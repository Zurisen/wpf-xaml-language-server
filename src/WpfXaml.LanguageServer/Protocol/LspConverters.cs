using Microsoft.CodeAnalysis;
using WpfXaml.Analysis.Features;
using WpfXaml.Analysis.Features.Completion;
using WpfXaml.Language;
using WpfXaml.Language.Text;

namespace WpfXaml.LanguageServer.Protocol;

internal static class LspConverters
{
    public const string DiagnosticSource = "wpf-xaml";

    public static Position ToPosition(LinePosition position) => new(position.Line, position.Character);

    public static Range ToRange(LineIndex lines, TextSpan span) =>
        new(ToPosition(lines.GetPosition(span.Start)), ToPosition(lines.GetPosition(span.End)));

    public static int ToOffset(LineIndex lines, Position position) => lines.GetOffset(position.Line, position.Character);

    public static Diagnostic ToDiagnostic(LineIndex lines, XamlDiagnostic diagnostic) => new(
        ToRange(lines, diagnostic.Span),
        diagnostic.Severity switch
        {
            XamlDiagnosticSeverity.Error => 1,
            XamlDiagnosticSeverity.Warning => 2,
            XamlDiagnosticSeverity.Information => 3,
            _ => 4,
        },
        diagnostic.Code,
        DiagnosticSource,
        diagnostic.Message);

    public static CompletionList ToCompletionList(string uri, LineIndex lines, XamlCompletionList list, bool snippets)
    {
        var range = ToRange(lines, list.ReplaceSpan);
        var items = list.Items.Select(item => ToCompletionItem(uri, range, item, snippets)).ToArray();
        return new CompletionList(false, items);
    }

    private static CompletionItem ToCompletionItem(string uri, Range range, XamlCompletionItem item, bool snippets)
    {
        var useSnippet = snippets && item.SnippetText is not null;
        var text = useSnippet ? item.SnippetText! : item.InsertText ?? item.Label;
        return new CompletionItem
        {
            Label = item.Label,
            Kind = item.Kind switch
            {
                XamlCompletionKind.Type => CompletionItemKind.Class,
                XamlCompletionKind.MarkupExtension => CompletionItemKind.Class,
                XamlCompletionKind.Property or XamlCompletionKind.AttachedProperty => CompletionItemKind.Property,
                XamlCompletionKind.Event => CompletionItemKind.Event,
                XamlCompletionKind.EnumValue => CompletionItemKind.EnumMember,
                XamlCompletionKind.Value => CompletionItemKind.Value,
                XamlCompletionKind.ResourceKey => CompletionItemKind.Constant,
                XamlCompletionKind.Method => CompletionItemKind.Method,
                XamlCompletionKind.Namespace => CompletionItemKind.Module,
                _ => CompletionItemKind.Keyword,
            },
            Detail = item.Detail,
            Documentation = item.Documentation is { } docs ? MarkupContent.Markdown(docs) : null,
            SortText = item.SortText,
            InsertTextFormat = useSnippet ? InsertTextFormat.Snippet : InsertTextFormat.PlainText,
            TextEdit = new TextEdit(range, text),
            Data = item.Symbol is { } symbol ? new CompletionItemData(uri, symbol.GetDocumentationCommentId()) : null,
        };
    }

    public static Location ToLocation(string currentUri, XamlLocation location)
    {
        var uri = location.FilePath is null ? currentUri : DocumentUris.FromPath(location.FilePath);
        return new Location(uri, new Range(ToPosition(location.Start), ToPosition(location.End)));
    }
}
