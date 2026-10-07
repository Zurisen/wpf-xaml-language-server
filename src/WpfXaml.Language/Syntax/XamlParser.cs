using System.Text;
using WpfXaml.Language.Text;

namespace WpfXaml.Language.Syntax;

/// <summary>
/// Hand-written XML parser that never throws and always produces a tree.
/// Recovery rules, chosen to keep half-typed markup useful for completion:
/// <list type="bullet">
/// <item>An unclosed start tag (<c>&lt;Button Con</c>) ends at the next <c>&lt;</c> and is treated as self-closing.</item>
/// <item>An attribute value without a closing quote ends at the next <c>&lt;</c>.</item>
/// <item>An end tag closes the nearest open element with the same name; elements opened after it are reported as unclosed.</item>
/// </list>
/// </summary>
internal sealed class XamlParser
{
    private readonly string _text;
    private readonly List<XamlDiagnostic> _diagnostics = [];
    private readonly List<XamlNode> _topLevel = [];
    private readonly List<XamlElement> _open = [];
    private int _pos;

    public XamlParser(string text)
    {
        _text = text;
    }

    public XamlDocument Parse()
    {
        while (_pos < _text.Length)
        {
            if (_text[_pos] == '<')
            {
                if (StartsWith("<!--"))
                {
                    ParseComment();
                }
                else if (StartsWith("<![CDATA["))
                {
                    ParseCData();
                }
                else if (StartsWith("<?") || StartsWith("<!"))
                {
                    ParseProcessingInstruction();
                }
                else if (StartsWith("</"))
                {
                    ParseEndTag();
                }
                else
                {
                    ParseStartTag();
                }
            }
            else
            {
                ParseText();
            }
        }

        for (var i = _open.Count - 1; i >= 0; i--)
        {
            var e = _open[i];
            e.Span = TextSpan.FromBounds(e.StartTagSpan.Start, _text.Length);
            Error($"Element '{e.Name}' is not closed.", e.Name.IsEmpty ? e.StartTagSpan : e.Name.Span);
        }

        var roots = _topLevel.OfType<XamlElement>().ToList();
        foreach (var extra in roots.Skip(1))
        {
            Error("A XAML document must have exactly one root element.", extra.Name.Span);
        }

        foreach (var text in _topLevel.OfType<XamlText>().Where(t => !t.IsWhitespace))
        {
            Error("Text is not allowed outside the root element.", text.Span);
        }

        return new XamlDocument(_text, _topLevel, _diagnostics);
    }

    private bool StartsWith(string s) => string.CompareOrdinal(_text, _pos, s, 0, s.Length) == 0;

    private char Peek(int offset = 0) => _pos + offset < _text.Length ? _text[_pos + offset] : '\0';

    private void Error(string message, TextSpan span) =>
        _diagnostics.Add(new XamlDiagnostic(DiagnosticCodes.Syntax, message, span, XamlDiagnosticSeverity.Error));

    private void Add(XamlNode node)
    {
        if (_open.Count > 0)
        {
            _open[^1].AddChild(node);
        }
        else
        {
            _topLevel.Add(node);
        }
    }

    private void ParseText()
    {
        var start = _pos;
        var end = _text.IndexOf('<', _pos);
        if (end < 0)
        {
            end = _text.Length;
        }

        _pos = end;
        var raw = _text[start..end];
        Add(new XamlText(XmlEntities.Decode(raw)) { Span = TextSpan.FromBounds(start, end) });
    }

    private void ParseComment()
    {
        var start = _pos;
        var end = _text.IndexOf("-->", _pos + 4, StringComparison.Ordinal);
        if (end < 0)
        {
            _pos = _text.Length;
            Error("Comment is not closed.", new TextSpan(start, 4));
        }
        else
        {
            _pos = end + 3;
        }

        Add(new XamlComment { Span = TextSpan.FromBounds(start, _pos) });
    }

