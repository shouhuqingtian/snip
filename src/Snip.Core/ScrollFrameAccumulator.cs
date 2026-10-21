namespace Snip.Core;

/// <summary>Retains one viewport and stitched body bytes, rather than every overlapping full screenshot.</summary>
public sealed class ScrollFrameAccumulator : IDisposable
{
    private readonly MemoryStream body = new();
    private readonly int width, viewportHeight, stride, maximumFrames;
    private int header, footer, origin;
    public PixelFrame Last { get; private set; }
    public int FrameCount { get; private set; } = 1;
    public int Height => viewportHeight + origin;
    public long RetainedBytes => body.Capacity + Last.Bgra.Length;
    public ScrollFrameAccumulator(PixelFrame first, int maximumFrames = 160)
    {
        if (maximumFrames is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(maximumFrames));
        this.maximumFrames = maximumFrames;
        if (first.Width <= 0 || first.Height <= 0 || first.Bgra.Length != (long)first.Width * first.Height * 4)
            throw new ArgumentException("截图像素数据无效。");
        width = first.Width; viewportHeight = first.Height; stride = checked(width * 4); Last = first;
        if ((long)width * first.Height > 60_000_000) throw new InvalidOperationException("长图达到图像大小上限，请分段截图。");
        body.Write(first.Bgra);
    }
    public void Append(PixelFrame frame, ScrollMatch match)
    {
        if (frame.Width != width || frame.Height != viewportHeight || frame.Bgra.Length != (long)width * viewportHeight * 4)
            throw new InvalidOperationException("采集区域尺寸发生变化，请重新框选。");
        int nextHeader = FrameCount == 1 ? match.Header : header, nextFooter = FrameCount == 1 ? match.Footer : footer;
        if (match.Offset <= 0 || match.Offset > viewportHeight - nextHeader - nextFooter || nextHeader < 0 || nextFooter < 0)
            throw new InvalidOperationException("滚动距离过大，请保留更多重叠内容并减慢滚动。");
        if (FrameCount >= maximumFrames || Height + match.Offset > 24000 || (long)(Height + match.Offset) * width > 60_000_000)
            throw new InvalidOperationException("长图达到采集大小上限，请完成后分段截图。");
        if (FrameCount == 1)
        {
            header = nextHeader; footer = nextFooter;
            body.SetLength((viewportHeight - footer) * (long)stride);
        }
        int nextOrigin = origin + match.Offset;
        int repair = viewportHeight - footer - match.Offset - header;
        if (repair > 0)
        {
            body.Position = (nextOrigin + header) * (long)stride;
            body.Write(frame.Bgra, header * stride, repair * stride);
        }
        body.Position = body.Length;
        body.Write(frame.Bgra, (viewportHeight - footer - match.Offset) * stride, match.Offset * stride);
        origin = nextOrigin; Last = frame; FrameCount++;
    }
    public PixelFrame Finish()
    {
        var output = new byte[checked(Height * stride)];
        body.Position = 0; body.ReadExactly(output.AsSpan(0, (Height - footer) * stride));
        if (footer > 0) Buffer.BlockCopy(Last.Bgra, (viewportHeight - footer) * stride, output, (Height - footer) * stride, footer * stride);
        return new(width, Height, output);
    }
    public PixelFrame Finish(PixelRect selection)
    {
        var r = selection.RoundOut();
        if (r.IsEmpty || r.Intersect(new(0, 0, width, Height)) != r) throw new ArgumentOutOfRangeException(nameof(selection));
        int cropWidth = (int)r.Width, cropHeight = (int)r.Height, cropStride = checked(cropWidth * 4);
        var output = new byte[checked(cropHeight * cropStride)];
        var stored = body.GetBuffer();
        for (int row = 0; row < cropHeight; row++)
        {
            int y = (int)r.Y + row;
            bool inFooter = y >= Height - footer;
            var source = inFooter ? Last.Bgra : stored;
            int sourceRow = inFooter ? y - origin : y;
            Buffer.BlockCopy(source, sourceRow * stride + (int)r.X * 4, output, row * cropStride, cropStride);
        }
        return new(cropWidth, cropHeight, output);
    }
    public void Dispose() => body.Dispose();
}
