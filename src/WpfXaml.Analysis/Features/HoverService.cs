using WpfXaml.Analysis.Semantics;
using WpfXaml.Language.Text;

namespace WpfXaml.Analysis.Features;

public sealed record XamlHover(string Markdown, TextSpan Span);

public static class HoverService
{
    public static XamlHover? GetHover(XamlSemanticModel model, int offset)
    {
        var target = XamlTargetResolver.Resolve(model, offset);
        if (target is null)
        {
            return null;
        }

        var markdown = target.Kind switch
        {
            XamlTargetKind.Type or XamlTargetKind.MarkupExtension when target.Symbol is { } type =>
                DocumentationFormatter.Hover(type.ToDisplayString(DocumentationFormatter.FullTypeFormat), type),
            XamlTargetKind.Member when target.Member is { } member =>
                DocumentationFormatter.Hover(DocumentationFormatter.MemberSignature(member), member.Symbol),
            XamlTargetKind.Directive when target.Directive is { } directive =>
                $"**x:{directive.Name}** (XAML directive)\n\n{directive.Description}",
            XamlTargetKind.EnumValue or XamlTargetKind.StaticMember when target.Symbol is { } symbol =>
                DocumentationFormatter.Hover(symbol.ToDisplayString(DocumentationFormatter.SignatureFormat), symbol),
            XamlTargetKind.EventHandler => target.Symbol is { } handler
                ? DocumentationFormatter.Hover(handler.ToDisplayString(DocumentationFormatter.SignatureFormat), handler)
                : $"Event handler `{target.Name}` (not found in code-behind)",
            XamlTargetKind.ClassName => target.Symbol is { } cls
                ? DocumentationFormatter.Hover(cls.ToDisplayString(DocumentationFormatter.FullTypeFormat), cls)
                : $"Class `{target.Name}` (generated from this XAML)",
            XamlTargetKind.ResourceReference or XamlTargetKind.ResourceKey => ResourceHover(model, target),
            _ => null,
        };

        return markdown is null ? null : new XamlHover(markdown, target.Span);
    }

    private static string ResourceHover(XamlSemanticModel model, XamlTarget target)
    {
        var key = target.Name ?? string.Empty;
        var scope = target.Element is { } element ? model.GetResourceScope(element) : null;
        var definitions = scope?.Find(key) ?? [];
        if (definitions.Count == 0)
        {
            var reasons = scope?.IncompleteReasons.ToList() ?? [];
            return reasons.Count == 0
                ? $"Resource `{key}` (not found)"
                : $"Resource `{key}` (not found in the dictionaries visible here; it may come from elsewhere: {string.Join("; ", reasons)})";
        }

        var lines = definitions.Select(d =>
        {
            var where = d.FilePath is null ? "this file" : $"`{Path.GetFileName(d.FilePath)}`";
            return $"- `{d.ElementName}` in {where}, line {d.Start.Line + 1}";
        });

        return $"Resource `{key}`\n\n{string.Join('\n', lines)}";
    }
}