    private void ParseCData()
    {
        var start = _pos;
        var contentStart = _pos + 9;
        var end = _text.IndexOf("]]>", contentStart, StringComparison.Ordinal);
        string content;
        if (end < 0)
        {
            content = _text[contentStart..];
            _pos = _text.Length;
            Error("CDATA section is not closed.", new TextSpan(start, 9));
        }
        else
        {
            content = _text[contentStart..end];
            _pos = end + 3;
        }

        Add(new XamlCData(content) { Span = TextSpan.FromBounds(start, _pos) });
    }

    private void ParseProcessingInstruction()
    {
        var start = _pos;
        var end = _text.IndexOf('>', _pos + 2);
        _pos = end < 0 ? _text.Length : end + 1;
        Add(new XamlProcessingInstruction { Span = TextSpan.FromBounds(start, _pos) });
    }

    private void ParseStartTag()
    {
        var start = _pos;
        _pos++; // '<'
        var name = ParseName();
        if (name.IsEmpty)
        {
            Error("Expected an element name.", new TextSpan(start, 1));
        }

        var element = new XamlElement(name);
        Add(element);

        var closed = false;
        var selfClosing = false;
        while (_pos < _text.Length)
        {
            SkipWhitespace();
            var c = Peek();
            if (c == '>')
            {
                _pos++;
                closed = true;
                break;
            }

            if (c == '/' && Peek(1) == '>')
            {
                _pos += 2;
                closed = true;
                selfClosing = true;
                break;
            }

            if (c == '<' || c == '\0')
            {
                break;
            }

            if (XmlChars.IsNameStartChar(c))
            {
                ParseAttribute(element);
            }
            else
            {
                Error($"Unexpected character '{c}' in start tag.", new TextSpan(_pos, 1));
                _pos++;
            }
        }

        element.StartTagSpan = TextSpan.FromBounds(start, _pos);
        element.IsStartTagClosed = closed;
        element.IsSelfClosing = selfClosing || !closed;

        if (!closed)
        {
            Error($"Start tag of '{name}' is not closed.", name.IsEmpty ? new TextSpan(start, 1) : name.Span);
        }

        if (element.IsSelfClosing)
        {
            element.Span = element.StartTagSpan;
        }
        else
        {
            _open.Add(element);
        }
    }

    private void ParseAttribute(XamlElement element)
    {
        var start = _pos;
        var name = ParseName();
        var attribute = new XamlAttribute(name);

        var afterName = _pos;
        SkipWhitespace();
        if (Peek() == '=')
        {
            attribute.HasEquals = true;
            _pos++;
            SkipWhitespace();
            var quote = Peek();
            if (quote is '"' or '\'')
            {
                _pos++;
                var valueStart = _pos;
                while (_pos < _text.Length && _text[_pos] != quote && _text[_pos] != '<')
                {
                    _pos++;
                }

                attribute.Quote = quote;
                attribute.ValueSpan = TextSpan.FromBounds(valueStart, _pos);
                attribute.RawValue = _text[valueStart.._pos];
                if (Peek() == quote)
                {
                    attribute.IsValueClosed = true;
                    _pos++;
                }
                else
                {
                    Error($"Value of attribute '{name}' is missing a closing quote.", TextSpan.FromBounds(valueStart - 1, _pos));
                }
            }
            else
            {
                // Unquoted value: accept it for recovery, but report it.
                var valueStart = _pos;
                while (_pos < _text.Length && !char.IsWhiteSpace(_text[_pos]) && _text[_pos] is not ('>' or '<') && !StartsWith("/>"))
                {
                    _pos++;
                }

                attribute.ValueSpan = TextSpan.FromBounds(valueStart, _pos);
                attribute.RawValue = _text[valueStart.._pos];
                Error($"Value of attribute '{name}' must be quoted.", TextSpan.FromBounds(start, Math.Max(_pos, valueStart + 1)));
            }
        }
        else
        {
            _pos = afterName;
            Error($"Attribute '{name}' is missing a value.", name.Span);
        }

        if (attribute.RawValue is not null)
        {
            attribute.Value = XmlEntities.Decode(attribute.RawValue);
        }

        attribute.Span = TextSpan.FromBounds(start, _pos);

        if (element.Attributes.Any(a => a.Name.FullName == name.FullName))
        {
            Error($"Duplicate attribute '{name}'.", name.Span);
        }

        element.AddAttribute(attribute);
    }

