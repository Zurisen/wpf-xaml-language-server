using WpfXaml.Analysis.Semantics;
using WpfXaml.Language;

namespace WpfXaml.Analysis.Tests;

public class CodeFixTests
{
    private const string Root = "<Window xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";

    private static string Window(string body, string namespaces = " xmlns:local=\"clr-namespace:TestApp\"") =>
        $"{Root}{namespaces} x:Class=\"TestApp.MainWindow\">\n{body}\n</Window>";

    private static IReadOnlyList<XamlFix> Fixes(string xaml, string code)
    {
        var model = TestWorkspace.Model(xaml);
        return model.GetDiagnostics().Where(d => d.Code == code).SelectMany(d => d.Fixes).ToList();
    }

    /// <summary>Applies a fix's edits to the document (edits in other files are not applied).</summary>
    private static string Apply(string xaml, XamlFix fix)
    {
        var text = xaml;
        foreach (var edit in fix.Edits.Where(e => e.FilePath is null).OrderByDescending(e => e.Span.Start))
        {
            text = text.Remove(edit.Span.Start, edit.Span.Length).Insert(edit.Span.Start, edit.NewText);
        }

        return text;
    }

    private static string FixBody(string body, string code, string? namespaces = null)
    {
        var xaml = namespaces is null ? Window(body) : Window(body, namespaces);
        var fix = Fixes(xaml, code).First();
        var fixedText = Apply(xaml, fix);
        return fixedText.Split('\n')[1];
    }

    [Theory]
    [InlineData("<Buton Content=\"OK\"></Buton>", DiagnosticCodes.UnknownType, "<Button Content=\"OK\"></Button>")]
    [InlineData("<Button Contnet=\"OK\" />", DiagnosticCodes.UnknownMember, "<Button Content=\"OK\" />")]
    [InlineData("<Button Grid.Rwo=\"1\" />", DiagnosticCodes.UnknownMember, "<Button Grid.Row=\"1\" />")]
    [InlineData("<Button Gird.Row=\"1\" />", DiagnosticCodes.UnknownType, "<Button Grid.Row=\"1\" />")]
    [InlineData("<Grid><Grid.RowDefinitons></Grid.RowDefinitons></Grid>", DiagnosticCodes.UnknownMember, "<Grid><Grid.RowDefinitions></Grid.RowDefinitions></Grid>")]
    [InlineData("<Button Visibility=\"Collpased\" />", DiagnosticCodes.InvalidValue, "<Button Visibility=\"Collapsed\" />")]
    [InlineData("<Button Content=\"{Bindng Title}\" />", DiagnosticCodes.UnknownMarkupExtension, "<Button Content=\"{Binding Title}\" />")]
    [InlineData("<Button Content=\"{Binding Title, Moed=OneWay}\" />", DiagnosticCodes.UnknownMember, "<Button Content=\"{Binding Title, Mode=OneWay}\" />")]
    [InlineData("<Button Content=\"{Binding Title, Mode=OneWya}\" />", DiagnosticCodes.InvalidValue, "<Button Content=\"{Binding Title, Mode=OneWay}\" />")]
    [InlineData("<Button x:Nmae=\"Ok\" />", DiagnosticCodes.UnknownDirective, "<Button x:Name=\"Ok\" />")]
    public void SpellingFixes_ReplaceExactlyTheMisspelledPart(string body, string code, string expected)
    {
        Assert.Equal(expected, FixBody(body, code));
    }

    [Fact]
    public void SpellingFix_IsPreferredAndTitled()
    {
        var fix = Assert.Single(Fixes(Window("<Button Contnet=\"OK\" />"), DiagnosticCodes.UnknownMember));

        Assert.Equal("Change to 'Content'", fix.Title);
        Assert.True(fix.IsPreferred);
    }

