using Snip.Core;
using Xunit;

namespace Snip.Core.Tests;

public sealed class ImageTextDocumentTests
{
    [Fact]
    public void NativeWordBoxesAnchorPartialTextAndSpatialLayout()
    {
        var result = new OcrResult([new("Hello world", new(10,20,180,20), null,
            [new("Hello",new(10,20,60,20)),new("world",new(110,20,80,20))])]);
        var doc = ImageTextDocument.Create(result, 300, 100);
        var word = doc.Hit(new(111,25))!;
        Assert.Equal("world",doc.Text.Substring(word.WordStart,word.WordLength));
        Assert.Equal(110,doc.Glyphs.First(g=>g.Start==word.WordStart).Bounds.X);
        Assert.True(doc.Highlights(word.WordStart,word.WordStart+word.WordLength).All(r=>r.X>=110 && r.Right<=190.001));
        Assert.Null(doc.Hit(new(280,90)));
        Assert.NotNull(doc.Hit(new(280,90),nearest:true));
    }
    [Fact]
    public void RowsAndColumnsFollowSourceCoordinatesRatherThanEngineOrder()
    {
        var doc=ImageTextDocument.Create(new([
            new("下右",new(160,60,40,20),null),new("上右",new(160,10,40,20),null),
            new("下左",new(10,60,40,20),null),new("上左",new(10,10,40,20),null)]),300,120);
        Assert.True(doc.Text.IndexOf("上左",StringComparison.Ordinal)<doc.Text.IndexOf("上右",StringComparison.Ordinal));
        Assert.True(doc.Text.IndexOf("上右",StringComparison.Ordinal)<doc.Text.IndexOf("下左",StringComparison.Ordinal));
        Assert.True(doc.Text.IndexOf("下左",StringComparison.Ordinal)<doc.Text.IndexOf("下右",StringComparison.Ordinal));
        Assert.Contains(Environment.NewLine,doc.Text);
        Assert.Equal("下左",doc.Text.Substring(doc.Hit(new(11,65))!.WordStart,2));
    }
    [Fact]
    public void CombiningCharactersAndSurrogatePairsAreNeverSplit()
    {
        var doc=ImageTextDocument.Create(new([new("A e\u0301 中文🙂",new(0,0,200,20),null)]),220,40);
        Assert.Contains(doc.Glyphs,g=>doc.Text.Substring(g.Start,g.Length)=="e\u0301");
        Assert.Contains(doc.Glyphs,g=>doc.Text.Substring(g.Start,g.Length)=="🙂");
        foreach(var glyph in doc.Glyphs) Assert.False(char.IsLowSurrogate(doc.Text[glyph.Start]));
    }
    [Fact]
    public void MosaicExcludesHiddenWordsEvenIfAnEngineGuessesTheirOriginalText()
    {
        var result=new OcrResult([new("public SECRET",new(10,10,190,20),null,
            [new("public",new(10,10,60,20)),new("SECRET",new(110,10,90,20))])]);
        var doc=ImageTextDocument.Create(result,300,100,[new(100,0,110,40)]);
        Assert.Contains("public",doc.Text);Assert.DoesNotContain("SECRET",doc.Text);
        var lineOnly=ImageTextDocument.Create(new([new("public SECRET",new(10,10,190,20),null)]),300,100,[new(100,0,110,40)]);
        Assert.Empty(lineOnly.Glyphs);Assert.Empty(lineOnly.Text);
    }
    [Fact]
    public void InvalidGeometryIsIgnoredAndLargeResultsFailBeforeLayout()
    {
        var doc=ImageTextDocument.Create(new([new("invalid",new(double.NaN,0,10,20),null),new("outside",new(400,0,20,20),null),new("inside",new(-5,0,20,20),null)]),100,100);
        Assert.Equal("inside",doc.Text);Assert.True(doc.Glyphs.All(g=>g.Bounds.X>=0));
        Assert.Throws<InvalidOperationException>(()=>ImageTextDocument.Create(new([new(new string('a',100_001),new(0,0,20,20),null)]),100,100));
    }
}
