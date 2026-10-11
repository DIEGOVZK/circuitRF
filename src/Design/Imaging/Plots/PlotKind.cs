// What kind of plot a picture holds — brief-img-14-sparameter-plots-to-touchstone.md R-im14-1.
//
// A plot is told apart by its FRAME, not by its curves: a rectangular plot has two long perpendicular lines meeting at a
// corner, with periodic grid lines or ticks along both; a Smith chart an outer circle holding circles tangent at one
// point of it; a polar chart concentric circles about the outer circle's centre. PlotFrames.Find measures all three;
// this file names the answer and is what ImageKind consults before it reads a picture as a schematic, because a plot's
// grid would otherwise read as wires.

namespace CircuitRF.Design.Imaging.Plots;

/// <summary>A plot's frame: rectangular (value against x), a Smith chart, or a polar chart.</summary>
public enum PlotType { Rectangular, Smith, Polar }

/// <summary>The kind of one plot on a picture, and how it was told (R-im14-1).</summary>
public static class PlotKind
{
    /// <summary>The wire spelling: <c>rectangular</c>, <c>smith</c> or <c>polar</c>.</summary>
    public static string Name(PlotType type) => type switch
    {
        PlotType.Smith => "smith",
        PlotType.Polar => "polar",
        _              => "rectangular",
    };

    /// <summary>A picture is a plot when it holds at least one plot frame whose area is at least this share of the
    /// picture: a schematic's box (an IC body, a filter block) is a rectangle too, but a fraction of the drawing, and it
    /// carries no grid.</summary>
    public const double MinFrameShare = 0.08;

    /// <summary>A rectangular frame counts as a plot only with at least this many grid lines or ticks along EACH axis:
    /// two perpendicular lines alone are a box.</summary>
    public const int MinTicksPerAxis = 3;

    /// <summary>The plots <paramref name="raster"/> holds that are large enough to make it a plot picture — empty when it
    /// is not one. Pure.</summary>
    public static IReadOnlyList<PlotFrame> Detect(RasterImage raster)
    {
        ArgumentNullException.ThrowIfNull(raster);
        if (raster.Width < ImageKind.MinSide || raster.Height < ImageKind.MinSide) return [];
        var ink = PlotInk.Of(raster);
        double area = (double)raster.Width * raster.Height;
        return [.. PlotFrames.Find(ink).Where(f => f.Box.Width * (double)f.Box.Height >= MinFrameShare * area)];
    }
}
