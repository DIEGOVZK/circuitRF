// ================================================================
//  OutOfRangeLineTests.cs — one far-out sample must not erase a trace's line.
//
//  A power sweep with one unconverged point stored DE = −9.9e11 there. In the window the trace's
//  markers drew (each is its own small shape) and its line did not draw at all; the CPU raster the
//  render verb uses drew it. The GPU canvas gives up on a path with a coordinate that far out, so
//  the renderer clips each segment to a box a few canvas sizes wide before it reaches the path —
//  which is what this holds: no coordinate beyond the box reaches Skia, and the in-range segments
//  are still there.
// ================================================================

using CircuitRF.Render.DataDisplay;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests.DataDisplay;

public sealed class OutOfRangeLineTests
{
    [Fact]
    public void AHugeValueReachesThePathClippedAndTheRestOfTheLineIsKept()
    {
        const double W = 200, H = 100;
        using var path = new SKPath();
        var line = new TraceRenderer.ClippedPolyline(path, (W, H));
        foreach (var p in new SKPoint[] { new(10, 50), new(60, 50), new(100, -9.9e11f), new(140, 50), new(190, 50) })
            line.Add(p);

        var b = path.Bounds;
        Assert.InRange(b.Top, -4 * W - 1, 0);
        Assert.InRange(b.Bottom, 50, 51);
        Assert.Equal(10, b.Left, 3);
        Assert.Equal(190, b.Right, 3);
        // The flat ends are unchanged: the first and last points are the samples themselves.
        Assert.Equal(new SKPoint(10, 50), path.Points[0]);
        Assert.Equal(new SKPoint(190, 50), path.Points[^1]);
    }
}
