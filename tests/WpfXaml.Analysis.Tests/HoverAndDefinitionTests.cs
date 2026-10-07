using WpfXaml.Analysis.Features;

namespace WpfXaml.Analysis.Tests;

public class HoverAndDefinitionTests
{
    private static (Semantics.XamlSemanticModel Model, int Offset) At(string bodyWithCaret)
    {
        var (text, offset) = TestWorkspace.Caret($"<Window {TestWorkspace.Header} x:Class=\"TestApp.MainWindow\">\n{bodyWithCaret}\n</Window>");
        return (TestWorkspace.Model(text), offset);
    }

    [Fact]
    public void Hover_ElementShowsTypeAndDocumentation()
    {
        var (model, offset) = At("<But|ton />");
        var hover = HoverService.GetHover(model, offset);

        Assert.NotNull(hover);
        Assert.Contains("class System.Windows.Controls.Button", hover.Markdown);
        Assert.Contains("button", hover.Markdown.Split("```")[^1], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Hover_PropertyShowsSignature()
    {
        var (model, offset) = At("<Button Mar|gin=\"1\" />");

        Assert.Contains("Thickness FrameworkElement.Margin", HoverService.GetHover(model, offset)!.Markdown);
    }

    [Fact]
    public void Hover_AttachedPropertyAndOwner()
    {
        var (model, offset) = At("<Button Grid.Ro|w=\"1\" />");
        Assert.Contains("attached property int Grid.Row", HoverService.GetHover(model, offset)!.Markdown);

        var (model2, offset2) = At("<Button Gr|id.Row=\"1\" />");
        Assert.Contains("class System.Windows.Controls.Grid", HoverService.GetHover(model2, offset2)!.Markdown);
    }

    [Fact]
    public void Hover_MarkupExtensionPartsAndEnumValues()
    {
        var (model, offset) = At("<Button Content=\"{Binding Title, Mo|de=TwoWay}\" />");
        Assert.Contains("BindingMode Binding.Mode", HoverService.GetHover(model, offset)!.Markdown);

        var (model2, offset2) = At("<Button Content=\"{Binding Title, Mode=Two|Way}\" />");
        Assert.Contains("BindingMode.TwoWay", HoverService.GetHover(model2, offset2)!.Markdown);
    }

    [Fact]
    public void Hover_Directive()
    {
        var (model, offset) = At("<Button x:Na|me=\"A\" />");

        Assert.Contains("x:Name", HoverService.GetHover(model, offset)!.Markdown);
    }

    [Fact]
    public void Definition_ProjectTypeGoesToSource()
    {
        var (model, offset) = At("<local:Fancy|Button />");
        var location = Assert.Single(DefinitionService.GetDefinitions(model, offset));

        Assert.Equal("C:/TestApp/MainWindow.xaml.cs", location.FilePath);
    }

    [Fact]
    public void Definition_EventHandlerGoesToCodeBehindMethod()
    {
        var (model, offset) = At("<Button Click=\"OnOk|Click\" />");
        var location = Assert.Single(DefinitionService.GetDefinitions(model, offset));

        Assert.Equal("C:/TestApp/MainWindow.xaml.cs", location.FilePath);
        Assert.True(location.Start.Line > 0);
    }

    [Fact]
    public void Definition_ResourceReferenceGoesToKey()
    {
        var (model, offset) = At("<Window.Resources><SolidColorBrush x:Key=\"Accent\" /></Window.Resources><Button Background=\"{StaticResource Acc|ent}\" />");
        var location = Assert.Single(DefinitionService.GetDefinitions(model, offset));

        Assert.Null(location.FilePath);
        Assert.Equal(1, location.Start.Line);
    }

    [Fact]
    public void Definition_FrameworkTypeHasNoSourceLocation()
    {
        var (model, offset) = At("<But|ton />");

        Assert.Empty(DefinitionService.GetDefinitions(model, offset));
    }
}
