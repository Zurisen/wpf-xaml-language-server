using WpfXaml.Language.Text;

namespace WpfXaml.Language.Syntax;

public sealed class XamlDocument
{
    internal XamlDocument(string text, IReadOnlyList<XamlNode> nodes, IReadOnlyList<XamlDiagnostic> diagnostics)
    {
        Text = text;
        Nodes = nodes;
        Diagnostics = diagnostics;
        Lines = new LineIndex(text);
        Root = nodes.OfType<XamlElement>().FirstOrDefault();
    }

    public string Text { get; }

    public LineIndex Lines { get; }

    /// <summary>Top-level nodes (prolog, comments, root element).</summary>
    public IReadOnlyList<XamlNode> Nodes { get; }

    public XamlElement? Root { get; }

    /// <summary>Syntax diagnostics only.</summary>
    public IReadOnlyList<XamlDiagnostic> Diagnostics { get; }

    public static XamlDocument Parse(string text) => new XamlParser(text).Parse();

    public IEnumerable<XamlElement> AllElements() =>
        Nodes.OfType<XamlElement>().SelectMany(e => e.DescendantsAndSelf());

    /// <summary>
    /// Finds the innermost element whose start tag contains <paramref name="position"/>.
    /// A position right after an unclosed start tag counts as inside it, so half-typed tags are found.
    /// </summary>
    public XamlElement? FindStartTagAt(int position)
    {
        XamlElement? best = null;
        foreach (var e in AllElements())
        {
            var tag = e.StartTagSpan;
            var inside = position > tag.Start && (position < tag.End || (!e.IsStartTagClosed && position == tag.End));
            if (inside && (best is null || tag.Start >= best.StartTagSpan.Start))
            {
                best = e;
            }
        }

        return best;
    }

    /// <summary>Finds the innermost element whose content (between start and end tag) contains the position.</summary>
    public XamlElement? FindContainingElement(int position)
    {
        XamlElement? best = null;
        foreach (var e in AllElements())
        {
            if (e.ContentSpan.ContainsOrTouches(position) && !e.IsSelfClosing && e.IsStartTagClosed)
            {
                if (best is null || e.ContentSpan.Start >= best.ContentSpan.Start)
                {
                    best = e;
                }
            }
        }

        return best;
    }
}
