using WpfXaml.Language.Syntax;
using WpfXaml.Language.Text;

namespace WpfXaml.Language.Tests;

public class XamlParserTests
{
    [Fact]
    public void WellFormedDocument_ParsesTreeWithSpans()
    {
        const string text = """
            <Window xmlns="p" xmlns:x="x" x:Class="App.MainWindow" Title="Hi &amp; bye">
              <Grid>
                <Button Content="OK" Grid.Row="1" />
              </Grid>
            </Window>
            """;

        var doc = XamlDocument.Parse(text);

        Assert.Empty(doc.Diagnostics);
        var root = Assert.IsType<XamlElement>(doc.Root);
        Assert.Equal("Window", root.Name.LocalName);
        Assert.Equal(4, root.Attributes.Count);
        Assert.Equal("Hi & bye", root.FindAttribute(null, "Title")!.Value);
        Assert.Equal("Hi &amp; bye", root.FindAttribute(null, "Title")!.RawValue);
        Assert.Equal("x", root.FindAttribute("x", "Class")!.Name.Prefix);

        var grid = Assert.Single(root.ChildElements);
        var button = Assert.Single(grid.ChildElements);
        Assert.True(button.IsSelfClosing);
        var row = button.FindAttribute(null, "Grid.Row")!;
        Assert.True(row.Name.IsDotted);
        Assert.Equal("Grid", row.Name.OwnerName);
        Assert.Equal("Row", row.Name.MemberName);
        Assert.Equal("Row", text.Substring(row.Name.MemberNameSpan.Start, row.Name.MemberNameSpan.Length));
        Assert.Equal("1", text.Substring(row.ValueSpan!.Value.Start, row.ValueSpan.Value.Length));
        Assert.Equal("</Window>", text[root.EndTagSpan!.Value.Start..root.EndTagSpan.Value.End]);
    }

    [Fact]
    public void NamespaceLookup_UsesNearestDeclaration()
    {
        var doc = XamlDocument.Parse("""<a xmlns="outer" xmlns:p="one"><b xmlns:p="two"><c/></b></a>""");
        var c = doc.AllElements().Single(e => e.Name.LocalName == "c");

        Assert.Equal("two", c.LookupNamespace("p"));
        Assert.Equal("outer", c.LookupNamespace(null));
        Assert.Null(c.LookupNamespace("missing"));
        Assert.Equal(2, c.GetNamespacesInScope().Count);
    }

    [Fact]
    public void UnclosedStartTag_IsTreatedAsSelfClosingAndKeepsSiblings()
    {
        var doc = XamlDocument.Parse("<Grid>\n  <Button Con\n  <TextBlock />\n</Grid>");

        var grid = doc.Root!;
        Assert.Equal(["Button", "TextBlock"], grid.ChildElements.Select(e => e.Name.LocalName));
        var button = grid.ChildElements.First();
        Assert.False(button.IsStartTagClosed);
        Assert.Single(button.Attributes);
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("not closed"));
    }

    [Fact]
    public void BareLessThan_ProducesEmptyNamedElement()
    {
        var text = "<Grid>\n  <\n</Grid>";
        var doc = XamlDocument.Parse(text);

        var empty = Assert.Single(doc.Root!.ChildElements);
        Assert.True(empty.Name.IsEmpty);
        Assert.Equal(text.IndexOf("<\n", StringComparison.Ordinal) + 1, empty.Name.Span.Start);
    }

    [Fact]
    public void UnclosedAttributeValue_StopsAtNextTag()
    {
        var doc = XamlDocument.Parse("<Grid>\n  <Button Content=\"abc\n  <TextBlock />\n</Grid>");

        var button = doc.Root!.ChildElements.First();
        var content = button.Attributes.Single();
        Assert.False(content.IsValueClosed);
        Assert.StartsWith("abc", content.RawValue);
        Assert.Equal(2, doc.Root.ChildElements.Count());
    }

    [Fact]
    public void MismatchedEndTag_ClosesNearestMatchingAncestor()
    {
        var doc = XamlDocument.Parse("<a><b><c></a>");

        Assert.NotNull(doc.Root!.EndTagSpan);
        Assert.Equal(2, doc.Diagnostics.Count(d => d.Message.Contains("is not closed")));
    }

    [Fact]
    public void CommentsCDataAndProlog_AreParsed()
    {
        var doc = XamlDocument.Parse("<?xml version=\"1.0\"?>\n<!-- c --><a><![CDATA[x < y]]></a>");

        Assert.Empty(doc.Diagnostics);
        Assert.IsType<XamlProcessingInstruction>(doc.Nodes[0]);
        Assert.Equal("x < y", Assert.IsType<XamlCData>(doc.Root!.Children.Single()).Text);
    }

    [Fact]
    public void FindStartTagAt_FindsInnermostTag()
    {
        const string text = "<a>\n  <b c=\"1\" />\n</a>";
        var doc = XamlDocument.Parse(text);

        Assert.Equal("b", doc.FindStartTagAt(text.IndexOf('c'))!.Name.LocalName);
        Assert.Equal("a", doc.FindStartTagAt(1)!.Name.LocalName);
        Assert.Null(doc.FindStartTagAt(text.Length));
    }

    [Theory]
    [InlineData("a\nb", 1, 0, 2)]
    [InlineData("a\r\nb", 1, 0, 3)]
    [InlineData("ab", 0, 1, 1)]
    public void LineIndex_RoundTrips(string text, int line, int character, int offset)
    {
        var lines = new LineIndex(text);

        Assert.Equal(new LinePosition(line, character), lines.GetPosition(offset));
        Assert.Equal(offset, lines.GetOffset(line, character));
    }
}
