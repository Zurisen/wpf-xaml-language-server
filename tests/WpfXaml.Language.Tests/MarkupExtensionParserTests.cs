using WpfXaml.Language.Syntax;

namespace WpfXaml.Language.Tests;

public class MarkupExtensionParserTests
{
    [Fact]
    public void ParsesPositionalNamedAndNestedArguments()
    {
        const string raw = "{Binding Title, Mode=TwoWay, Converter={StaticResource Conv}}";
        var result = MarkupExtensionParser.Parse(raw, 10);

        Assert.Empty(result.Diagnostics);
        var root = result.Root!;
        Assert.True(root.IsClosed);
        Assert.Equal("Binding", root.TypeName.LocalName);
        Assert.Equal(3, root.Arguments.Count);

        Assert.True(root.Arguments[0].IsPositional);
        Assert.Equal("Title", root.Arguments[0].Value.Text);
        Assert.Equal(10 + raw.IndexOf("Title", StringComparison.Ordinal), root.Arguments[0].Value.Span.Start);

        Assert.Equal("Mode", root.Arguments[1].Name);
        Assert.Equal("TwoWay", root.Arguments[1].Value.Text);

        var nested = root.Arguments[2].Value.Extension!;
        Assert.Equal("StaticResource", nested.TypeName.LocalName);
        Assert.Equal("Conv", nested.Arguments.Single().Value.Text);
        Assert.Same(root.Arguments[2], nested.ParentArgument);
    }

    [Fact]
    public void ParsesPrefixedNameAndQuotedValues()
    {
        var root = MarkupExtensionParser.Parse("{x:Static local:Foo.Bar}", 0).Root!;
        Assert.Equal("x", root.TypeName.Prefix);
        Assert.Equal("Static", root.TypeName.LocalName);
        Assert.Equal("local:Foo.Bar", root.Arguments.Single().Value.Text);

        var quoted = MarkupExtensionParser.Parse("{Binding Path=A, StringFormat='{0}, {1}'}", 0).Root!;
        Assert.Equal("{0}, {1}", quoted.FindArgument("StringFormat")!.Value.Text);
    }

    [Fact]
    public void KeepsIndexersAndEscapedFormatsTogether()
    {
        var root = MarkupExtensionParser.Parse("{Binding Items[a,b], StringFormat={}{0:N2}}", 0).Root!;

        Assert.Equal("Items[a,b]", root.Arguments[0].Value.Text);
        Assert.Equal("{0:N2}", root.FindArgument("StringFormat")!.Value.Text);
        Assert.Null(root.FindArgument("StringFormat")!.Value.Extension);
    }

    [Fact]
    public void ReportsUnclosedExtension()
    {
        var result = MarkupExtensionParser.Parse("{Binding Path=A", 0);

        Assert.False(result.Root!.IsClosed);
        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.MarkupExtensionSyntax);
    }

    [Theory]
    [InlineData("{Binding}", true)]
    [InlineData("{}{literal}", false)]
    [InlineData("plain", false)]
    [InlineData("", false)]
    public void DetectsMarkupExtensions(string raw, bool expected) =>
        Assert.Equal(expected, MarkupExtensionParser.IsMarkupExtension(raw));
}
