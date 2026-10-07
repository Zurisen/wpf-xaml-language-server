using Microsoft.CodeAnalysis;
using WpfXaml.Language.Text;

namespace WpfXaml.Analysis.Features.Completion;

public enum XamlCompletionKind
{
    Type,
    Property,
    AttachedProperty,
    Event,
    Directive,
    EnumValue,
    Value,
    MarkupExtension,
    ResourceKey,
    Method,
    Namespace,
    ClosingTag,
}

public sealed record XamlCompletionItem(string Label, XamlCompletionKind Kind)
{
    public string? Detail { get; init; }

    /// <summary>Plain text to insert; defaults to <see cref="Label"/>.</summary>
    public string? InsertText { get; init; }

    /// <summary>Snippet variant (with <c>$1</c> placeholders) for clients that support snippets.</summary>
    public string? SnippetText { get; init; }

    public string? SortText { get; init; }

    /// <summary>The symbol to document. Documentation is resolved lazily (completionItem/resolve).</summary>
    public ISymbol? Symbol { get; init; }

    /// <summary>Static Markdown documentation, for items without a symbol.</summary>
    public string? Documentation { get; init; }
}

/// <param name="ReplaceSpan">The text every item replaces (the partially typed word).</param>
public sealed record XamlCompletionList(TextSpan ReplaceSpan, IReadOnlyList<XamlCompletionItem> Items);
