// A photograph traced as a layout with no corners picked — a bare board under a warm lamp, its laminate the largest
// colour. Area order read the laminate as the top copper and the traces as the bottom; an impedance scale on that bottom
// layer, which has no reference, took the cross-section search's starting width as the answer. And a picture with no
// copper at all is still a layout worth writing when its picture is kept under the result. A scale picked on a line is
// the picture's, so a region dragged afterwards does not change it.

using System;
using System.IO;
using System.Linq;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Recognition.Image;
using Xunit;

namespace CircuitRF.Ui.Tests.Imaging;

public sealed class PhotographTraceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("crf-imgphoto-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private const int W = 400, H = 260, StripTop = 120, StripWidth = 26;

    /// <summary>A dark bench, a tan laminate filling most of it, and one copper strip across — every pixel jittered as a
    /// sensor jitters it, so no colour repeats the way a drawing's do.</summary>
    private static RasterImage Photo()
    {
        var rng = new Random(1);
        var rgba = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                bool board = x >= 20 && x < W - 20 && y >= 20 && y < H - 20;
                bool strip = board && y >= StripTop && y < StripTop + StripWidth;
                (int r, int g, int b) = strip ? (212, 127, 79) : board ? (186, 154, 118) : (32, 28, 23);
                int i = (y * W + x) * 4;
                rgba[i] = (byte)Math.Clamp(r + rng.Next(-6, 7), 0, 255);
                rgba[i + 1] = (byte)Math.Clamp(g + rng.Next(-6, 7), 0, 255);
                rgba[i + 2] = (byte)Math.Clamp(b + rng.Next(-6, 7), 0, 255);
                rgba[i + 3] = 255;
            }
        return new RasterImage(W, H, rgba);
    }

    private static ImageTraceInput Input(RasterImage raster, ImageScaleStatement? scale = null, ImageLayerMap? map = null,
                                         bool underlay = true) => new()
    {
        Source = TracePictures.Source(raster), Technology = TracePictures.Tech(), LayerMap = map,
        Scale = scale ?? ImageScaleStatement.Auto, Options = new ImageTraceOptions { KeepUnderlay = underlay },
    };

    private static readonly PixelPoint OnStrip = new(200, StripTop + StripWidth / 2.0);

    [Fact]
    public void APhotograph_TracesItsCopperOnTheTopCopper_AndNotItsLaminate()
    {
        Assert.True(ImageKind.IsPhotograph(Photo()));
        var tech = TracePictures.Tech();
        var copper = LineCalculator.CopperLayerNames(tech);

        var r = ImageTrace.Trace(Input(Photo()));

        var mapped = r.LayerMap!.Rows.Where(row => row.Role == ImageLayerRole.Layer).ToList();
        Assert.NotEmpty(mapped);
        Assert.All(mapped, row => Assert.Equal([copper[0]], row.Layers));
        var laminate = r.LayerMap.Rows.Where(row => row.Role != ImageLayerRole.Background).MaxBy(row => row.Share)!;
        Assert.Equal(ImageLayerRole.Ignore, laminate.Role);
    }

    [Fact]
    public void AnImpedanceScale_IsTheModelsWidth_AndOnALayerWithNoReferenceItIsRefused()
    {
        var tech = TracePictures.Tech();
        var copper = LineCalculator.CopperLayerNames(tech);
        double modelW = LineCalculator.Calculate(tech, new LineCalcRequest(copper[0], [], [50])).Rows[0].Model!.WidthM;

        var top = ImageTrace.Trace(Input(Photo(), ImageScaleStatement.Impedance(OnStrip, 50)));
        Assert.NotNull(top.Scale);
        Assert.Equal(modelW, top.Scale!.MetresPerPixel * StripWidth, modelW * 0.05);

        // The strip read as the bottom copper: nothing below it to return on, so there is no width — and no scale.
        var map = top.LayerMap!;
        foreach (var row in map.Rows.Where(row => row.Role == ImageLayerRole.Layer)) map = map.With(row.Cluster, ImageLayerRole.Layer, copper[^1]);
        var bottom = ImageTrace.Trace(Input(Photo(), ImageScaleStatement.Impedance(OnStrip, 50, copper[^1]), map));
        Assert.Null(bottom.Scale);
        Assert.StartsWith($"The technology gives no width for 50 Ω on {copper[^1]}", bottom.Refusal);
    }

    [Fact]
    public void NoCopper_WithThePictureKept_WritesThePictureAlone_AndWithoutItIsRefused()
    {
        var first = ImageTrace.Trace(Input(Photo()));
        var none = first.LayerMap!;
        foreach (var row in none.Rows.Where(row => row.Role == ImageLayerRole.Layer)) none = none.With(row.Cluster, ImageLayerRole.Ignore);
        var scale = ImageScaleStatement.Stated(25.4e-6);

        Assert.Equal(ImageTrace.NoCopperRefusal, ImageTrace.Trace(Input(Photo(), scale, none, underlay: false)).Refusal);

        var run = ImageTrace.Run(Input(Photo(), scale, none), ImageTraceTarget.NewCell(_dir, "traced"));
        Assert.True(run.Ok, run.Refusal);
        Assert.Empty(run.Result.Shapes);
        Assert.NotNull(run.Result.Underlay);
        Assert.Contains("\"$type\": \"Bitmap\"", File.ReadAllText(run.LayoutPath!));
    }

    [Fact]
    public void AnImpedanceScale_IsThePictures_NotTheRegions()
    {
        var scale = ImageScaleStatement.Impedance(OnStrip, 50);
        double whole = ImageTrace.Trace(Input(Photo(), scale)).Scale!.MetresPerPixel;

        // A region leaving the picked line out, and one whose edge cuts across the line just beside the pick.
        foreach (var scope in new[] { new PixelRect(20, 20, 380, StripTop - 10), new PixelRect(195, 20, 380, 240) })
        {
            var r = ImageTrace.Trace(Input(Photo(), scale) with { Scope = scope });
            Assert.Null(r.Refusal);
            Assert.Equal(whole, r.Scale!.MetresPerPixel, whole * 1e-9);
        }
    }
}
