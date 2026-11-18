using WpfXaml.Language.Text;

namespace WpfXaml.Language;

public enum XamlDiagnosticSeverity
{
    Error,
    Warning,
    Information,
    Hint,
}

public sealed record XamlDiagnostic(string Code, string Message, TextSpan Span, XamlDiagnosticSeverity Severity)
{
    /// <summary>Quick fixes for this diagnostic, offered as code actions.</summary>
    public IReadOnlyList<XamlFix> Fixes { get; init; } = [];

    public XamlDiagnostic WithFix(XamlFix? fix) => fix is null ? this : this with { Fixes = [.. Fixes, fix] };
}

/// <summary>A quick fix: a titled set of edits, applied together.</summary>
/// <param name="IsPreferred">The obvious fix (e.g. the single spelling suggestion), which editors may apply with one key.</param>
public sealed record XamlFix(string Title, IReadOnlyList<XamlTextEdit> Edits, bool IsPreferred = false)
{
    public static XamlFix Replace(string title, TextSpan span, string newText, bool isPreferred = true) =>
        new(title, [new XamlTextEdit(span, newText)], isPreferred);
}

/// <summary>
/// A text edit. <see cref="Span"/> applies to the document being analyzed; for an edit in another file,
/// <see cref="FilePath"/> is set and <see cref="Start"/>/<see cref="End"/> locate it instead.
/// </summary>
public sealed record XamlTextEdit(TextSpan Span, string NewText)
{
    public string? FilePath { get; init; }

    public LinePosition Start { get; init; }

    public LinePosition End { get; init; }

    public static XamlTextEdit InFile(string filePath, LinePosition start, LinePosition end, string newText) =>
        new(default, newText) { FilePath = filePath, Start = start, End = end };
}

/// <summary>Diagnostic codes. Syntax codes are 0xxx, semantic codes are 1xxx.</summary>
public static class DiagnosticCodes
{
    public const string Syntax = "XLS0001";
    public const string MarkupExtensionSyntax = "XLS0002";

    public const string UnknownNamespacePrefix = "XLS1001";
    public const string UnknownType = "XLS1002";
    public const string UnknownMember = "XLS1003";
    public const string InvalidValue = "XLS1004";
    public const string DuplicateName = "XLS1005";
    public const string UnknownMarkupExtension = "XLS1006";
    public const string ReadOnlyProperty = "XLS1007";
    public const string EventHandlerNotFound = "XLS1008";
    public const string ClassNotFound = "XLS1009";
    public const string UnresolvedAssembly = "XLS1010";
    public const string UnknownDirective = "XLS1011";
    public const string PropertyElementOwnerMismatch = "XLS1012";
    public const string ResourceDictionaryNotFound = "XLS1013";
    public const string ResourceNotFound = "XLS1014";
    public const string BindingPathNotFound = "XLS1015";
    public const string TemplateBindingPropertyNotFound = "XLS1016";

    /// <summary>Informational: the file is excluded from its project's build, so semantic checks are skipped.</summary>
    public const string ExcludedFromBuild = "XLS9001";
}
