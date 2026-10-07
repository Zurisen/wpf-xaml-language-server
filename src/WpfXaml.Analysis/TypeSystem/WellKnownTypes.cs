using Microsoft.CodeAnalysis;

namespace WpfXaml.Analysis.TypeSystem;

/// <summary>WPF and BCL types the analysis depends on. Any of them can be null in a compilation without WPF.</summary>
public sealed class WellKnownTypes
{
    public WellKnownTypes(Compilation compilation)
    {
        INamedTypeSymbol? Get(string name) => compilation.GetTypeByMetadataName(name);

        DependencyObject = Get("System.Windows.DependencyObject");
        DependencyProperty = Get("System.Windows.DependencyProperty");
        RoutedEvent = Get("System.Windows.RoutedEvent");
        FrameworkTemplate = Get("System.Windows.FrameworkTemplate");
        ResourceDictionary = Get("System.Windows.ResourceDictionary");
        Style = Get("System.Windows.Style");
        MarkupExtension = Get("System.Windows.Markup.MarkupExtension");
        ContentPropertyAttribute = Get("System.Windows.Markup.ContentPropertyAttribute");
        XmlnsDefinitionAttribute = Get("System.Windows.Markup.XmlnsDefinitionAttribute");
        XmlnsPrefixAttribute = Get("System.Windows.Markup.XmlnsPrefixAttribute");
        RuntimeNamePropertyAttribute = Get("System.Windows.Markup.RuntimeNamePropertyAttribute");
        StaticResourceExtension = Get("System.Windows.StaticResourceExtension");
        DynamicResourceExtension = Get("System.Windows.DynamicResourceExtension");
        IList = Get("System.Collections.IList");
        IDictionary = Get("System.Collections.IDictionary");
        IAddChild = Get("System.Windows.Markup.IAddChild");
        Type = Get("System.Type");
    }

    public INamedTypeSymbol? DependencyObject { get; }
    public INamedTypeSymbol? DependencyProperty { get; }
    public INamedTypeSymbol? RoutedEvent { get; }
    public INamedTypeSymbol? FrameworkTemplate { get; }
    public INamedTypeSymbol? ResourceDictionary { get; }
    public INamedTypeSymbol? Style { get; }
    public INamedTypeSymbol? MarkupExtension { get; }
    public INamedTypeSymbol? ContentPropertyAttribute { get; }
    public INamedTypeSymbol? XmlnsDefinitionAttribute { get; }
    public INamedTypeSymbol? XmlnsPrefixAttribute { get; }
    public INamedTypeSymbol? RuntimeNamePropertyAttribute { get; }
    public INamedTypeSymbol? StaticResourceExtension { get; }
    public INamedTypeSymbol? DynamicResourceExtension { get; }
    public INamedTypeSymbol? IList { get; }
    public INamedTypeSymbol? IDictionary { get; }
    public INamedTypeSymbol? IAddChild { get; }
    public INamedTypeSymbol? Type { get; }
}

public static class SymbolExtensions
{
    public static bool DerivesFrom(this ITypeSymbol type, ITypeSymbol? baseType)
    {
        if (baseType is null)
        {
            return false;
        }

        for (var t = type; t is not null; t = t.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(t, baseType))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Class inheritance or interface implementation.</summary>
    public static bool IsAssignableTo(this ITypeSymbol type, ITypeSymbol? target)
    {
        if (target is null)
        {
            return false;
        }

        if (target.SpecialType == SpecialType.System_Object || type.DerivesFrom(target))
        {
            return true;
        }

        return target.TypeKind == TypeKind.Interface
            && (SymbolEqualityComparer.Default.Equals(type, target) || type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, target)));
    }

    public static IEnumerable<INamedTypeSymbol> BaseTypesAndSelf(this INamedTypeSymbol type)
    {
        for (var t = type; t is not null; t = t.BaseType)
        {
            yield return t;
        }
    }

    public static ITypeSymbol UnwrapNullable(this ITypeSymbol type) =>
        type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } n ? n.TypeArguments[0] : type;

    public static bool HasSourceLocation(this ISymbol symbol) => symbol.Locations.Any(l => l.IsInSource);
}
