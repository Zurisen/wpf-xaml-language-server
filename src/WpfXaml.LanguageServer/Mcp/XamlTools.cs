using System.ComponentModel;
using System.Text;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using WpfXaml.Analysis.Bindings;
using WpfXaml.Analysis.Features;
using WpfXaml.Analysis.TypeSystem;
using WpfXaml.Language;
using WpfXaml.Language.Syntax;
using WpfXaml.LanguageServer.Workspace;

namespace WpfXaml.LanguageServer.Mcp;

/// <summary>
/// MCP tools for AI agents editing WPF XAML. Output is plain text; positions are 1-based (line, column), matching the
/// <c>file(line,col)</c> format of the diagnostics.
/// </summary>
[McpServerToolType]
public sealed class XamlTools(XamlWorkspace workspace)
{
    private const int MaxDiagnostics = 300;
    private const int MaxFiles = 2000;

    [McpServerTool(Name = "xaml_check", ReadOnly = true, Idempotent = true)]
    [Description("Checks WPF XAML for errors, the way the WPF build and runtime would see them: unknown types, properties " +
        "and events, invalid values, missing resources, wrong binding paths, missing event handlers in code-behind. " +
        "Pass a .xaml file (optionally with `content` to check text you haven't saved yet), a folder, or a .sln/.csproj " +
        "to check every XAML file under it. Each problem lists its quick fixes. Reports only problems that are certain.")]
    public async Task<string> CheckAsync(
        [Description("A .xaml file, a folder, or a .sln/.slnx/.csproj; absolute or relative to the server's root.")] string path,
        [Description("Optional: the file's new content to check instead of what is on disk (single file only).")] string? content = null,
        CancellationToken cancellationToken = default)
    {
        var fullPath = workspace.ResolvePath(path);
        if (path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
        {
            if (content is null && !File.Exists(fullPath))
            {
                return $"File not found: {fullPath}";
            }

            var file = await workspace.AnalyzeAsync(fullPath, content, cancellationToken).ConfigureAwait(false);
            var diagnostics = Diagnostics(file, cancellationToken);
            var output = new StringBuilder();
            output.AppendLine(Status(file));
            AppendDiagnostics(output, file, diagnostics);
            output.AppendLine(diagnostics.Count == 0 ? "No problems found." : Summary(1, diagnostics));
            return output.ToString();
        }

        var folder = Directory.Exists(fullPath) ? fullPath : Path.GetDirectoryName(fullPath);
        if (folder is null || !Directory.Exists(folder))
        {
            return $"Not found: {fullPath}";
        }

        var files = EnumerateXaml(folder).Take(MaxFiles + 1).ToList();
        var report = new StringBuilder();
        var all = new List<XamlDiagnostic>();
        var shown = 0;
        foreach (var xamlPath in files.Take(MaxFiles))
        {
            var file = await workspace.AnalyzeAsync(xamlPath, null, cancellationToken).ConfigureAwait(false);
            var diagnostics = Diagnostics(file, cancellationToken);
            all.AddRange(diagnostics);
            if (shown < MaxDiagnostics && diagnostics.Count > 0)
            {
                shown += AppendDiagnostics(report, file, diagnostics.Take(MaxDiagnostics - shown).ToList());
            }
        }

        if (all.Count > shown)
        {
            report.AppendLine($"… {all.Count - shown} more not shown; check a narrower folder or single files.");
        }

        if (files.Count > MaxFiles)
        {
            report.AppendLine($"Only the first {MaxFiles} XAML files were checked.");
        }

        report.AppendLine(all.Count == 0 ? $"{Math.Min(files.Count, MaxFiles)} XAML files, no problems found." : Summary(Math.Min(files.Count, MaxFiles), all));
        return report.ToString();
    }

    [McpServerTool(Name = "xaml_fix", ReadOnly = true, Idempotent = true)]
    [Description("Returns the XAML file's content with the obvious fixes applied (misspelled types, properties, values, " +
        "resource keys and binding paths with a single clear suggestion). Does not write the file. Fixes that need a " +
        "choice or edit another file (adding an xmlns, generating an event handler in code-behind) are listed instead.")]
    public async Task<string> FixAsync(
        [Description("The .xaml file; absolute or relative to the server's root.")] string path,
        [Description("Optional: content to fix instead of what is on disk.")] string? content = null,
        CancellationToken cancellationToken = default)
    {
        var file = await workspace.AnalyzeAsync(path, content, cancellationToken).ConfigureAwait(false);
        var text = file.Document.Text;
        var applied = new List<string>();
        var remaining = new List<string>();
        var edits = new List<XamlTextEdit>();

        foreach (var diagnostic in Diagnostics(file, cancellationToken).Where(d => d.Fixes.Count > 0))
        {
            var position = file.Document.Lines.GetPosition(diagnostic.Span.Start);
            var where = $"({position.Line + 1},{position.Character + 1})";
            var fix = diagnostic.Fixes.Count == 1 && diagnostic.Fixes[0] is { IsPreferred: true } only && only.Edits.All(e => e.FilePath is null)
                && !only.Edits.Any(e => edits.Any(o => o.Span.Start < e.Span.End && e.Span.Start < o.Span.End))
                ? only
                : null;
            if (fix is null)
            {
                remaining.AddRange(diagnostic.Fixes.Select(f => $"{where} {diagnostic.Code}: {f.Title}"));
                continue;
            }

            edits.AddRange(fix.Edits);
            applied.Add($"{where} {diagnostic.Code}: {fix.Title}");
        }

        foreach (var edit in edits.OrderByDescending(e => e.Span.Start))
        {
            text = text.Remove(edit.Span.Start, edit.Span.Length).Insert(edit.Span.Start, edit.NewText);
        }

        var output = new StringBuilder();
        output.AppendLine(applied.Count == 0 ? "No fixes applied." : $"Applied {applied.Count} fix(es):");
        applied.ForEach(a => output.AppendLine("  " + a));
        if (remaining.Count > 0)
        {
            output.AppendLine("Not applied (need a choice or edit another file; apply them yourself if wanted):");
            remaining.ForEach(r => output.AppendLine("  " + r));
        }

        if (applied.Count > 0)
        {
            output.AppendLine().AppendLine("Fixed content:").AppendLine(text);
        }

        return output.ToString();
    }

    [McpServerTool(Name = "xaml_describe_type", ReadOnly = true, Idempotent = true)]
    [Description("Describes how a type can be used in WPF XAML: its xmlns, content property, the properties and events " +
        "that can be set as attributes (with types), and the attached properties it owns (like Grid.Row). Use it before " +
        "writing XAML for a control to avoid guessing member names.")]
    public async Task<string> DescribeTypeAsync(
        [Description("The type: a XAML name (Button, local:MyControl with contextFile) or a full CLR name (MyApp.Controls.MyControl).")] string name,
        [Description("Optional: a .xaml file whose project and xmlns prefixes should be used. Needed for project types and prefixes.")] string? contextFile = null,
        CancellationToken cancellationToken = default)
    {
        var types = await workspace.GetTypeSystemAsync(contextFile, cancellationToken).ConfigureAwait(false);
        if (types is null)
        {
            return "No WPF type information is available (no WPF targeting pack found and no project loaded).";
        }

        var candidates = await FindTypesAsync(types, name, contextFile, cancellationToken).ConfigureAwait(false);
        return candidates.Count switch
        {
            0 => $"Type '{name}' not found." + (contextFile is null ? " For project types, pass contextFile (a .xaml file in that project)." : string.Empty),
            1 => Describe(types, types.GetTypeInfo(candidates[0])),
            _ => "Several types match; ask again with the full name:\n" + string.Join("\n", candidates.Select(c => "  " + c.ToDisplayString())),
        };
    }

    [McpServerTool(Name = "xaml_resources", ReadOnly = true, Idempotent = true)]
    [Description("Lists the resource keys a {StaticResource}/{DynamicResource} can use at a position in a XAML file: " +
        "the element's and its ancestors' resources, merged dictionaries and App.xaml, with each key's type and file.")]
    public async Task<string> ResourcesAsync(
        [Description("The .xaml file; absolute or relative to the server's root.")] string path,
        [Description("Optional 1-based line of the position (default: the root element).")] int? line = null,
        [Description("Optional 1-based column of the position.")] int? column = null,
        CancellationToken cancellationToken = default)
    {
        var file = await workspace.AnalyzeAsync(path, null, cancellationToken).ConfigureAwait(false);
        if (file.Model is null || ElementAt(file, line, column) is not { } element)
        {
            return Status(file);
        }

        var scope = file.Model.GetResourceScope(element);
        var output = new StringBuilder();
        foreach (var definition in scope.AllDefinitions().OrderBy(d => d.Key, StringComparer.OrdinalIgnoreCase))
        {
            var where = definition.FilePath is null ? "this file" : Relative(definition.FilePath);
            output.AppendLine($"{definition.Key}: {definition.ElementName} ({where}:{definition.Start.Line + 1})");
        }

        if (output.Length == 0)
        {
            output.AppendLine("No resource keys in scope.");
        }

        foreach (var reason in scope.IncompleteReasons)
        {
            output.AppendLine($"Note: more keys may exist at run time ({reason}).");
        }

        return output.ToString();
    }

    [McpServerTool(Name = "xaml_binding_context", ReadOnly = true, Idempotent = true)]
    [Description("Tells which data context type {Binding} paths resolve against at a position in a XAML file " +
        "(from d:DataContext, DataTemplate DataType, ItemsSource items or DataContext bindings) and lists the " +
        "properties a binding can use. Use it to write binding paths that exist.")]
    public async Task<string> BindingContextAsync(
        [Description("The .xaml file; absolute or relative to the server's root.")] string path,
        [Description("1-based line of the element.")] int line,
        [Description("1-based column inside the element.")] int column,
        CancellationToken cancellationToken = default)
    {
        var file = await workspace.AnalyzeAsync(path, null, cancellationToken).ConfigureAwait(false);
        if (file.Model is null || ElementAt(file, line, column) is not { } element)
        {
            return Status(file);
        }

        var source = file.Model.Bindings.GetDataContext(element);
        if (source.Type is null)
        {
            return $"The data context at <{element.Name.FullName}> is not known statically: {source.Origin}. " +
                "Declare it with d:DataContext=\"{d:DesignInstance Type=local:MyViewModel}\" to get checked bindings.";
        }

        var output = new StringBuilder();
        var precision = source.Kind switch
        {
            BindingSourceKind.Exact => string.Empty,
            BindingSourceKind.LowerBound => " (or a subclass)",
            _ => " (a hint only: WPF doesn't enforce it here)",
        };
        output.AppendLine($"Data context at <{element.Name.FullName}>: {source.Type.ToDisplayString()}{precision}, from {source.Origin}.");
        output.AppendLine("Bindable properties:");
        foreach (var property in BindingResolver.GetBindableProperties(source.Type).OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            output.AppendLine($"  {property.Name}: {DocumentationFormatter.ShortTypeName(property.Type)}{(property.SetMethod is null ? " (read-only)" : string.Empty)}");
        }

        return output.ToString();
    }

    // ------------------------------------------------------------------ helpers

    private static IReadOnlyList<XamlDiagnostic> Diagnostics(AnalyzedFile file, CancellationToken cancellationToken) =>
        file.Model?.GetDiagnostics(cancellationToken) ?? file.Document.Diagnostics;

    private string Status(AnalyzedFile file) => file.Context.Status switch
    {
        XamlFileStatus.ExcludedFromProject => $"{Relative(file.Path)} is excluded from the build of {Path.GetFileName(file.Context.ProjectFilePath)}; only XML syntax is checked.",
        XamlFileStatus.NoProject => $"{Relative(file.Path)} belongs to no loaded project; checked against the WPF framework types only.",
        _ when file.Model is null => $"{Relative(file.Path)}: no WPF type information is available; only XML syntax is checked.",
        _ => $"{Relative(file.Path)} (project {Path.GetFileNameWithoutExtension(file.Context.ProjectFilePath)})",
    };

    private int AppendDiagnostics(StringBuilder output, AnalyzedFile file, IReadOnlyList<XamlDiagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics.OrderBy(d => d.Span.Start))
        {
            var position = file.Document.Lines.GetPosition(diagnostic.Span.Start);
            var severity = diagnostic.Severity.ToString().ToLowerInvariant();
            output.AppendLine($"{Relative(file.Path)}({position.Line + 1},{position.Character + 1}): {severity} {diagnostic.Code}: {diagnostic.Message}");
            foreach (var fix in diagnostic.Fixes)
            {
                output.AppendLine($"    fix: {fix.Title}");
            }
        }

        return diagnostics.Count;
    }

