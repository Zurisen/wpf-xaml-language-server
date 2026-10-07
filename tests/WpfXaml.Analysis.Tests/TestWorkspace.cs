using Microsoft.CodeAnalysis.CSharp;
using WpfXaml.Analysis.Framework;
using WpfXaml.Analysis.Semantics;
using WpfXaml.Analysis.TypeSystem;
using WpfXaml.Language.Syntax;

namespace WpfXaml.Analysis.Tests;

/// <summary>
/// Builds type systems from the WPF targeting pack installed on this machine plus optional C# sources
/// standing in for a project. Tests are skipped when no WindowsDesktop targeting pack is found.
/// </summary>
internal static class TestWorkspace
{
    public const string Header =
        """xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:local="clr-namespace:TestApp" """;

    /// <summary>A small "project": code-behind for MainWindow, a view model, a converter and an attached property.</summary>
    public const string ProjectSource =
        """
        using System;
        using System.Globalization;
        using System.Windows;
        using System.Windows.Controls;
        using System.Windows.Data;

        namespace TestApp
        {
            /// <summary>The main window.</summary>
            public partial class MainWindow : Window
            {
                private void OnOkClick(object sender, RoutedEventArgs e) { }
                private void OnLoaded(object sender, RoutedEventArgs e) { }
            }

            public class MainViewModel
            {
                public string Title { get; set; } = "";
            }

            public class BoolToVisibilityConverter : IValueConverter
            {
                public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value;
                public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value;
            }

            public static class Behaviors
            {
                public static readonly DependencyProperty IsHighlightedProperty =
                    DependencyProperty.RegisterAttached("IsHighlighted", typeof(bool), typeof(Behaviors));

                /// <summary>Gets whether the element is highlighted.</summary>
                public static bool GetIsHighlighted(DependencyObject o) => (bool)o.GetValue(IsHighlightedProperty);
                public static void SetIsHighlighted(DependencyObject o, bool value) => o.SetValue(IsHighlightedProperty, value);
            }

            public class FancyButton : Button
            {
                public int Fanciness { get; set; }
            }
        }
        """;

    private static readonly Lazy<IReadOnlyList<string>> References = new(() => FrameworkReferences.FindWpfReferencePaths());
    private static readonly Lazy<XamlTypeSystem> ProjectTypes = new(() => CreateTypeSystem(ProjectSource));
    private static readonly Lazy<XamlTypeSystem> FrameworkTypes = new(() =>
        XamlTypeSystem.Create(FrameworkReferences.CreateFrameworkCompilation(References.Value), isFrameworkOnly: true));

    public static bool IsAvailable => References.Value.Count > 0;

    public static XamlTypeSystem Project
    {
        get
        {
            Assert.SkipUnless(IsAvailable, "No WindowsDesktop targeting pack found.");
            return ProjectTypes.Value;
        }
    }

    public static XamlTypeSystem FrameworkOnly
    {
        get
        {
            Assert.SkipUnless(IsAvailable, "No WindowsDesktop targeting pack found.");
            return FrameworkTypes.Value;
        }
    }

    public static XamlTypeSystem CreateTypeSystem(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, path: "C:/TestApp/MainWindow.xaml.cs");
        var compilation = FrameworkReferences.CreateFrameworkCompilation(References.Value, [tree], "TestApp");
        return XamlTypeSystem.Create(compilation);
    }

    public static XamlSemanticModel Model(string xaml, XamlTypeSystem? types = null) =>
        new(XamlDocument.Parse(xaml), types ?? Project);

    /// <summary>Splits a <c>|</c> caret marker out of the text.</summary>
    public static (string Text, int Offset) Caret(string textWithCaret)
    {
        var offset = textWithCaret.IndexOf('|');
        Assert.True(offset >= 0, "Missing | caret marker.");
        return (textWithCaret.Remove(offset, 1), offset);
    }
}
