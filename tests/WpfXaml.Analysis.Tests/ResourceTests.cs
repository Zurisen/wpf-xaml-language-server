using WpfXaml.Analysis.Features;
using WpfXaml.Analysis.Features.Completion;
using WpfXaml.Analysis.Resources;
using WpfXaml.Analysis.Semantics;
using WpfXaml.Language;
using WpfXaml.Language.Syntax;

namespace WpfXaml.Analysis.Tests;

public class ResourceTests
{
    private const string Root = "C:/App";
    private const string Ns = """xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" """;

    /// <summary>Files keyed by path; "Lib" maps to C:/Lib for /Lib;component/ URIs; C:/App/App.xaml is the application.</summary>
    private sealed class InMemoryHost(Dictionary<string, string> files, IReadOnlyList<string>? applications = null) : IXamlResourceHost
    {
        private readonly Dictionary<string, XamlDocument> _parsed = new(StringComparer.OrdinalIgnoreCase);

        public XamlDocument? GetDocument(string filePath)
        {
            filePath = Normalize(filePath);
            if (!files.TryGetValue(filePath, out var text))
            {
                return null;
            }

            if (!_parsed.TryGetValue(filePath, out var document))
            {
                _parsed[filePath] = document = XamlDocument.Parse(text);
            }

            return document;
        }

        public ResourceSourceResolution ResolveSource(string? fromFilePath, string source)
        {
            const string pack = "pack://application:,,,";
            var uri = source.StartsWith(pack, StringComparison.Ordinal) ? source[pack.Length..] : source;
            var component = uri.IndexOf(";component/", StringComparison.Ordinal);
            if (component >= 0)
            {
                var assembly = uri[..component].TrimStart('/');
                return assembly == "Lib"
                    ? ResourceSourceResolution.File($"C:/Lib/{uri[(component + ";component/".Length)..]}")
                    : ResourceSourceResolution.Unknown($"'{assembly}' is not a project in the loaded solution");
            }

            return uri.StartsWith('/')
                ? ResourceSourceResolution.File($"{Root}{uri}")
                : ResourceSourceResolution.File($"{Path.GetDirectoryName(fromFilePath)!.Replace('\\', '/')}/{uri}");
        }

        public IReadOnlyList<string>? GetApplicationDefinitions(string? filePath) => applications;

        public IReadOnlySet<string>? GetAllResourceKeys() =>
            files.Values.SelectMany(t => XamlDocument.Parse(t).AllElements()).Select(ResourceSyntax.GetStringKey).OfType<string>().ToHashSet();

        private static string Normalize(string path) => path.Replace('\\', '/');
    }

    private static readonly Dictionary<string, string> Files = new(StringComparer.OrdinalIgnoreCase)
    {
        [$"{Root}/App.xaml"] = $"""
            <Application {Ns}>
              <Application.Resources>
                <ResourceDictionary>
                  <ResourceDictionary.MergedDictionaries>
                    <ResourceDictionary Source="Themes/Colors.xaml" />
                    <ResourceDictionary Source="pack://application:,,,/Lib;component/Themes/Shared.xaml" />
                  </ResourceDictionary.MergedDictionaries>
                  <SolidColorBrush x:Key="AppBrush" Color="Red" />
                </ResourceDictionary>
              </Application.Resources>
            </Application>
            """,
        [$"{Root}/Themes/Colors.xaml"] = $"""
            <ResourceDictionary {Ns}>
              <SolidColorBrush x:Key="AccentBrush" Color="SteelBlue" />
            </ResourceDictionary>
            """,
        ["C:/Lib/Themes/Shared.xaml"] = $"""
            <ResourceDictionary {Ns}>
              <ResourceDictionary.MergedDictionaries>
                <ResourceDictionary Source="/Lib;component/Themes/Shared.xaml" />
              </ResourceDictionary.MergedDictionaries>
              <Thickness x:Key="SharedMargin">4</Thickness>
            </ResourceDictionary>
            """,
        [$"{Root}/Views/Consumer.xaml"] = $"""
            <UserControl {Ns}>
              <UserControl.Resources>
                <SolidColorBrush x:Key="ConsumerOnlyBrush" Color="Green" />
              </UserControl.Resources>
            </UserControl>
            """,
    };

    private static XamlSemanticModel Model(string text, string path = $"{Root}/Views/MainWindow.xaml", bool withApplication = true, Dictionary<string, string>? files = null)
    {
        var host = new InMemoryHost(files ?? Files, withApplication ? [$"{Root}/App.xaml"] : null);
        return new XamlSemanticModel(XamlDocument.Parse(text), TestWorkspace.Project, new XamlResourceContext(host, path));
    }

    private static string Window(string body, string resources = "") =>
        $"<Window {Ns}><Window.Resources>{resources}</Window.Resources>\n{body}\n</Window>";

    private static IEnumerable<XamlDiagnostic> ResourceDiagnostics(XamlSemanticModel model) =>
        model.GetDiagnostics().Where(d => d.Code is DiagnosticCodes.ResourceNotFound or DiagnosticCodes.ResourceDictionaryNotFound);

    [Fact]
    public void KeysFromApplicationAndMergedDictionaries_AreFound()
    {
        var model = Model(Window("""
            <StackPanel Background="{StaticResource AppBrush}" Margin="{StaticResource SharedMargin}">
              <Button Background="{StaticResource AccentBrush}" Foreground="{StaticResource LocalBrush}" />
            </StackPanel>
            """, """<SolidColorBrush x:Key="LocalBrush" Color="Blue" />"""));

        Assert.Empty(ResourceDiagnostics(model));
    }

