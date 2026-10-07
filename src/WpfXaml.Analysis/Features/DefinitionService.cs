using Microsoft.CodeAnalysis;
using WpfXaml.Analysis.Semantics;
using WpfXaml.Language.Text;

namespace WpfXaml.Analysis.Features;

/// <param name="FilePath">Target file, or null for the current XAML document.</param>
public sealed record XamlLocation(string? FilePath, LinePosition Start, LinePosition End);

public static class DefinitionService
{
    public static IReadOnlyList<XamlLocation> GetDefinitions(XamlSemanticModel model, int offset)
    {
        var target = XamlTargetResolver.Resolve(model, offset);
        if (target is null)
        {
            return [];
        }

        if (target.Kind is XamlTargetKind.ResourceReference or XamlTargetKind.ResourceKey)
        {
            var lines = model.Document.Lines;
            return model.GetResourceKeys()
                .Where(k => k.Key == target.Name && k.Attribute.ValueSpan is not null)
                .Select(k => k.Attribute.ValueSpan!.Value)
                .Select(span => new XamlLocation(null, lines.GetPosition(span.Start), lines.GetPosition(span.End)))
                .ToList();
        }

        return target.Symbol is null ? [] : GetSourceLocations(target.Symbol);
    }

    /// <summary>Source locations of a symbol. Metadata-only symbols (framework types) have none.</summary>
    public static IReadOnlyList<XamlLocation> GetSourceLocations(ISymbol symbol)
    {
        // Attached members are represented by their accessor; for go-to-definition the identifier field is nicer,
        // but the accessor is where the user's code lives, so keep it.
        return symbol.Locations
            .Where(l => l.IsInSource && l.SourceTree?.FilePath is { Length: > 0 })
            .Select(l =>
            {
                var span = l.GetLineSpan();
                return new XamlLocation(
                    span.Path,
                    new LinePosition(span.StartLinePosition.Line, span.StartLinePosition.Character),
                    new LinePosition(span.EndLinePosition.Line, span.EndLinePosition.Character));
            })
            .ToList();
    }
}
