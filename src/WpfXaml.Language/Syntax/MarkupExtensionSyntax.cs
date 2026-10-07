using System.Text;
using WpfXaml.Language.Text;

namespace WpfXaml.Language.Syntax;

/// <summary><c>{prefix:Name arg, Key=Value, Key={Nested}}</c>. All spans are absolute document offsets.</summary>
public sealed class MarkupExtensionSyntax
{
    private readonly List<MarkupArgumentSyntax> _arguments = [];

    internal MarkupExtensionSyntax(XamlName typeName, int start)
    {
        TypeName = typeName;
        Span = new TextSpan(start, 0);
    }

    public XamlName TypeName { get; }

    public IReadOnlyList<MarkupArgumentSyntax> Arguments => _arguments;

    public TextSpan Span { get; internal set; }

    public bool IsClosed { get; internal set; }

    /// <summary>The argument whose value is this extension, when nested.</summary>
    public MarkupArgumentSyntax? ParentArgument { get; internal set; }

    public IEnumerable<MarkupArgumentSyntax> PositionalArguments => _arguments.Where(a => a.IsPositional);

    public MarkupArgumentSyntax? FindArgument(string name) =>
        _arguments.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal));

    internal void Add(MarkupArgumentSyntax argument)
    {
        argument.Owner = this;
        _arguments.Add(argument);
    }

    public IEnumerable<MarkupExtensionSyntax> DescendantsAndSelf()
    {
        yield return this;
        foreach (var a in _arguments)
        {
            if (a.Value.Extension is { } nested)
            {
                foreach (var d in nested.DescendantsAndSelf())
                {
                    yield return d;
                }
            }
        }
    }

    public override string ToString() => $"{{{TypeName} ...}}";
}

public sealed class MarkupArgumentSyntax
{
    internal MarkupArgumentSyntax(string? name, TextSpan? nameSpan, MarkupValueSyntax value, TextSpan span)
    {
        Name = name;
        NameSpan = nameSpan;
        Value = value;
        Span = span;
    }

    /// <summary>Null for positional arguments.</summary>
    public string? Name { get; }

    public TextSpan? NameSpan { get; }

    public MarkupValueSyntax Value { get; }

    public TextSpan Span { get; }

    public bool IsPositional => Name is null;

    public MarkupExtensionSyntax Owner { get; internal set; } = null!;
}

public sealed class MarkupValueSyntax
{
    internal MarkupValueSyntax(string text, TextSpan span, MarkupExtensionSyntax? extension)
    {
        Text = text;
        Span = span;
        Extension = extension;
    }

    /// <summary>The value with quotes and escapes removed.</summary>
    public string Text { get; }

    /// <summary>Span of the value as written (including quotes).</summary>
    public TextSpan Span { get; }

    public MarkupExtensionSyntax? Extension { get; }
}

public sealed record MarkupExtensionParseResult(MarkupExtensionSyntax? Root, IReadOnlyList<XamlDiagnostic> Diagnostics);

public static class MarkupExtensionParser
{
    /// <summary>True when an attribute value is a markup extension (starts with <c>{</c> but not the <c>{}</c> escape).</summary>
    public static bool IsMarkupExtension(string? raw) =>
        raw is { Length: > 0 } && raw[0] == '{' && !raw.StartsWith("{}", StringComparison.Ordinal);

    /// <summary>Parses <paramref name="raw"/>, which starts at document offset <paramref name="baseOffset"/>.</summary>
    public static MarkupExtensionParseResult Parse(string raw, int baseOffset)
    {
        var parser = new Parser(raw, baseOffset);
        var root = parser.ParseExtension();
        parser.SkipWhitespace();
        if (parser.Position < raw.Length)
        {
            parser.Error("Unexpected text after markup extension.", TextSpan.FromBounds(parser.Position, raw.Length));
        }

        return new MarkupExtensionParseResult(root, parser.Diagnostics);
    }

    private sealed class Parser(string s, int baseOffset)
    {
        public readonly List<XamlDiagnostic> Diagnostics = [];

        public int Position;

        private TextSpan Abs(int start, int end) => TextSpan.FromBounds(start + baseOffset, end + baseOffset);

        public void Error(string message, TextSpan relative) =>
            Diagnostics.Add(new XamlDiagnostic(DiagnosticCodes.MarkupExtensionSyntax, message, relative.Shift(baseOffset), XamlDiagnosticSeverity.Error));

        public void SkipWhitespace()
        {
            while (Position < s.Length && char.IsWhiteSpace(s[Position]))
            {
                Position++;
            }
        }

        private char Peek() => Position < s.Length ? s[Position] : '\0';