    private static string Summary(int files, IReadOnlyList<XamlDiagnostic> diagnostics) =>
        $"{files} XAML file(s): {diagnostics.Count(d => d.Severity == XamlDiagnosticSeverity.Error)} error(s), " +
        $"{diagnostics.Count(d => d.Severity == XamlDiagnosticSeverity.Warning)} warning(s).";

    private string Relative(string path) => Path.GetRelativePath(workspace.Root, path);

    private static XamlElement? ElementAt(AnalyzedFile file, int? line, int? column)
    {
        if (line is null)
        {
            return file.Document.Root;
        }

        var offset = file.Document.Lines.GetOffset(line.Value - 1, (column ?? 1) - 1);
        return file.Document.AllElements().Where(e => e.Span.ContainsOrTouches(offset)).MaxBy(e => e.Span.Start) ?? file.Document.Root;
    }

    private async Task<IReadOnlyList<INamedTypeSymbol>> FindTypesAsync(XamlTypeSystem types, string name, string? contextFile, CancellationToken cancellationToken)
    {
        name = name.Trim().Trim('<', '>', '/').Trim();
        if (name.Contains(':'))
        {
            if (contextFile is null)
            {
                return [];
            }

            var file = await workspace.AnalyzeAsync(contextFile, null, cancellationToken).ConfigureAwait(false);
            return file.Model is not null && file.Document.Root is { } root && file.Model.ResolveTypeReference(root, name) is { } resolved
                ? [resolved.Symbol]
                : [];
        }

        if (name.Contains('.') && types.Compilation.GetTypeByMetadataName(name) is { } byFullName)
        {
            return [byFullName];
        }

        if (types.GetNamespace(XmlNamespaces.WpfPresentation)?.FindElementType(name) is { } wpf)
        {
            return [wpf.Symbol];
        }

        return types.FindTypesByName(name).Take(10).ToList();
    }

