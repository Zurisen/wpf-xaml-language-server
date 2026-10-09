using WpfXaml.Analysis.Features;
using WpfXaml.Analysis.Features.Completion;
using WpfXaml.Analysis.Semantics;
using WpfXaml.Analysis.TypeSystem;
using WpfXaml.Language;
using WpfXaml.Language.Syntax;

namespace WpfXaml.Analysis.Tests;

public class BindingTests
{
    private const string Source = """
        using System.Collections.Generic;
        using System.Windows;
        using System.Windows.Input;

        namespace BindApp
        {
            public class MainViewModel
            {
                public string Title { get; set; } = "";
                public Customer Customer { get; set; } = new();
                public List<Customer> Customers { get; } = new();
                public ICommand SaveCommand => null!;
                public Animal Pet { get; set; } = new();
            }

            public class Customer
            {
                /// <summary>The customer's full name.</summary>
                public string Name { get; set; } = "";
                public Address Address { get; set; } = new();
            }

            public class Address { public string City { get; set; } = ""; }

            public class Animal { public string Name { get; set; } = ""; }

            public class Dog : Animal { public bool Barks { get; set; } }

            public partial class MainWindow : Window { public int Fanciness { get; set; } }

            public class Commands { public static string Paste => ""; }

            public class DpOnly : FrameworkElement
            {
                public static readonly DependencyProperty LevelProperty = DependencyProperty.Register("Level", typeof(int), typeof(DpOnly));
            }
        }
        """;

    private static readonly Lazy<XamlTypeSystem> Types = new(() => TestWorkspace.CreateTypeSystem(Source.Replace("namespace BindApp", "namespace TestApp")));

    private const string Root = """<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:d="http://schemas.microsoft.com/expression/blend/2008" xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006" mc:Ignorable="d" xmlns:local="clr-namespace:TestApp" x:Class="TestApp.MainWindow" x:Name="Root" """;

    private static XamlSemanticModel Model(string xaml, XamlTypeSystem? types = null)
    {
        Assert.SkipUnless(TestWorkspace.IsAvailable, "No WindowsDesktop targeting pack found.");
        return new XamlSemanticModel(XamlDocument.Parse(xaml), types ?? Types.Value);
    }

    private static string Window(string body, string rootAttributes = """d:DataContext="{d:DesignInstance Type=local:MainViewModel}" """) =>
        $"{Root}{rootAttributes}>\n{body}\n</Window>";

    private static List<XamlDiagnostic> BindingDiagnostics(string xaml, XamlTypeSystem? types = null) =>
        Model(xaml, types).GetDiagnostics().Where(d => d.Code is DiagnosticCodes.BindingPathNotFound or DiagnosticCodes.TemplateBindingPropertyNotFound).ToList();

    [Fact]
    public void DesignDataContext_ValidPaths_HaveNoWarnings()
    {
        Assert.Empty(BindingDiagnostics(Window("""
            <StackPanel>
              <TextBlock Text="{Binding Title}" />
              <TextBlock Text="{Binding Path=Customer.Address.City, Mode=OneWay}" />
              <TextBlock Text="{Binding Customers[0].Name}" />
              <TextBlock Text="{Binding Customers/Name}" />
              <TextBlock Text="{Binding (Grid.Row)}" />
              <TextBlock Text="{Binding}" />
              <TextBlock Text="{Binding Title.Length}" />
            </StackPanel>
            """)));
    }