    [Fact]
    public void MissingEventHandler_GeneratesMethodInCodeBehind()
    {
        var fix = Assert.Single(Fixes(Window("<Button Click=\"OnCancelClick\" />"), DiagnosticCodes.EventHandlerNotFound));
        var edit = Assert.Single(fix.Edits);

        Assert.Equal("C:/TestApp/MainWindow.xaml.cs", edit.FilePath);
        Assert.Contains("private void OnCancelClick(object sender, RoutedEventArgs e)", edit.NewText);
        Assert.Equal(edit.Start, edit.End);
        Assert.StartsWith("Generate event handler 'OnCancelClick' in MainWindow.xaml.cs", fix.Title);

        // Inserted right before the closing brace of the MainWindow class (line of "}" after OnLoaded).
        var source = TestWorkspace.ProjectSource.Replace("\r\n", "\n").Split('\n');
        Assert.Equal("    }", source[edit.Start.Line].TrimEnd());
        Assert.Contains("OnLoaded", source[edit.Start.Line - 1]);
    }

    [Fact]
    public void UndeclaredPrefix_OffersToDeclareIt()
    {
        var xaml = Window("<my:FancyButton />");
        var fix = Assert.Single(Fixes(xaml, DiagnosticCodes.UnknownNamespacePrefix));

        Assert.Equal("Add xmlns:my=\"clr-namespace:TestApp\"", fix.Title);
        Assert.Contains("xmlns:local=\"clr-namespace:TestApp\" xmlns:my=\"clr-namespace:TestApp\"", Apply(xaml, fix));
    }

    [Fact]
    public void UnprefixedProjectType_UsesTheExistingPrefix()
    {
        var fix = Fixes(Window("<FancyButton></FancyButton>"), DiagnosticCodes.UnknownType).Single(f => f.Title.StartsWith("Use", StringComparison.Ordinal));

        Assert.Equal("Use local:FancyButton", fix.Title);
        Assert.Equal("<local:FancyButton></local:FancyButton>", FixBody("<FancyButton></FancyButton>", DiagnosticCodes.UnknownType));
    }

    [Fact]
    public void UnprefixedProjectType_WithoutPrefix_AddsXmlns()
    {
        var xaml = Window("<MainViewModel />", namespaces: string.Empty);
        var fix = Fixes(xaml, DiagnosticCodes.UnknownType).Single(f => f.Title.StartsWith("Use", StringComparison.Ordinal));
        var fixedText = Apply(xaml, fix);

        Assert.Equal("Use local:MainViewModel from TestApp (add xmlns:local)", fix.Title);
        Assert.Contains("xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" xmlns:local=\"clr-namespace:TestApp\"", fixedText);
        Assert.Contains("<local:MainViewModel />", fixedText);
    }

    [Fact]
    public void XmlnsIsAddedOnItsOwnLine_WhenRootAttributesAreOnePerLine()
    {
        var xaml = "<Window xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"\n        xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"\n        x:Class=\"TestApp.MainWindow\">\n<MainViewModel />\n</Window>";
        var fix = Fixes(xaml, DiagnosticCodes.UnknownType).Single(f => f.Title.StartsWith("Use", StringComparison.Ordinal));

        Assert.Contains("xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"\n        xmlns:local=\"clr-namespace:TestApp\"\n", Apply(xaml, fix));
    }

    [Fact]
    public void DiagnosticsWithoutAnObviousFix_HaveNone()
    {
        Assert.Empty(Fixes(Window("<Button Zzzqqq=\"1\" />"), DiagnosticCodes.UnknownMember));
    }

    [Fact]
    public void FixableDiagnostics_AreFoundByRange()
    {
        var (text, offset) = TestWorkspace.Caret(Window("<Button Cont|net=\"OK\" />"));
        var model = TestWorkspace.Model(text);

        Assert.Single(model.GetFixableDiagnostics(new Language.Text.TextSpan(offset, 0)));
        Assert.Empty(model.GetFixableDiagnostics(new Language.Text.TextSpan(0, 1)));
    }
}
