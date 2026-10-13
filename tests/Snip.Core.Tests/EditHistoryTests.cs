using Snip.Core;
using Xunit;

namespace Snip.Core.Tests;

public sealed class EditHistoryTests
{
    private static Annotation Shape() => new(Guid.NewGuid(), AnnotationTool.Rectangle, new(10, 10), new(30, 40), "#FF0000", 4, 24, 12, "", []);

    [Fact]
    public void UndoAndRedoRestoreTheEntireDocument()
    {
        var history = new EditHistory(); var a = Shape(); var b = Shape();
        history.Commit([a]); history.Commit([a, b]); history.Undo();
        Assert.Single(history.Current); Assert.True(history.CanRedo);
        history.Redo(); Assert.Equal(2, history.Current.Count);
    }
    [Fact]
    public void NewEditAfterUndoDropsRedoBranch()
    {
        var history = new EditHistory(); var a = Shape();
        history.Commit([a]); history.Commit([a, Shape()]); history.Undo(); history.Commit([]);
        Assert.False(history.CanRedo); Assert.Empty(history.Current);
    }
    [Fact]
    public void DirtyStateTracksExportedRevisionAcrossUndoRedo()
    {
        var history = new EditHistory(); Assert.False(history.IsDirty);
        history.Commit([Shape()]); Assert.True(history.IsDirty);
        history.MarkExported(); Assert.False(history.IsDirty);
        history.Undo(); Assert.True(history.IsDirty);
        history.Redo(); Assert.False(history.IsDirty);
    }
    [Fact]
    public void CommittedPointsAreDefensivelyCopied()
    {
        var points = new List<PixelPoint> { new(1, 2) };
        var history = new EditHistory(); history.Commit([Shape() with { Tool = AnnotationTool.Pen, Points = points }]);
        points.Add(new(999, 999));
        Assert.Single(history.Current[0].Points);
    }
    [Fact]
    public void DeletionAndMovementCanBeUndoneIndependently()
    {
        var history = new EditHistory(); var a = Shape();
        history.Commit([a]); history.Commit([a.Translate(new(100, 200))]); history.Commit([]);
        history.Undo(); Assert.Equal(new PixelPoint(110, 210), history.Current[0].Start);
        history.Undo(); Assert.Equal(a.Start, history.Current[0].Start);
    }
    [Fact]
    public void BoundaryUndoRedoAreSafe()
    {
        var history = new EditHistory(); history.Undo(); history.Redo();
        Assert.False(history.CanUndo); Assert.False(history.CanRedo); Assert.Empty(history.Current);
    }

    [Fact]
    public void HistoryRetainsAtMostOneHundredUndoSteps()
    {
        var history = new EditHistory();
        for (int i = 0; i < 150; i++) history.Commit([Shape() with { Text = i.ToString() }]);
        int steps = 0;
        while (history.CanUndo) { history.Undo(); steps++; }
        Assert.Equal(100, steps);
        Assert.Equal("49", history.Current.Single().Text);
    }
    [Fact]
    public void CommittedStickerPayloadDoesNotAliasCallerBuffer()
    {
        byte[] data = [1, 2, 3]; var history = new EditHistory();
        history.Commit([Shape() with { Tool = AnnotationTool.Sticker, StickerPng = data }]); data[0] = 99;
        Assert.Equal((byte)1, history.Current.Single().StickerPng.Span[0]);
    }
}