    private static string Describe(XamlTypeSystem types, XamlTypeInfo type)
    {
        var symbol = type.Symbol;
        var output = new StringBuilder();
        var bases = symbol.BaseTypesAndSelf().Skip(1).TakeWhile(b => b.SpecialType != SpecialType.System_Object).Select(b => b.Name);
        output.AppendLine($"{symbol.ToDisplayString()}" + (bases.Any() ? $" : {string.Join(" : ", bases)}" : string.Empty));
        output.AppendLine($"xmlns: {types.GetXmlns(symbol)}");
        if (DocumentationFormatter.GetSummary(symbol) is { } summary)
        {
            output.AppendLine($"Summary: {summary}");
        }

        output.AppendLine(type.IsCreatable ? "Can be used as an element." : "Cannot be used as an element (abstract, static, or no public parameterless constructor).");
        if (type.IsMarkupExtension)
        {
            output.AppendLine($"Markup extension: {{{type.XamlName} …}}");
        }

        if (type.ContentPropertyName is { } content)
        {
            output.AppendLine($"Content property: {content} (child elements set it)");
        }

        AppendMembers(output, "Properties", type, type.Members.Where(m => !m.IsEvent && !m.IsReadOnly));
        AppendMembers(output, "Read-only properties (collections can be filled with property elements)", type, type.Members.Where(m => !m.IsEvent && m.IsReadOnly));
        AppendMembers(output, "Events", type, type.Members.Where(m => m.IsEvent));

        var attached = type.AttachableMembers.Where(m => SymbolEqualityComparer.Default.Equals(m.Symbol.ContainingType, symbol)).OrderBy(m => m.Name).ToList();
        if (attached.Count > 0)
        {
            output.AppendLine($"Attached properties and events it owns (use as {type.Name}.Name on other elements):");
            attached.ForEach(m => output.AppendLine($"  {type.Name}.{m.Name}: {DocumentationFormatter.ShortTypeName(m.ValueType)}{(m.IsEvent ? " (event)" : string.Empty)}"));
        }

        return output.ToString();
    }

