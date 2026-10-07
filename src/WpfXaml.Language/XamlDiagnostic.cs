using WpfXaml.Language.Text;

namespace WpfXaml.Language;

public enum XamlDiagnosticSeverity
{
    Error,
    Warning,
    Information,
    Hint,
}

public sealed record XamlDiagnostic(string Code, string Message, TextSpan Span, XamlDiagnosticSeverity Severity);

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
}
