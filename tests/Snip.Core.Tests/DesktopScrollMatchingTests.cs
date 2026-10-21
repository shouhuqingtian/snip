using Snip.Core;
using Xunit;

namespace Snip.Core.Tests;

public sealed class DesktopScrollMatchingTests
{
    private const int Width = 240, Height = 600, Header = 400, Footer = 40, Body = Height - Header - Footer;
    private static readonly byte[] page = CreatePage();

    [Fact]
    public void LegacyMatcherCannotCorrectlyDescribeLargeFrozenHeader()
    {
        var legacy = ScrollMatching.Match(Frame(0), Frame(32));
        Assert.True(legacy is null || legacy.Header != Header || legacy.Footer != Footer);
    }
    [Fact]
    public void FindsMotionBehindFrozenToolbarAndDistinguishesUpwardScrolling()
    {
        var down = DesktopScrollMatching.Match(Frame(0), Frame(32));
        Assert.NotNull(down); Assert.Equal(32, down.Offset);
        Assert.InRange(down.Header, Header, Header + 6); Assert.InRange(down.Footer, Footer, Footer + 6);
        var up = DesktopScrollMatching.Match(Frame(64), Frame(32));
        Assert.NotNull(up); Assert.Equal(-32, up.Offset);
    }
    [Fact]
    public void UnrelatedContentIsNotAcceptedAsAnOverlap()
        => Assert.Null(DesktopScrollMatching.Match(Frame(0), Frame(700)));
    [Fact]
    public void RepeatedRowsRemainAmbiguousRatherThanGuessingADirection()
    {
        PixelFrame Repeated(int offset)
        {
            var frame = Frame(0);
            for (int y = Header; y < Height - Footer; y++)
                Buffer.BlockCopy(page, ((y - Header + offset) % 32) * Width * 4, frame.Bgra, y * Width * 4, Width * 4);
            return frame;
        }
        Assert.Null(DesktopScrollMatching.Match(Repeated(0), Repeated(16)));
    }
    [Fact]
    public async Task SmallStepsThroughLargeFrozenHeaderRemainContinuousAndKeepHeaderOnce()
    {
        using var control = new ManualCaptureControl();
        var source = new Source(control);
        var progress = new Trace();
        var result = await new ManualScrollCaptureSession(TimeSpan.Zero).CaptureAsync(source, control, progress, CancellationToken.None, desktopMode: true);
        Assert.DoesNotContain(progress.Values, p => p.Partial);
        Assert.Equal(Height + 160, result.Height);
        var expected = new byte[Width * result.Height * 4]; var first = Frame(0);
        Buffer.BlockCopy(first.Bgra, 0, expected, 0, Header * Width * 4);
        Buffer.BlockCopy(page, 0, expected, Header * Width * 4, (Body + 160) * Width * 4);
        Buffer.BlockCopy(first.Bgra, (Height - Footer) * Width * 4, expected, (Header + Body + 160) * Width * 4, Footer * Width * 4);
        Assert.Equal(expected, result.Bgra);
        Assert.Equal(6, progress.Values[^1].Frames);
    }
    private static PixelFrame Frame(int offset)
    {
        var b = new byte[Width * Height * 4];
        for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++)
        {
            int i = (y * Width + x) * 4;
            if (y >= Header && y < Height - Footer) Buffer.BlockCopy(page, ((y - Header + offset) * Width + x) * 4, b, i, 4);
            else { b[i] = (byte)(80 + y % 50); b[i + 1] = 160; b[i + 2] = (byte)(x % 80); b[i + 3] = 255; }
        }
        return new(Width, Height, b);
    }
    private static byte[] CreatePage()
    {
        var b = new byte[Width * 1000 * 4]; new Random(54).NextBytes(b);
        for (int y = 0; y < 1000; y++) for (int x = 0; x < Width; x++)
        {
            int i = (y * Width + x) * 4;
            if (x >= 180) b[i] = b[i + 1] = b[i + 2] = 255;
            b[i + 3] = 255;
        }
        return b;
    }
    private sealed class Source(ManualCaptureControl control) : IScrollCaptureSource
    {
        private int reads;
        public Task<PixelFrame> ReadAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            int offset = Math.Min(5, reads / 5) * 32;
            reads++; control.RequestFrame();
            if (reads > 29) control.RequestFinish();
            return Task.FromResult(Frame(offset));
        }
        public Task<PixelFrame> ScrollAsync(int units, CancellationToken token) => throw new InvalidOperationException("Manual capture must not inject scrolling.");
    }
    private sealed class Trace : IProgress<ManualCaptureProgress>
    { public List<ManualCaptureProgress> Values { get; } = []; public void Report(ManualCaptureProgress value) => Values.Add(value); }
}
