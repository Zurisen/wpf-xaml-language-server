using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using WpfXaml.Analysis.TypeSystem;
using WpfXaml.Language;
using WpfXaml.Language.Syntax;
using WpfXaml.Language.Text;

namespace WpfXaml.Analysis.Semantics;

/// <summary>Quick fixes that need more than a rename: generating code-behind and importing namespaces.</summary>
internal static class CodeFixes
{
    private const int MaxImportSuggestions = 3;

    /// <summary>
    /// "Generate event handler": a method with the event's delegate signature, added to the end of the x:Class
    /// declaration in the code-behind file.
    /// </summary>
    public static XamlFix? GenerateEventHandler(XamlSemanticModel model, INamedTypeSymbol classSymbol, string handler, ITypeSymbol? delegateType)
    {
        if (!SyntaxFacts.IsValidIdentifier(handler))
        {
            return null;
        }

        // Prefer the hand-written code-behind (Foo.xaml.cs) over generated partial declarations in obj/.
        var declaration = classSymbol.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax() as TypeDeclarationSyntax)
            .OfType<TypeDeclarationSyntax>()
            .Where(d => d.SyntaxTree.FilePath is { Length: > 0 } path && !IsGenerated(path))
            .OrderByDescending(d => d.SyntaxTree.FilePath.EndsWith(".xaml.cs", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
        if (declaration is null)
        {
            return null;
        }

        var tree = declaration.SyntaxTree;
        var text = tree.GetText();
        var closeBrace = declaration.CloseBraceToken.SpanStart;
        var semanticModel = model.Types.Compilation.GetSemanticModel(tree);

        var invoke = (delegateType as INamedTypeSymbol)?.DelegateInvokeMethod;
        var parameters = invoke is null
            ? "object sender, System.EventArgs e"
            : string.Join(", ", invoke.Parameters.Select(p => $"{p.Type.ToMinimalDisplayString(semanticModel, closeBrace)} {p.Name}"));

        var classIndent = LeadingWhitespace(text, declaration.SpanStart);
        var memberIndent = declaration.Members.FirstOrDefault() is { } first ? LeadingWhitespace(text, first.SpanStart) : classIndent + "    ";
        var newline = text.ToString().Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var method = $"{memberIndent}private void {handler}({parameters}){newline}{memberIndent}{{{newline}{memberIndent}}}{newline}";

        // Insert on its own lines before the class's closing brace, after a blank line when there are members.
        var braceLine = text.Lines.GetLineFromPosition(closeBrace);
        var braceOnOwnLine = string.IsNullOrWhiteSpace(text.ToString(Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(braceLine.Start, closeBrace)));
        int insertAt;
        string insertText;
        if (braceOnOwnLine)
        {
            insertAt = braceLine.Start;
            insertText = (declaration.Members.Count > 0 ? newline : string.Empty) + method;
        }
        else
        {
            insertAt = closeBrace;
            insertText = newline + method + classIndent;
        }

        var position = tree.GetLineSpan(new Microsoft.CodeAnalysis.Text.TextSpan(insertAt, 0)).StartLinePosition;
        var at = new LinePosition(position.Line, position.Character);
        return new XamlFix(
            $"Generate event handler '{handler}' in {Path.GetFileName(tree.FilePath)}",
            [XamlTextEdit.InFile(tree.FilePath, at, at, insertText)],
            IsPreferred: true);
    }

    /// <summary>
    /// "Add xmlns": for an element whose type exists in a namespace the document doesn't import, either declare its
    /// undeclared prefix, or qualify the element with an existing or new prefix.
    /// </summary>
    public static IEnumerable<XamlFix> ImportNamespace(XamlSemanticModel model, XamlElement element)
    {
        var name = element.Name;
        var typeName = name.IsDotted ? name.OwnerName! : name.LocalName;
        if (model.Document.Root is not { } root || typeName.Length == 0)
        {
            yield break;
        }

        var inScope = element.GetNamespacesInScope();
        foreach (var type in model.Types.FindTypesByName(typeName).Take(MaxImportSuggestions))
        {
            var uri = XmlnsFor(model.Types, type);
            if (name.Prefix is { } undeclared)
            {
                // <local:Foo> with local undeclared: declare it.
                yield return new XamlFix($"Add xmlns:{undeclared}=\"{uri}\"", [AddXmlns(model, root, undeclared, uri)]);
                continue;
            }

            // <Foo> where Foo isn't in the default namespace: qualify it with a prefix for its namespace.
            var existing = inScope.FirstOrDefault(n => n.Prefix is not null && n.Namespace == uri).Prefix;
            var prefix = existing ?? NewPrefix(model.Types, type, uri, inScope);
            var edits = new List<XamlTextEdit>();
            if (existing is null)
            {
                edits.Add(AddXmlns(model, root, prefix, uri));
            }

            edits.Add(new XamlTextEdit(new TextSpan(name.Span.Start, 0), prefix + ":"));
            if (element.EndTagName is { } end && end.FullName == name.FullName)
            {
                edits.Add(new XamlTextEdit(new TextSpan(end.Span.Start, 0), prefix + ":"));
            }

            var title = existing is null
                ? $"Use {prefix}:{typeName} from {type.ContainingNamespace.ToDisplayString()} (add xmlns:{prefix})"
                : $"Use {prefix}:{typeName}";
            yield return new XamlFix(title, edits);
        }
    }

    private static string XmlnsFor(XamlTypeSystem types, INamedTypeSymbol type)
    {
        var clrNamespace = type.ContainingNamespace.IsGlobalNamespace ? string.Empty : type.ContainingNamespace.ToDisplayString();
        if (types.FindXmlnsUri(type.ContainingAssembly, clrNamespace) is { } uri)
        {
            return uri;
        }

        var isProject = SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, types.Compilation.Assembly);
        return isProject
            ? $"{XmlNamespaces.ClrNamespacePrefix}{clrNamespace}"
            : $"{XmlNamespaces.ClrNamespacePrefix}{clrNamespace};assembly={type.ContainingAssembly.Name}";
    }