    private void ParseEndTag()
    {
        var start = _pos;
        _pos += 2; // '</'
        var name = ParseName();
        SkipWhitespace();
        if (Peek() == '>')
        {
            _pos++;
        }
        else
        {
            Error("End tag is not closed.", TextSpan.FromBounds(start, _pos));
        }

        var span = TextSpan.FromBounds(start, _pos);
        var index = _open.FindLastIndex(e => e.Name.FullName == name.FullName);
        if (index < 0)
        {
            Error(name.IsEmpty ? "Expected an element name." : $"Unexpected end tag '{name}'.", span);
            return;
        }

        for (var i = _open.Count - 1; i > index; i--)
        {
            var unclosed = _open[i];
            unclosed.Span = TextSpan.FromBounds(unclosed.StartTagSpan.Start, start);
            Error($"Element '{unclosed.Name}' is not closed.", unclosed.Name.IsEmpty ? unclosed.StartTagSpan : unclosed.Name.Span);
        }

        var element = _open[index];
        element.EndTagSpan = span;
        element.EndTagName = name;
        element.Span = TextSpan.FromBounds(element.StartTagSpan.Start, _pos);
        _open.RemoveRange(index, _open.Count - index);
    }

    private XamlName ParseName()
    {
        var start = _pos;
        if (_pos < _text.Length && XmlChars.IsNameStartChar(_text[_pos]))
        {
            _pos++;
            while (_pos < _text.Length && XmlChars.IsNameChar(_text[_pos]))
            {
                _pos++;
            }
        }

        var full = _text[start.._pos];
        var colon = full.IndexOf(':');
        return colon < 0
            ? new XamlName(null, full, TextSpan.FromBounds(start, _pos))
            : new XamlName(full[..colon], full[(colon + 1)..], TextSpan.FromBounds(start, _pos));
    }

    private void SkipWhitespace()
    {
        while (_pos < _text.Length && char.IsWhiteSpace(_text[_pos]))
        {
            _pos++;
        }
    }
}

internal static class XmlChars
{
    public static bool IsNameStartChar(char c) => char.IsLetter(c) || c == '_' || c == ':';

    public static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is '_' or ':' or '.' or '-' || char.GetUnicodeCategory(c) is
        System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.SpacingCombiningMark;
}

public static class XmlEntities
{
    public static string Decode(string raw)
    {
        if (raw.IndexOf('&') < 0)
        {
            return raw;
        }

        var sb = new StringBuilder(raw.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (c != '&')
            {
                sb.Append(c);
                continue;
            }

            var semi = raw.IndexOf(';', i + 1);
            if (semi < 0 || semi - i > 10)
            {
                sb.Append(c);
                continue;
            }

            var entity = raw.Substring(i + 1, semi - i - 1);
            string? replacement = entity switch
            {
                "lt" => "<",
                "gt" => ">",
                "amp" => "&",
                "quot" => "\"",
                "apos" => "'",
                _ when entity.StartsWith("#x", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(entity.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out var hex) => FromCodePoint(hex),
                _ when entity.StartsWith('#') && int.TryParse(entity.AsSpan(1), out var dec) => FromCodePoint(dec),
                _ => null,
            };

            if (replacement is null)
            {
                sb.Append(c);
                continue;
            }

            sb.Append(replacement);
            i = semi;
        }

        return sb.ToString();
    }

    private static string? FromCodePoint(int value) =>
        value is >= 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF) ? char.ConvertFromUtf32(value) : null;
}
