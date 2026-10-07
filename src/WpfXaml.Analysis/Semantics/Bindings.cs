using WpfXaml.Analysis.TypeSystem;
using WpfXaml.Language;

namespace WpfXaml.Analysis.Semantics;

public enum ElementKind
{
    /// <summary><c>&lt;Button&gt;</c>: creates an instance of <see cref="ElementInfo.Type"/>.</summary>
    Object,

    /// <summary><c>&lt;Button.Content&gt;</c>: sets <see cref="ElementInfo.Member"/> on the parent object.</summary>
    PropertyElement,

    /// <summary><c>&lt;x:Code&gt;</c>, <c>&lt;x:XData&gt;</c>.</summary>
    Directive,

    /// <summary>In a namespace listed in <c>mc:Ignorable</c>, or nested in such an element.</summary>
    Ignored,

    /// <summary>Could not be resolved. <see cref="ElementInfo.Error"/> says why, unless the failure is not reportable.</summary>
    Unknown,
}

public sealed record ElementInfo(
    ElementKind Kind,
    XamlTypeInfo? Type = null,
    XamlMember? Member = null,
    XamlTypeInfo? OwnerType = null,
    XamlDirective? Directive = null,
    XamlDiagnostic? Error = null);

public enum AttributeKind
{
    Member,
    Directive,
    NamespaceDeclaration,

    /// <summary><c>xml:space</c>, <c>xml:lang</c>.</summary>
    XmlAttribute,
    Ignored,
    Unknown,
}

public sealed record AttributeInfo(
    AttributeKind Kind,
    XamlMember? Member = null,
    XamlDirective? Directive = null,
    XamlDiagnostic? Error = null);