    /// <summary><c>[XmlnsPrefix]</c>'s choice, <c>local</c> for the project, or the last namespace segment; made unique.</summary>
    private static string NewPrefix(XamlTypeSystem types, INamedTypeSymbol type, string uri, IReadOnlyList<(string? Prefix, string Namespace)> inScope)
    {
        var isProject = SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, types.Compilation.Assembly);
        var candidate = types.GetPreferredPrefix(uri)
            ?? (isProject ? "local" : type.ContainingNamespace.Name.ToLowerInvariant() is { Length: > 0 } last ? last : "ns");
        var prefix = candidate;
        for (var i = 2; inScope.Any(n => n.Prefix == prefix); i++)
        {
            prefix = candidate + i;
        }

        return prefix;
    }

    /// <summary>Inserts <c>xmlns:prefix="uri"</c> on the root element, after its last namespace declaration, matching its layout.</summary>
    private static XamlTextEdit AddXmlns(XamlSemanticModel model, XamlElement root, string prefix, string uri)
    {
        var text = model.Document.Text;
        var anchor = root.Attributes.LastOrDefault(a => a.IsNamespaceDeclaration) ?? root.Attributes.LastOrDefault();
        var declaration = $"xmlns:{prefix}=\"{uri}\"";
        if (anchor is null)
        {
            return new XamlTextEdit(new TextSpan(root.Name.Span.End, 0), " " + declaration);
        }

        // Attributes one per line: continue that layout; otherwise append on the same line.
        var lineStart = text.LastIndexOf('\n', Math.Max(0, anchor.Span.Start - 1)) + 1;
        var before = text[lineStart..anchor.Span.Start];
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var separator = string.IsNullOrWhiteSpace(before) ? newline + before : " ";
        return new XamlTextEdit(new TextSpan(anchor.Span.End, 0), separator + declaration);
    }

    private static string LeadingWhitespace(Microsoft.CodeAnalysis.Text.SourceText text, int position)
    {
        var line = text.Lines.GetLineFromPosition(position);
        var content = text.ToString(Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(line.Start, position));
        return new string(content.TakeWhile(char.IsWhiteSpace).ToArray());
    }

    private static bool IsGenerated(string path) =>
        path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase)
        || path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains("obj", StringComparer.OrdinalIgnoreCase);
}
