using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using WpfXaml.Analysis.TypeSystem;

namespace WpfXaml.Analysis.Features;

/// <summary>Turns symbols and their XML documentation into Markdown for hover and completion.</summary>
public static partial class DocumentationFormatter
{
    public static readonly SymbolDisplayFormat SignatureFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeType | SymbolDisplayMemberOptions.IncludeContainingType
            | SymbolDisplayMemberOptions.IncludeParameters,
        kindOptions: SymbolDisplayKindOptions.IncludeTypeKeyword | SymbolDisplayKindOptions.IncludeMemberKeyword,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName,
        propertyStyle: SymbolDisplayPropertyStyle.ShowReadWriteDescriptor,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    public static readonly SymbolDisplayFormat FullTypeFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        kindOptions: SymbolDisplayKindOptions.IncludeTypeKeyword,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    public static string ShortTypeName(ITypeSymbol? type) =>
        type?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) ?? "?";

    /// <summary>One-line signature of an XAML member, e.g. <c>Thickness FrameworkElement.Margin</c> or <c>attached int Grid.Row</c>.</summary>
    public static string MemberSignature(XamlMember member) => member.Kind switch
    {
        XamlMemberKind.AttachedProperty => $"attached property {ShortTypeName(member.ValueType)} {member.Owner.Name}.{member.Name}",
        XamlMemberKind.AttachedEvent => $"attached event {ShortTypeName(member.ValueType)} {member.Owner.Name}.{member.Name}",
        _ => member.Symbol.ToDisplayString(SignatureFormat),
    };

    public static string CodeBlock(string code) => $"```csharp\n{code}\n```";

    /// <summary>Signature code block followed by the documentation summary, when there is one.</summary>
    public static string Hover(string signature, ISymbol? documented)
    {
        var summary = documented is null ? null : GetSummary(documented);
        return summary is null ? CodeBlock(signature) : $"{CodeBlock(signature)}\n\n{summary}";
    }

    /// <summary>The <c>&lt;summary&gt;</c> of a symbol as Markdown, or null.</summary>
    public static string? GetSummary(ISymbol symbol)
    {
        string? xml;
        try
        {
            xml = symbol.GetDocumentationCommentXml(expandIncludes: false);
        }
        catch (Exception e) when (e is IOException or XmlException or UnauthorizedAccessException)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        XElement? summary;
        try
        {
            var root = XElement.Parse(xml.TrimStart().StartsWith("<member", StringComparison.Ordinal) ? xml : $"<doc>{xml}</doc>", LoadOptions.PreserveWhitespace);
            summary = root.DescendantsAndSelf("summary").FirstOrDefault();
        }
        catch (XmlException)
        {
            return null;
        }

        if (summary is null)
        {
            return null;
        }

        var sb = new StringBuilder();
        Render(summary, sb);
        var text = Whitespace().Replace(sb.ToString(), " ").Replace(" \u0001 ", "\n\n").Replace("\u0001", "\n\n").Trim();
        return text.Length == 0 ? null : text;
    }

    private static void Render(XElement element, StringBuilder sb)
    {
        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XText text:
                    sb.Append(text.Value);
                    break;
                case XElement { Name.LocalName: "see" or "seealso" } see:
                    var target = (string?)see.Attribute("cref") ?? (string?)see.Attribute("langword") ?? (string?)see.Attribute("href");
                    var label = see.Value;
                    sb.Append('`').Append(label.Length > 0 ? label : CrefToName(target)).Append('`');
                    break;
                case XElement { Name.LocalName: "paramref" or "typeparamref" } reference:
                    sb.Append('`').Append((string?)reference.Attribute("name")).Append('`');
                    break;
                case XElement { Name.LocalName: "c" or "code" } code:
                    sb.Append('`').Append(code.Value.Trim()).Append('`');
                    break;
                case XElement { Name.LocalName: "para" or "br" } para:
                    sb.Append('\u0001');
                    Render(para, sb);
                    break;
                case XElement other:
                    Render(other, sb);
                    break;
            }
        }
    }

    private static string CrefToName(string? cref)
    {
        if (string.IsNullOrEmpty(cref))
        {
            return string.Empty;
        }

        var name = cref.Length > 2 && cref[1] == ':' ? cref[2..] : cref;
        var paren = name.IndexOf('(');
        if (paren >= 0)
        {
            name = name[..paren];
        }

        // Keep "Type.Member" for members, the simple name for types.
        var parts = name.Split('.');
        return cref.StartsWith("T:", StringComparison.Ordinal) || parts.Length < 2 ? parts[^1] : $"{parts[^2]}.{parts[^1]}";
    }

    [GeneratedRegex(@"[ \t\r\n]+")]
    private static partial Regex Whitespace();
}