    [Fact]
    public void DesignDataContext_Typo_IsReportedWithSuggestionAndOrigin()
    {
        var diagnostic = Assert.Single(BindingDiagnostics(Window("""<TextBlock Text="{Binding Titel}" />""")));

        Assert.Equal(DiagnosticCodes.BindingPathNotFound, diagnostic.Code);
        Assert.Equal(XamlDiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains("'Titel' is not a property of 'MainViewModel' (data context from d:DataContext). Did you mean 'Title'?", diagnostic.Message);
    }

    [Fact]
    public void NestedTypo_IsReportedForProjectTypes_ButNotWhenASubclassHasIt()
    {
        var diagnostic = Assert.Single(BindingDiagnostics(Window("""<TextBlock Text="{Binding Customer.Nmae}" />""")));
        Assert.Contains("'Nmae' is not a property of 'Customer'", diagnostic.Message);

        // Pet is declared as Animal; at run time it may be a Dog.
        Assert.Empty(BindingDiagnostics(Window("""<CheckBox IsChecked="{Binding Pet.Barks}" />""")));

        // ICommand is a framework interface: the run-time command may have anything.
        Assert.Empty(BindingDiagnostics(Window("""<TextBlock Text="{Binding SaveCommand.Text}" />""")));
    }

    [Fact]
    public void NoDeclaredDataContext_IsNeverReported()
    {
        Assert.Empty(BindingDiagnostics(Window("""<TextBlock Text="{Binding Anything.At.All}" />""", rootAttributes: "")));
    }

    [Fact]
    public void ElementName_UsesTheElementType_AndTheXClassForTheRoot()
    {
        var diagnostics = BindingDiagnostics(Window("""
            <StackPanel>
              <Button x:Name="Ok" />
              <TextBlock Text="{Binding ActualWidth, ElementName=Ok}" />
              <TextBlock Text="{Binding Fanciness, ElementName=Root}" />
              <TextBlock Text="{Binding Bogus, ElementName=Ok}" />
            </StackPanel>
            """));

        Assert.Contains("'Bogus' is not a property of 'Button'", Assert.Single(diagnostics).Message);
    }

    [Fact]
    public void RelativeSources_AreLowerBounds()
    {
        Assert.Empty(BindingDiagnostics(Window("""
            <StackPanel>
              <TextBlock Text="{Binding Fanciness, RelativeSource={RelativeSource AncestorType=Window}}" />
              <TextBlock Text="{Binding ActualWidth, RelativeSource={RelativeSource Self}}" />
              <TextBlock>
                <TextBlock.Style>
                  <Style TargetType="TextBlock">
                    <Style.Triggers>
                      <DataTrigger Binding="{Binding IsMouseOver, RelativeSource={RelativeSource Self}}" Value="True" />
                    </Style.Triggers>
                  </Style>
                </TextBlock.Style>
              </TextBlock>
            </StackPanel>
            """)));
    }

    [Fact]
    public void DataTemplates_ImplicitDataTypeCounts_KeyedAndXmlDataTypesDont()
    {
        var diagnostics = BindingDiagnostics(Window("""
            <Grid>
              <Grid.Resources>
                <DataTemplate DataType="{x:Type local:Customer}"><TextBlock Text="{Binding Nme}" /></DataTemplate>
                <DataTemplate x:Key="Keyed" DataType="{x:Type local:Customer}"><TextBlock Text="{Binding Anything}" /></DataTemplate>
                <DataTemplate DataType="Point"><TextBlock Text="{Binding Tag}" /></DataTemplate>
              </Grid.Resources>
            </Grid>
            """));

        Assert.Contains("'Nme' is not a property of 'Customer'", Assert.Single(diagnostics).Message);
    }

    [Fact]
    public void ItemTemplate_UsesTheItemsSourceItemType()
    {
        var diagnostics = BindingDiagnostics(Window("""
            <ItemsControl ItemsSource="{Binding Customers}">
              <ItemsControl.ItemTemplate>
                <DataTemplate DataType="{x:Type local:Address}">
                  <StackPanel><TextBlock Text="{Binding Name}" /><TextBlock Text="{Binding Nam}" /></StackPanel>
                </DataTemplate>
              </ItemsControl.ItemTemplate>
            </ItemsControl>
            """));

        Assert.Contains("'Nam' is not a property of 'Customer'", Assert.Single(diagnostics).Message);
    }

    [Fact]
    public void DataContextBinding_ChangesTheContextForDescendants()
    {
        var diagnostics = BindingDiagnostics(Window("""
            <StackPanel DataContext="{Binding Customer}">
              <TextBlock Text="{Binding Name}" />
              <TextBlock Text="{Binding Title}" />
            </StackPanel>
            """));

        Assert.Contains("'Title' is not a property of 'Customer'", Assert.Single(diagnostics).Message);
    }

    [Fact]
    public void StaticPropertiesAndDependencyPropertiesWithoutWrappers_Resolve()
    {
        Assert.Empty(BindingDiagnostics(Window("""
            <StackPanel>
              <StackPanel.Resources><local:Commands x:Key="Commands" /></StackPanel.Resources>
              <TextBlock Text="{Binding Paste, Source={StaticResource Commands}}" />
              <local:DpOnly x:Name="Dp" />
              <TextBlock Text="{Binding Level, ElementName=Dp}" />
            </StackPanel>
            """)));
    }

    [Fact]
    public void GeneratorFreeFirstPass_ReportsNothing()
    {
        var compilation = Types.Value.Compilation;
        var quick = XamlTypeSystem.Create(compilation, hasGeneratedCode: false);

        Assert.Empty(BindingDiagnostics(Window("""<TextBlock Text="{Binding Titel}" />"""), quick));
    }

    [Fact]
    public void TemplateBinding_UnknownProperty_IsReported()
    {
        var diagnostics = BindingDiagnostics(Window("""
            <Window.Resources>
              <ControlTemplate x:Key="T" TargetType="Button">
                <Border Background="{TemplateBinding Background}" Padding="{TemplateBinding Paddding}" />
              </ControlTemplate>
            </Window.Resources>
            """));

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(DiagnosticCodes.TemplateBindingPropertyNotFound, diagnostic.Code);
        Assert.Contains("Did you mean 'Padding'?", diagnostic.Message);
    }

    [Fact]
    public void Completion_OffersPropertiesOfThePathSoFar_AndElementNames()
    {
        static HashSet<string> Labels(string body)
        {
            var (text, offset) = TestWorkspace.Caret(Window(body));
            return CompletionService.GetCompletions(Model(text), offset)!.Items.Select(i => i.Label).ToHashSet();
        }

        Assert.Superset(new HashSet<string> { "Title", "Customer", "Customers" }, Labels("""<TextBlock Text="{Binding |}" />"""));
        Assert.Superset(new HashSet<string> { "Name", "Address" }, Labels("""<TextBlock Text="{Binding Customer.|}" />"""));
        Assert.Contains("City", Labels("""<TextBlock Text="{Binding Path=Customer.Address.Ci|}" />"""));
        Assert.Contains("Ok", Labels("""<StackPanel><Button x:Name="Ok" /><TextBlock Text="{Binding ActualWidth, ElementName=|}" /></StackPanel>"""));
    }

    [Fact]
    public void Completion_ReplacesOnlyTheCurrentSegment()
    {
        var (text, offset) = TestWorkspace.Caret(Window("""<TextBlock Text="{Binding Customer.Na|}" />"""));
        var list = CompletionService.GetCompletions(Model(text), offset)!;

        Assert.Equal("Na", text.Substring(list.ReplaceSpan.Start, list.ReplaceSpan.Length));
    }

    [Fact]
    public void HoverAndDefinition_FollowPathSegments()
    {
        var (text, offset) = TestWorkspace.Caret(Window("""<TextBlock Text="{Binding Customer.Na|me}" />"""));
        var model = Model(text);

        var hover = HoverService.GetHover(model, offset)!.Markdown;
        Assert.Contains("string Customer.Name", hover);
        Assert.Contains("The customer's full name.", hover);

        var location = Assert.Single(DefinitionService.GetDefinitions(model, offset));
        Assert.EndsWith("MainWindow.xaml.cs", location.FilePath);

        var (text2, offset2) = TestWorkspace.Caret(Window("""<TextBlock Text="{Binding Cust|omer.Name}" />"""));
        Assert.Contains("Data context: `MainViewModel (from d:DataContext)`", HoverService.GetHover(Model(text2), offset2)!.Markdown);
    }
}