    [Fact]
    public void MissingKey_IsReportedWithSuggestion()
    {
        var diagnostic = Assert.Single(ResourceDiagnostics(Model(Window("""<Button Background="{StaticResource AcentBrush}" />"""))));

        Assert.Equal(DiagnosticCodes.ResourceNotFound, diagnostic.Code);
        Assert.Equal(XamlDiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains("Did you mean 'AccentBrush'?", diagnostic.Message);
    }

    [Fact]
    public void DynamicResource_IsNotReported()
    {
        Assert.Empty(ResourceDiagnostics(Model(Window("""<Button Background="{DynamicResource SetFromCode}" />"""))));
    }

    [Fact]
    public void UnknownApplicationOrExternalDictionary_SuppressesWarnings()
    {
        // No application known for the file.
        Assert.Empty(ResourceDiagnostics(Model(Window("""<Button Background="{StaticResource Nope}" />"""), withApplication: false)));

        // A dictionary merged from an assembly outside the solution.
        var model = Model(Window("""<Button Background="{StaticResource FromPackage}" />""",
            """<ResourceDictionary><ResourceDictionary.MergedDictionaries><ResourceDictionary Source="pack://application:,,,/MahApps.Metro;component/Styles/Controls.xaml" /></ResourceDictionary.MergedDictionaries></ResourceDictionary>"""));
        Assert.Empty(ResourceDiagnostics(model));

        // A ResourceDictionary subclass that may add resources in code.
        var themed = Model(Window("""<Button Background="{StaticResource FromTheme}" />""",
            """<ResourceDictionary><ResourceDictionary.MergedDictionaries><local:BundledTheme xmlns:local="clr-namespace:TestApp" /></ResourceDictionary.MergedDictionaries></ResourceDictionary>"""));
        Assert.Empty(ResourceDiagnostics(themed));
    }

    [Fact]
    public void LoneKeyedDictionaryInResources_IsTheResourcesValue()
    {
        // WPF assigns a single ResourceDictionary child as the Resources value, ignoring its x:Key.
        var model = Model(Window("""<Button Background="{StaticResource InnerBrush}" />""",
            """<ResourceDictionary x:Key="Icons"><SolidColorBrush x:Key="InnerBrush" Color="Red" /></ResourceDictionary>"""));

        Assert.Empty(ResourceDiagnostics(model));
    }

    [Fact]
    public void DeferredContent_AcceptsKeysDefinedByOtherFiles()
    {
        // In a dictionary file, a key provided by some consumer elsewhere may be found at run time...
        var dictionary = Model($$"""
            <ResourceDictionary {{Ns}}>
              <Style x:Key="S" TargetType="Button"><Setter Property="Background" Value="{StaticResource ConsumerOnlyBrush}" /></Style>
              <Style x:Key="T" TargetType="Button"><Setter Property="Background" Value="{StaticResource DefinedNowhere}" /></Style>
            </ResourceDictionary>
            """, path: $"{Root}/Themes/Styles.xaml");

        // ...but a key defined nowhere is still reported.
        var diagnostic = Assert.Single(ResourceDiagnostics(dictionary));
        Assert.Contains("DefinedNowhere", diagnostic.Message);

        // Outside deferred content the consumer's key is not visible.
        Assert.Single(ResourceDiagnostics(Model(Window("""<Button Background="{StaticResource ConsumerOnlyBrush}" />"""))));
    }

    [Fact]
    public void MissingDictionarySource_IsReported()
    {
        var model = Model(Window("<Grid />", """<ResourceDictionary Source="../Themes/Missing.xaml" />"""));

        Assert.Equal(DiagnosticCodes.ResourceDictionaryNotFound, Assert.Single(ResourceDiagnostics(model)).Code);
    }

    [Fact]
    public void Completion_OffersKeysFromEveryVisibleDictionary()
    {
        var (text, offset) = TestWorkspace.Caret(Window("""<Button Background="{StaticResource |}" />""", """<SolidColorBrush x:Key="LocalBrush" />"""));
        var list = CompletionService.GetCompletions(Model(text), offset);

        var labels = list!.Items.Select(i => i.Label).ToHashSet();
        Assert.Superset(new HashSet<string> { "LocalBrush", "AppBrush", "AccentBrush", "SharedMargin" }, labels);
        Assert.Equal("SolidColorBrush (Colors.xaml)", list.Items.Single(i => i.Label == "AccentBrush").Detail);
    }

    [Fact]
    public void DefinitionAndHover_FollowKeysIntoOtherFiles()
    {
        var (text, offset) = TestWorkspace.Caret(Window("""<Button Margin="{StaticResource Shared|Margin}" />"""));
        var model = Model(text);

        var location = Assert.Single(DefinitionService.GetDefinitions(model, offset));
        Assert.Equal("C:/Lib/Themes/Shared.xaml", location.FilePath);
        Assert.Equal(4, location.Start.Line);

        var hover = HoverService.GetHover(model, offset)!.Markdown;
        Assert.Contains("`Thickness` in `Shared.xaml`, line 5", hover);
    }

    [Fact]
    public void WithoutHost_OnlyDocumentResourcesAreKnown()
    {
        var model = new XamlSemanticModel(
            XamlDocument.Parse(Window("""<Button Background="{StaticResource LocalBrush}" Foreground="{StaticResource Unknown}" />""", """<SolidColorBrush x:Key="LocalBrush" />""")),
            TestWorkspace.Project);

        Assert.Empty(ResourceDiagnostics(model));
        var button = model.Document.AllElements().Single(e => e.Name.LocalName == "Button");
        Assert.Single(model.GetResourceScope(button).Find("LocalBrush"));
        Assert.False(model.GetResourceScope(button).IsComplete);
    }
}
