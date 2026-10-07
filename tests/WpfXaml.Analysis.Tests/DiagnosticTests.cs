using WpfXaml.Language;

namespace WpfXaml.Analysis.Tests;

public class DiagnosticTests
{
    private static IReadOnlyList<XamlDiagnostic> Diagnose(string body, string rootAttributes = "x:Class=\"TestApp.MainWindow\"")
    {
        var xaml = $"<Window {TestWorkspace.Header} {rootAttributes}>\n{body}\n</Window>";
        return TestWorkspace.Model(xaml).GetDiagnostics();
    }

    [Fact]
    public void ValidDocument_HasNoDiagnostics()
    {
        var diagnostics = Diagnose("""
            <Window.Resources>
              <local:BoolToVisibilityConverter x:Key="BoolToVis" />
              <SolidColorBrush x:Key="Accent" Color="Red" />
            </Window.Resources>
            <Grid local:Behaviors.IsHighlighted="True">
              <Grid.RowDefinitions>
                <RowDefinition Height="Auto" />
              </Grid.RowDefinitions>
              <Button x:Name="Ok" Grid.Row="0" Content="{Binding Title, Mode=OneWay}" Click="OnOkClick"
                      Visibility="{Binding IsOk, Converter={StaticResource BoolToVis}}" Background="{StaticResource Accent}"
                      HorizontalAlignment="Center" IsEnabled="false" Mouse.MouseDown="OnLoaded" />
              <local:FancyButton Fanciness="3" Tag="{x:Static local:Behaviors.IsHighlightedProperty}" />
              <ContentControl ContentTemplate="{x:Null}" Style="{DynamicResource {x:Type Button}}" />
            </Grid>
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void RealWorldPatterns_AreAccepted()
    {
        // Found by running `check` on a large production solution: all valid WPF.
        var diagnostics = Diagnose("""
            <Window.InputBindings>
              <KeyBinding Key="S" Modifiers="Ctrl+Shift" />
            </Window.InputBindings>
            <Grid ZIndex="1" ScrollViewer.ScrollChanged="OnLoaded" Button.Click="OnOkClick">
              <VirtualizingStackPanel IsVirtualizing="True" VirtualizationMode="Recycling" />
            </Grid>
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void UnknownType_IsReportedWithSuggestion()
    {
        var diagnostic = Assert.Single(Diagnose("<Buton />"));

        Assert.Equal(DiagnosticCodes.UnknownType, diagnostic.Code);
        Assert.Contains("Did you mean 'Button'?", diagnostic.Message);
    }

    [Fact]
    public void UnknownProperty_IsReportedWithSuggestion()
    {
        var diagnostic = Assert.Single(Diagnose("<Button Contnet=\"x\" />"));

        Assert.Equal(DiagnosticCodes.UnknownMember, diagnostic.Code);
        Assert.Contains("Did you mean 'Content'?", diagnostic.Message);
    }

    [Fact]
    public void UnknownAttachedProperty_IsReported()
    {
        var diagnostic = Assert.Single(Diagnose("<Button Grid.Rwo=\"1\" />"));

        Assert.Equal(DiagnosticCodes.UnknownMember, diagnostic.Code);
        Assert.Contains("'Row'", diagnostic.Message);
    }

    [Fact]
    public void InvalidEnumAndBooleanValues_AreReported()
    {
        var diagnostics = Diagnose("<Button Visibility=\"Collpased\" IsEnabled=\"yes\" />");

        Assert.Equal(2, diagnostics.Count);
        Assert.All(diagnostics, d => Assert.Equal(DiagnosticCodes.InvalidValue, d.Code));
        Assert.Contains(diagnostics, d => d.Message.Contains("Did you mean 'Collapsed'?"));
    }

    [Fact]
    public void ReadOnlyProperty_IsReported()
    {
        Assert.Equal(DiagnosticCodes.ReadOnlyProperty, Assert.Single(Diagnose("<Button ActualWidth=\"3\" />")).Code);
    }

    [Fact]
    public void MissingEventHandler_IsReported()
    {
        var diagnostic = Assert.Single(Diagnose("<Button Click=\"OnCancelClick\" />"));

        Assert.Equal(DiagnosticCodes.EventHandlerNotFound, diagnostic.Code);
    }

    [Fact]
    public void DuplicateNames_AreReportedButTemplatesHaveTheirOwnScope()
    {
        var diagnostics = Diagnose("""
            <StackPanel>
              <Button x:Name="A" />
              <Button Name="A" />
              <ContentControl>
                <ContentControl.ContentTemplate>
                  <DataTemplate><TextBlock x:Name="A" /></DataTemplate>
                </ContentControl.ContentTemplate>
              </ContentControl>
            </StackPanel>
            """);

        Assert.Equal(DiagnosticCodes.DuplicateName, Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void MarkupExtensionProblems_AreReported()
    {
        var diagnostics = Diagnose("<Button Content=\"{Bindng Title}\" Tag=\"{Binding Title, Moed=OneWay}\" Width=\"{Binding Mode=Sideways}\" />");

        Assert.Contains(diagnostics, d => d.Code == DiagnosticCodes.UnknownMarkupExtension && d.Message.Contains("'Binding'"));
        Assert.Contains(diagnostics, d => d.Code == DiagnosticCodes.UnknownMember && d.Message.Contains("'Mode'"));
        Assert.Contains(diagnostics, d => d.Code == DiagnosticCodes.InvalidValue && d.Message.Contains("Sideways"));
    }

    [Fact]
    public void UndeclaredPrefix_AndUnknownDirective_AreReported()
    {
        var diagnostics = Diagnose("<foo:Bar /><Button x:Nmae=\"A\" />");

        Assert.Contains(diagnostics, d => d.Code == DiagnosticCodes.UnknownNamespacePrefix);
        Assert.Contains(diagnostics, d => d.Code == DiagnosticCodes.UnknownDirective && d.Message.Contains("x:Name"));
    }

    [Fact]
    public void IgnorableNamespaces_AreNotChecked()
    {
        var diagnostics = Diagnose(
            "<Grid d:DataContext=\"{d:DesignInstance Type=local:MainViewModel}\" d:Whatever=\"1\"><d:Nonsense /></Grid>",
            "xmlns:d=\"http://schemas.microsoft.com/expression/blend/2008\" xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\" mc:Ignorable=\"d\"");

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void FrameworkOnlyMode_DoesNotReportProjectTypes()
    {
        var xaml = $"<Window {TestWorkspace.Header}><local:Missing Foo=\"1\" /><Buton /></Window>";
        var diagnostics = TestWorkspace.Model(xaml, TestWorkspace.FrameworkOnly).GetDiagnostics();

        Assert.Equal(DiagnosticCodes.UnknownType, Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void UnreferencedAssembly_IsReported()
    {
        var diagnostics = Diagnose("<Grid />", "xmlns:lib=\"clr-namespace:Lib;assembly=NotThere\"");

        Assert.Equal(DiagnosticCodes.UnresolvedAssembly, Assert.Single(diagnostics).Code);
    }
}