    /// <summary>Members declared on the type with their types; inherited ones by name, grouped by declaring type.</summary>
    private static void AppendMembers(StringBuilder output, string title, XamlTypeInfo type, IEnumerable<XamlMember> members)
    {
        var list = members.ToList();
        if (list.Count == 0)
        {
            return;
        }

        output.AppendLine($"{title}:");
        foreach (var group in list.GroupBy(m => m.Symbol.ContainingType, SymbolEqualityComparer.Default)
                     .OrderBy(g => SymbolEqualityComparer.Default.Equals(g.Key, type.Symbol) ? 0 : 1))
        {
            var owner = (INamedTypeSymbol)group.Key!;
            if (SymbolEqualityComparer.Default.Equals(owner, type.Symbol))
            {
                foreach (var member in group.OrderBy(m => m.Name, StringComparer.Ordinal))
                {
                    output.AppendLine($"  {member.Name}: {DocumentationFormatter.ShortTypeName(member.ValueType)}");
                }
            }
            else
            {
                output.AppendLine($"  from {owner.Name}: {string.Join(", ", group.Select(m => m.Name).Order(StringComparer.Ordinal))}");
            }
        }
    }

    private static IEnumerable<string> EnumerateXaml(string folder)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        return Directory.EnumerateFiles(folder, "*.xaml", options)
            .Where(p => !p.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is "bin" or "obj" or ".git" or ".vs"))
            .Order(StringComparer.OrdinalIgnoreCase);
    }
}
