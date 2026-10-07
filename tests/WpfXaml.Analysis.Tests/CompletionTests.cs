using WpfXaml.Analysis.Features.Completion;

namespace WpfXaml.Analysis.Tests;

public class CompletionTests
{
    private static XamlCompletionList Complete(string bodyWithCaret, string extraRootAttributes = "x:Class=\"TestApp.MainWindow\"")
    {
        var (text, offset) = TestWorkspace.Caret($"<Window {TestWorkspace.Header} {extraRootAttributes}>\n{bodyWithCaret}\n</Window>");
        var list = CompletionService.GetCompletions(TestWorkspace.Model(text), offset);
        Assert.NotNull(list);
        return list;
    }

    private static HashSet<string> Labels(XamlCompletionList list) => list.Items.Select(i => i.Label).ToHashSet();

    [Fact]
    public void ElementName_OffersTypesFromAllNamespacesAndPropertyElements()
    {
        var list = Complete("<Grid>\n<|\n</Grid>");
        var labels = Labels(list);

        Assert.Contains("Button", labels);
        Assert.Contains("local:FancyButton", labels);
        Assert.Contains("Grid.RowDefinitions", labels);
        Assert.DoesNotContain("Panel", labels);
        Assert.True(list.ReplaceSpan.IsEmpty);
    }

    [Fact]
    public void ElementName_ReplacesPartialName()
    {
        var list = Complete("<Grid><Butt|</Grid>");

        Assert.Equal(4, list.ReplaceSpan.Length);
        Assert.Contains("Button", Labels(list));
    }

    [Fact]
    public void AttributeName_OffersMembersAttachedPropertiesAndDirectives()
    {
        var list = Complete("<Grid><Button |/></Grid>");
        var labels = Labels(list);

        Assert.Contains("Content", labels);
        Assert.Contains("Click", labels);
        Assert.Contains("Grid.Row", labels);
        Assert.Contains("local:Behaviors.IsHighlighted", labels);
        Assert.Contains("x:Name", labels);
        Assert.DoesNotContain("x:Class", labels);
        Assert.DoesNotContain("ActualWidth", labels);

        var content = list.Items.Single(i => i.Label == "Content");
        Assert.Equal("Content=\"$1\"", content.SnippetText);
    }

    [Fact]
    public void AttributeName_ExcludesExistingAttributes()
    {
        var labels = Labels(Complete("<Button Content=\"a\" Con| />"));

        Assert.DoesNotContain("Content", labels);
        Assert.Contains("ContentTemplate", labels);
    }

    [Fact]
    public void AttributeValue_OffersEnumBooleanAndNamedValues()
    {
        Assert.Contains("Collapsed", Labels(Complete("<Button Visibility=\"|\" />")));
        Assert.Equal(["True", "False"], Labels(Complete("<Button IsEnabled=\"|\" />")));
        Assert.Contains("Red", Labels(Complete("<Button Background=\"|\" />")));
        Assert.Contains("Bold", Labels(Complete("<Button FontWeight=\"|\" />")));
    }

    [Fact]
    public void EventValue_OffersCodeBehindHandlers()
    {
        Assert.Equal(["OnOkClick", "OnLoaded"], Labels(Complete("<Button Click=\"|\" />")));
    }

    [Fact]
    public void TypeValuedProperties_OfferTypes()
    {
        var labels = Labels(Complete("<Window.Resources><Style TargetType=\"|\" /></Window.Resources>"));

        Assert.Contains("Button", labels);
        Assert.Contains("local:FancyButton", labels);
    }

    [Fact]
    public void SetterProperty_OffersTargetTypeProperties()
    {
        var labels = Labels(Complete("<Window.Resources><Style TargetType=\"Button\"><Setter Property=\"|\" /></Style></Window.Resources>"));

        Assert.Contains("Background", labels);
        Assert.Contains("Content", labels);
    }

    [Fact]
    public void MarkupExtension_OffersExtensionNames()
    {
        var labels = Labels(Complete("<Button Content=\"{|}\" />"));

        Assert.Contains("Binding", labels);
        Assert.Contains("StaticResource", labels);
        Assert.Contains("x:Static", labels);
        Assert.Contains("x:Type", labels);
    }

    [Fact]
    public void MarkupExtension_OffersArgumentNamesAndValues()
    {
        Assert.Contains("Mode", Labels(Complete("<Button Content=\"{Binding Title, |}\" />")));
        Assert.Contains("TwoWay", Labels(Complete("<Button Content=\"{Binding Title, Mode=|}\" />")));
        Assert.Contains("FindAncestor", Labels(Complete("<Button Content=\"{Binding RelativeSource={RelativeSource |}}\" />")));
    }

    [Fact]
    public void StaticResource_OffersKeysFromDocument()
    {
        var labels = Labels(Complete("<Window.Resources><SolidColorBrush x:Key=\"Accent\" /></Window.Resources><Button Background=\"{StaticResource |}\" />"));

        Assert.Contains("Accent", labels);
    }

    [Fact]
    public void ClosingTag_OffersInnermostOpenElement()
    {
        var list = Complete("<StackPanel>\n<Button></|\n</StackPanel>");

        var item = Assert.Single(list.Items);
        Assert.Equal("Button", item.Label);
        Assert.Equal("Button>", item.InsertText);
    }

    [Fact]
    public void NamespaceDeclaration_OffersKnownUrisAndProjectNamespaces()
    {
        var labels = Labels(Complete("<Grid xmlns:foo=\"|\" />"));

        Assert.Contains("http://schemas.microsoft.com/winfx/2006/xaml/presentation", labels);
        Assert.Contains("clr-namespace:TestApp", labels);
    }
}
