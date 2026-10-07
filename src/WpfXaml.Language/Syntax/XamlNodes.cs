using WpfXaml.Language.Text;

namespace WpfXaml.Language.Syntax;

public abstract class XamlNode
{
    public TextSpan Span { get; internal set; }

    public XamlElement? Parent { get; internal set; }

    public IEnumerable<XamlElement> Ancestors()
    {
        for (var e = Parent; e is not null; e = e.Parent)
        {
            yield return e;
        }
    }
}

/// <summary>
/// A possibly prefixed XML name such as <c>local:MyControl</c> or <c>Grid.Row</c>.
/// For property-element and attached-member names the local name contains a dot.
/// </summary>
public sealed class XamlName
{
    public XamlName(string? prefix, string localName, TextSpan span)
    {
        Prefix = prefix;
        LocalName = localName;
        Span = span;
    }

    public string? Prefix { get; }

    public string LocalName { get; }

    /// <summary>The span of the whole name, including prefix and colon.</summary>
    public TextSpan Span { get; }

    public TextSpan LocalNameSpan => Prefix is null ? Span : TextSpan.FromBounds(Span.Start + Prefix.Length + 1, Span.End);

    public string FullName => Prefix is null ? LocalName : $"{Prefix}:{LocalName}";

    public bool IsEmpty => Span.IsEmpty;

    /// <summary>True for names like <c>Grid.Row</c> or <c>Button.Content</c>.</summary>
    public bool IsDotted => LocalName.Contains('.');

    /// <summary>For a dotted name, the part before the last dot (the owner type).</summary>
    public string? OwnerName => LocalName.LastIndexOf('.') is var i and >= 0 ? LocalName[..i] : null;

    /// <summary>For a dotted name, the part after the last dot; otherwise the whole local name.</summary>
    public string MemberName => LocalName.LastIndexOf('.') is var i and >= 0 ? LocalName[(i + 1)..] : LocalName;

    /// <summary>Span of <see cref="MemberName"/> within the document.</summary>
    public TextSpan MemberNameSpan => LocalName.LastIndexOf('.') is var i and >= 0
        ? TextSpan.FromBounds(LocalNameSpan.Start + i + 1, Span.End)
        : LocalNameSpan;

    /// <summary>Span of the prefix and owner part of a dotted name (e.g. <c>local:Grid</c>).</summary>
    public TextSpan OwnerSpan => LocalName.LastIndexOf('.') is var i and >= 0
        ? TextSpan.FromBounds(Span.Start, LocalNameSpan.Start + i)
        : Span;

    public override string ToString() => FullName;
}

public sealed class XamlAttribute : XamlNode
{
    internal XamlAttribute(XamlName name)
    {
        Name = name;
    }

    public XamlName Name { get; }

    /// <summary>The value as written, without quotes and without entity decoding. Null when there is no value.</summary>
    public string? RawValue { get; internal set; }

    /// <summary>The value with XML entities decoded.</summary>
    public string? Value { get; internal set; }

    /// <summary>Span of <see cref="RawValue"/> (between the quotes).</summary>
    public TextSpan? ValueSpan { get; internal set; }

    public bool HasEquals { get; internal set; }

    public char? Quote { get; internal set; }

    public bool IsValueClosed { get; internal set; }

    public XamlElement Element => Parent!;

    public bool IsNamespaceDeclaration =>
        Name.Prefix == "xmlns" || (Name.Prefix is null && Name.LocalName == "xmlns");

    /// <summary>The prefix declared by an <c>xmlns</c> attribute; null for the default namespace.</summary>
    public string? DeclaredPrefix => Name.Prefix == "xmlns" ? Name.LocalName : null;

    public override string ToString() => $"{Name}=\"{RawValue}\"";
}

public sealed class XamlElement : XamlNode
{
    private readonly List<XamlAttribute> _attributes = [];
    private readonly List<XamlNode> _children = [];

    internal XamlElement(XamlName name)
    {
        Name = name;
    }

    public XamlName Name { get; }

    public IReadOnlyList<XamlAttribute> Attributes => _attributes;

    public IReadOnlyList<XamlNode> Children => _children;

