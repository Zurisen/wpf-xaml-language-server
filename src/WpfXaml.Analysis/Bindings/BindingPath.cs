using WpfXaml.Language.Text;

namespace WpfXaml.Analysis.Bindings;

public enum BindingPathSegmentKind
{
    /// <summary><c>Name</c></summary>
    Property,

    /// <summary><c>[0]</c>, <c>[key]</c></summary>
    Indexer,

    /// <summary><c>(Grid.Row)</c>, <c>(local:Behaviors.IsOn)</c></summary>
    Attached,

    /// <summary><c>/</c>: the current item of a collection view.</summary>
    CurrentItem,
}

/// <param name="Text">The name, or the content between the brackets/parentheses.</param>
/// <param name="Span">Absolute document span of the segment as written.</param>
public sealed record BindingPathSegment(BindingPathSegmentKind Kind, string Text, TextSpan Span);

/// <summary>Splits a WPF property path (<c>Customer.Orders[0].Total</c>) into segments with document spans.</summary>
public static class BindingPath
{
    /// <param name="path">The path text.</param>
    /// <param name="baseOffset">Document offset of the first character of <paramref name="path"/>.</param>
    /// <returns>The segments; empty for <c>.</c> or an empty path (the source itself). Null when the path uses syntax this parser doesn't handle.</returns>
    public static IReadOnlyList<BindingPathSegment>? Parse(string path, int baseOffset)
    {
        var segments = new List<BindingPathSegment>();
        var i = 0;
        while (i < path.Length)
        {
            var c = path[i];
            if (char.IsWhiteSpace(c) || c == '.')
            {
                i++;
                continue;
            }

            if (c == '/')
            {
                segments.Add(new BindingPathSegment(BindingPathSegmentKind.CurrentItem, "/", new TextSpan(baseOffset + i, 1)));
                i++;
                continue;
            }

            if (c is '[' or '(')
            {
                var close = path.IndexOf(c == '[' ? ']' : ')', i + 1);
                if (close < 0)
                {
                    return null;
                }

                var kind = c == '[' ? BindingPathSegmentKind.Indexer : BindingPathSegmentKind.Attached;
                segments.Add(new BindingPathSegment(kind, path[(i + 1)..close].Trim(), TextSpan.FromBounds(baseOffset + i, baseOffset + close + 1)));
                i = close + 1;
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < path.Length && (char.IsLetterOrDigit(path[i]) || path[i] == '_'))
                {
                    i++;
                }

                segments.Add(new BindingPathSegment(BindingPathSegmentKind.Property, path[start..i], TextSpan.FromBounds(baseOffset + start, baseOffset + i)));
                continue;
            }

            return null;
        }

        return segments;
    }
}
