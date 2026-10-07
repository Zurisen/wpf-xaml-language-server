using Microsoft.CodeAnalysis;
using WpfXaml.Analysis.TypeSystem;
using WpfXaml.Language.Syntax;

namespace WpfXaml.Analysis.Tests;

public class TypeSystemTests
{
    [Fact]
    public void PresentationNamespace_ResolvesWpfTypes()
    {
        var ns = TestWorkspace.Project.GetNamespace(XmlNamespaces.WpfPresentation);

        Assert.NotNull(ns);
        Assert.Equal("System.Windows.Controls.Button", ns.FindType("Button")?.Symbol.ToDisplayString());
        Assert.Equal("System.Windows.Controls.Grid", ns.FindType("Grid")?.Symbol.ToDisplayString());
        Assert.Equal("System.Windows.Media.SolidColorBrush", ns.FindType("SolidColorBrush")?.Symbol.ToDisplayString());
        Assert.Equal("System.Windows.Data.Binding", ns.FindMarkupExtension("Binding")?.Symbol.ToDisplayString());
        Assert.Equal("System.Windows.StaticResourceExtension", ns.FindMarkupExtension("StaticResource")?.Symbol.ToDisplayString());
    }

    [Fact]
    public void XamlLanguageNamespace_ResolvesMarkupExtensions()
    {
        var x = TestWorkspace.Project.GetNamespace(XmlNamespaces.Xaml2006);

        Assert.NotNull(x);
        Assert.Equal("System.Windows.Markup.TypeExtension", x.FindMarkupExtension("Type")?.Symbol.ToDisplayString());
        Assert.Equal("System.Windows.Markup.StaticExtension", x.FindMarkupExtension("Static")?.Symbol.ToDisplayString());
        Assert.Equal("System.Windows.Markup.NullExtension", x.FindMarkupExtension("Null")?.Symbol.ToDisplayString());
    }

    [Fact]
    public void ClrNamespace_ResolvesProjectAndFrameworkTypes()
    {
        var types = TestWorkspace.Project;

        Assert.NotNull(types.GetNamespace("clr-namespace:TestApp")?.FindType("MainViewModel"));
        Assert.Equal(SpecialType.System_String, types.GetNamespace("clr-namespace:System;assembly=mscorlib")?.FindType("String")?.Symbol.SpecialType);
        Assert.Equal(SpecialType.System_String, types.GetNamespace("clr-namespace:System;assembly=System.Runtime")?.FindType("String")?.Symbol.SpecialType);
        Assert.NotNull(types.ResolveNamespace("clr-namespace:Foo;assembly=Missing").Error);
    }

    [Fact]
    public void Members_IncludeInheritedPropertiesEventsAndContentProperty()
    {
        var button = TestWorkspace.Project.GetNamespace(XmlNamespaces.WpfPresentation)!.FindType("Button")!;

        Assert.Equal(XamlMemberKind.Property, button.GetMember("Content")?.Kind);
        Assert.Equal(XamlMemberKind.Property, button.GetMember("Margin")?.Kind);
        Assert.Equal(XamlMemberKind.Event, button.GetMember("Click")?.Kind);
        Assert.True(button.GetMember("ActualWidth")?.IsReadOnly);
        Assert.Equal("Content", button.ContentPropertyName);
        Assert.Equal("Name", button.RuntimeNamePropertyName);
        Assert.True(button.IsCreatable);
    }

    [Fact]
    public void AttachedMembers_AreDiscoveredFromStaticAccessors()
    {
        var types = TestWorkspace.Project;
        var presentation = types.GetNamespace(XmlNamespaces.WpfPresentation)!;

        var row = presentation.FindType("Grid")!.GetAttachableMember("Row");
        Assert.Equal(XamlMemberKind.AttachedProperty, row?.Kind);
        Assert.Equal("int", row?.ValueType?.ToDisplayString());

        Assert.Equal(XamlMemberKind.AttachedEvent, presentation.FindType("Mouse")?.GetAttachableMember("MouseDown")?.Kind);
        Assert.NotNull(types.GetNamespace("clr-namespace:TestApp")!.FindType("Behaviors")!.GetAttachableMember("IsHighlighted"));
    }

    [Fact]
    public void Namespace_EnumeratesCreatableTypes()
    {
        var presentation = TestWorkspace.Project.GetNamespace(XmlNamespaces.WpfPresentation)!;
        var names = presentation.CreatableTypes.Select(t => t.Name).ToHashSet();

        Assert.Contains("Button", names);
        Assert.Contains("StackPanel", names);
        Assert.DoesNotContain("Panel", names); // abstract
        Assert.True(names.Count > 300, $"Only {names.Count} creatable types found.");
    }
}
