using Snip.Core;
using Xunit;

namespace Snip.Core.Tests;

public sealed class OcrTextLayoutTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void FormValuesWithHigherBoxesStayAfterTheirLabelsInTheSameRow(double scale)
    {
        OcrTextRegion Box(string text, double x, double y, double width, double height) =>
            new(text, new(x * scale, y * scale, width * scale, height * scale), 0.9);
        var label = Box("通知方式：", 20, 22, 120, 24);
        var value = Box("Email", 320, 18, 72, 32);
        var nextLabel = Box("处理状态：", 20, 82, 120, 24);
        var nextValue = Box("Ready", 320, 79, 80, 30);
        var blocks = new[] { nextValue, value, nextLabel, label };
        // The previous exact-Y sort reverses the first row even though the two boxes share a baseline.
        Assert.Equal("Email", blocks.OrderBy(r => r.Bounds.Y).First().Text);
        var result = OcrTextLayout.Arrange(blocks);
        Assert.Equal(new[] { label, value, nextLabel, nextValue }, result.Regions);
        Assert.Equal("通知方式：" + new string(' ', 11) + "Email" + Environment.NewLine + Environment.NewLine + "处理状态：" + new string(' ', 11) + "Ready", result.LayoutText);
    }
    [Fact]
    public void AdjacentRowsDoNotMergeBecauseTheirBoxesOverlapSlightly()
    {
        var result = OcrTextLayout.Arrange([Block("第二行", 0, 24), Block("第一行", 0, 10)]);
        Assert.Equal("第一行" + Environment.NewLine + "第二行", result.LayoutText);
    }
    [Fact]
    public void GrowingRowBoundsCannotPullTheNextRowIntoIt()
    {
        var result = OcrTextLayout.Arrange([Block("A", 0, 10), Block("B", 150, 18), Block("C", 300, 26)]);
        Assert.Equal("A" + new string(' ', 9) + "B" + Environment.NewLine + new string(' ', 20) + "C", result.LayoutText);
    }
    [Fact]
    public void TallHeadingDoesNotAbsorbTheFollowingSmallerLine()
    {
        var title = new OcrTextRegion("标题", new(0, 0, 100, 40), null);
        var result = OcrTextLayout.Arrange([Block("正文", 0, 35), title]);
        Assert.Equal("标题" + Environment.NewLine + "正文", result.LayoutText);
    }
    [Fact]
    public void CloseMarkerAndOrdinaryWordGapsDoNotCreateExtraLines()
    {
        var result = OcrTextLayout.Arrange([Block("*", 0, 10, 8), Block("Hello", 10, 10, 50), Block("world", 70, 10, 50)]);
        Assert.Equal("*Hello world", result.LayoutText);
    }
    [Fact]
    public void SingleBlockKeepsItsLiteralTextAndAllSourceMetadata()
    {
        var block = new OcrTextRegion("  A01 / I-l-1\n第二段  ", new(2, 4, 100, 30), 0.73);
        var result = OcrTextLayout.Arrange([block]);
        Assert.Equal(block.Text, result.LayoutText);
        Assert.Same(block, Assert.Single(result.Regions));
    }
    [Fact]
    public void LongPageRowsAcrossTileBoundariesStayInSourceOrder()
    {
        var blocks = Enumerable.Range(1, 80).SelectMany(i => new[] { Block("Key" + i, 10, i * 36), Block("Value" + i, 300, i * 36 - 3) }).Reverse().ToArray();
        var result = OcrTextLayout.Arrange(blocks);
        Assert.Equal(160, result.Regions.Count);
        var lines = result.LayoutText!.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(80, lines.Length);
        for (int i = 1; i <= 80; i++)
        {
            Assert.StartsWith("Key" + i + " ", lines[i - 1]);
            Assert.EndsWith("Value" + i, lines[i - 1]);
        }
        Assert.Single(lines.Select(line => line.IndexOf("Value", StringComparison.Ordinal)).Distinct());
    }
    [Fact]
    public void IndentationAndParagraphWhitespaceFollowTheSourceCoordinates()
    {
        var result = OcrTextLayout.Arrange([Block("first", 10, 10, 50), Block("second", 40, 40, 60), Block("third", 10, 120, 50)]);
        Assert.Equal("first" + Environment.NewLine + Environment.NewLine + "   second" + string.Concat(Enumerable.Repeat(Environment.NewLine, 4)) + "third", result.LayoutText);
    }
    [Fact]
    public void EqualSourceRowPitchKeepsEqualSpacingDespiteDifferentGlyphDescenders()
    {
        // Geometry from the public Windows fixture: bottom-based gaps were 34 and 28,
        // causing a blank after the first row but none after the second.
        var result = OcrTextLayout.Arrange([
            new("标签甲", new(18, 30, 114, 34), null), new("Email", new(456, 27, 78, 34), null),
            new("标签乙", new(19, 103, 112, 31), null), new("Ready", new(453, 98, 93, 42), null),
            new("标签丙", new(19, 174, 113, 34), null), new("Today", new(453, 168, 91, 45), null)]);
        var indices = result.LayoutText!.Split(Environment.NewLine).Select((line, index) => (line, index)).Where(row => row.line.Length > 0).Select(row => row.index).ToArray();
        Assert.Equal(new[] { 0, 2, 4 }, indices);
    }
    [Fact]
    public void EmptyResultDoesNotInventText()
    {
        var result = OcrTextLayout.Arrange([]);
        Assert.Empty(result.Regions); Assert.Equal("", result.LayoutText);
    }
    private static OcrTextRegion Block(string text, double x, double y, double width = 80) => new(text, new(x, y, width, 20), null);
}