        public MarkupExtensionSyntax ParseExtension()
        {
            var start = Position;
            Position++; // '{'
            SkipWhitespace();

            var nameStart = Position;
            while (Position < s.Length && (char.IsLetterOrDigit(s[Position]) || s[Position] is '_' or ':' or '.'))
            {
                Position++;
            }

            var fullName = s[nameStart..Position];
            var colon = fullName.IndexOf(':');
            var typeName = colon < 0
                ? new XamlName(null, fullName, Abs(nameStart, Position))
                : new XamlName(fullName[..colon], fullName[(colon + 1)..], Abs(nameStart, Position));

            var ext = new MarkupExtensionSyntax(typeName, start + baseOffset);
            if (fullName.Length == 0)
            {
                Error("Expected a markup extension name.", new TextSpan(start, Math.Min(1, s.Length - start)));
            }

            while (true)
            {
                SkipWhitespace();
                var c = Peek();
                if (c == '\0')
                {
                    Error($"Markup extension '{typeName}' is missing a closing '}}'.", TextSpan.FromBounds(start, Position));
                    break;
                }

                if (c == '}')
                {
                    Position++;
                    ext.IsClosed = true;
                    break;
                }

                if (c == ',')
                {
                    // Empty argument (e.g. leading or double comma) - skip it.
                    Error("Expected an argument.", new TextSpan(Position, 1));
                    Position++;
                    continue;
                }

                var before = Position;
                ext.Add(ParseArgument());
                SkipWhitespace();
                if (Peek() == ',')
                {
                    Position++;
                }
                else if (Peek() is not ('}' or '\0'))
                {
                    Error("Expected ',' or '}'.", new TextSpan(Position, 1));
                }

                if (Position == before)
                {
                    Position++; // guarantee progress
                }
            }

            ext.Span = Abs(start, Position);
            return ext;
        }

        private MarkupArgumentSyntax ParseArgument()
        {
            var start = Position;

            // Named argument: identifier (dotted names allowed for attached properties) followed by '='.
            var q = Position;
            while (q < s.Length && (char.IsLetterOrDigit(s[q]) || s[q] is '_' or '.' or ':' or '(' or ')'))
            {
                q++;
            }

            var r = q;
            while (r < s.Length && char.IsWhiteSpace(s[r]))
            {
                r++;
            }

            if (q > Position && r < s.Length && s[r] == '=')
            {
                var name = s[Position..q];
                var nameSpan = Abs(Position, q);
                Position = r + 1;
                SkipWhitespace();
                var value = ParseValue();
                var argument = new MarkupArgumentSyntax(name, nameSpan, value, Abs(start, Position));
                value.Extension?.ParentArgument = argument;
                return argument;
            }

            var positional = ParseValue();
            var result = new MarkupArgumentSyntax(null, null, positional, Abs(start, Position));
            positional.Extension?.ParentArgument = result;
            return result;
        }

        private MarkupValueSyntax ParseValue()
        {
            var start = Position;
            var c = Peek();
            if (c == '{' && !(Position + 1 < s.Length && s[Position + 1] == '}'))
            {
                var nested = ParseExtension();
                return new MarkupValueSyntax(s[start..Position], Abs(start, Position), nested);
            }

            if (c is '\'' or '"')
            {
                Position++;
                var sb = new StringBuilder();
                var closed = false;
                while (Position < s.Length)
                {
                    var ch = s[Position];
                    if (ch == '\\' && Position + 1 < s.Length)
                    {
                        sb.Append(s[Position + 1]);
                        Position += 2;
                        continue;
                    }

                    Position++;
                    if (ch == c)
                    {
                        closed = true;
                        break;
                    }

                    sb.Append(ch);
                }

                if (!closed)
                {
                    Error("Quoted value is not closed.", TextSpan.FromBounds(start, Position));
                }

                return new MarkupValueSyntax(sb.ToString(), Abs(start, Position), null);
            }

            // Unquoted: up to ',' or '}' at depth 0. Brackets and braces nest so that
            // indexers (Path=Items[a,b]) and escaped formats (StringFormat={}{0:N2}) stay intact.
            var text = new StringBuilder();
            var braces = 0;
            var brackets = 0;
            var lastNonSpace = Position;
            var textLengthAtLastNonSpace = 0;
            while (Position < s.Length)
            {
                var ch = s[Position];
                if (ch == '\\' && Position + 1 < s.Length)
                {
                    text.Append(s[Position + 1]);
                    Position += 2;
                    lastNonSpace = Position;
                    textLengthAtLastNonSpace = text.Length;
                    continue;
                }

                if (braces == 0 && brackets == 0 && ch is ',' or '}')
                {
                    break;
                }

                switch (ch)
                {
                    case '{': braces++; break;
                    case '}': braces--; break;
                    case '[': brackets++; break;
                    case ']': brackets = Math.Max(0, brackets - 1); break;
                }

                text.Append(ch);
                Position++;
                if (!char.IsWhiteSpace(ch))
                {
                    lastNonSpace = Position;
                    textLengthAtLastNonSpace = text.Length;
                }
            }

            var value = text.ToString(0, textLengthAtLastNonSpace);
            if (value.StartsWith("{}", StringComparison.Ordinal))
            {
                value = value[2..];
            }

            return new MarkupValueSyntax(value, Abs(start, Math.Max(start, lastNonSpace)), null);
        }
    }
}
