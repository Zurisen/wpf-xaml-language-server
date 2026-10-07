namespace WpfXaml.Analysis.TypeSystem;

public enum DirectiveUsage
{
    /// <summary>Allowed on any object element.</summary>
    AnyElement,

    /// <summary>Only on the root element.</summary>
    Root,

    /// <summary>A directive element such as <c>x:Code</c>.</summary>
    Element,
}

public sealed record XamlDirective(string Name, string Description, DirectiveUsage Usage = DirectiveUsage.AnyElement);

/// <summary>The <c>x:</c> (XAML 2006 language namespace) directives supported by WPF.</summary>
public static class XamlDirectives
{
    public static readonly IReadOnlyList<XamlDirective> All =
    [
        new("Name", "Uniquely identifies the element in its XAML namescope. The markup compiler creates a field with this name in the x:Class."),
        new("Key", "Identifies the element in the parent ResourceDictionary. Referenced with {StaticResource} or {DynamicResource}."),
        new("Uid", "Unique identifier used by the localization process."),
        new("Shared", "When False, a resource lookup creates a new instance instead of returning a shared one."),
        new("FieldModifier", "Access modifier of the field generated for x:Name (public, internal, private, protected)."),
        new("TypeArguments", "Generic type arguments for the root element type (only valid together with x:Class)."),
        new("Arguments", "Constructor arguments for the element (XAML 2009; limited support in compiled WPF XAML)."),
        new("FactoryMethod", "Name of a static factory method used to create the element (XAML 2009)."),
        new("Class", "The partial class, in code-behind, that this XAML file is compiled into.", DirectiveUsage.Root),
        new("ClassModifier", "Access modifier for the class generated from x:Class (public or internal).", DirectiveUsage.Root),
        new("Subclass", "Name of the subclass to generate, for languages without partial classes.", DirectiveUsage.Root),
        new("Code", "Inline code block compiled into the x:Class.", DirectiveUsage.Element),
        new("XData", "Inline XML data island, typically for XmlDataProvider.", DirectiveUsage.Element),
    ];

    private static readonly Dictionary<string, XamlDirective> ByName = All.ToDictionary(d => d.Name, StringComparer.Ordinal);

    public static XamlDirective? Find(string name) => ByName.GetValueOrDefault(name);

    /// <summary>Design-time attributes in the Blend namespace (<c>d:</c>) worth offering in completion.</summary>
    public static readonly IReadOnlyList<(string Name, string Description)> DesignTimeAttributes =
    [
        ("DataContext", "Design-time data context, used by designers and by binding checks (e.g. {d:DesignInstance Type=local:MainViewModel})."),
        ("DesignWidth", "Width used by the designer."),
        ("DesignHeight", "Height used by the designer."),
        ("DesignSource", "Design-time source for a CollectionViewSource."),
    ];
}