    public IEnumerable<XamlElement> ChildElements => _children.OfType<XamlElement>();

    /// <summary>From <c>&lt;</c> to the closing <c>&gt;</c> (or as far as the parser got).</summary>
    public TextSpan StartTagSpan { get; internal set; }

    public bool IsStartTagClosed { get; internal set; }

    public bool IsSelfClosing { get; internal set; }

    public TextSpan? EndTagSpan { get; internal set; }

    public XamlName? EndTagName { get; internal set; }

    /// <summary>The span between the start tag and the end tag.</summary>
    public TextSpan ContentSpan => IsSelfClosing || !IsStartTagClosed
        ? new TextSpan(StartTagSpan.End, 0)
        : TextSpan.FromBounds(StartTagSpan.End, EndTagSpan?.Start ?? Span.End);

    /// <summary><c>&lt;Owner.Property&gt;</c> syntax.</summary>
    public bool IsPropertyElement => Name.IsDotted;

    internal void AddAttribute(XamlAttribute attribute)
    {
        attribute.Parent = this;
        _attributes.Add(attribute);
    }

    internal void AddChild(XamlNode node)
    {
        node.Parent = this;
        _children.Add(node);
    }

    public XamlAttribute? FindAttribute(string? prefix, string localName) =>
        _attributes.FirstOrDefault(a => a.Name.Prefix == prefix && a.Name.LocalName == localName);

    /// <summary>Resolves a prefix to a namespace URI using the <c>xmlns</c> declarations in scope.</summary>
    public string? LookupNamespace(string? prefix)
    {
        if (prefix == "xml")
        {
            return XmlNamespaces.Xml;
        }

        if (prefix == "xmlns")
        {
            return XmlNamespaces.Xmlns;
        }

        for (XamlElement? e = this; e is not null; e = e.Parent)
        {
            foreach (var a in e._attributes)
            {
                if (a.IsNamespaceDeclaration && a.DeclaredPrefix == prefix)
                {
                    return a.Value ?? string.Empty;
                }
            }
        }

        return null;
    }

    /// <summary>All prefix → namespace mappings in scope; inner declarations shadow outer ones.</summary>
    public IReadOnlyList<(string? Prefix, string Namespace)> GetNamespacesInScope()
    {
        var seen = new HashSet<string>();
        var result = new List<(string?, string)>();
        for (XamlElement? e = this; e is not null; e = e.Parent)
        {
            foreach (var a in e._attributes)
            {
                if (a.IsNamespaceDeclaration && seen.Add(a.DeclaredPrefix ?? string.Empty))
                {
                    result.Add((a.DeclaredPrefix, a.Value ?? string.Empty));
                }
            }
        }

        return result;
    }

    public IEnumerable<XamlElement> DescendantsAndSelf()
    {
        var stack = new Stack<XamlElement>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var e = stack.Pop();
            yield return e;
            for (var i = e._children.Count - 1; i >= 0; i--)
            {
                if (e._children[i] is XamlElement child)
                {
                    stack.Push(child);
                }
            }
        }
    }

    public override string ToString() => $"<{Name}>";
}

public sealed class XamlText : XamlNode
{
    internal XamlText(string text)
    {
        Text = text;
    }

    public string Text { get; }

    public bool IsWhitespace => string.IsNullOrWhiteSpace(Text);
}

public sealed class XamlComment : XamlNode
{
}

public sealed class XamlCData : XamlNode
{
    internal XamlCData(string text)
    {
        Text = text;
    }

    public string Text { get; }
}

public sealed class XamlProcessingInstruction : XamlNode
{
}

public static class XmlNamespaces
{
    public const string Xml = "http://www.w3.org/XML/1998/namespace";
    public const string Xmlns = "http://www.w3.org/2000/xmlns/";

    public const string WpfPresentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    public const string Xaml2006 = "http://schemas.microsoft.com/winfx/2006/xaml";
    public const string MarkupCompatibility = "http://schemas.openxmlformats.org/markup-compatibility/2006";
    public const string Blend2008 = "http://schemas.microsoft.com/expression/blend/2008";

    public const string ClrNamespacePrefix = "clr-namespace:";
}
