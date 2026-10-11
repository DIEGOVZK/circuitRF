// brief-ptu-6-drag-a-box.md R-ptu6-2, R-ptu6-3: a box dragged with Read only here crops the shown picture and reads the
// crop in its place, and Restore whole picture brings the original back; a box dragged with Ignore here lands in the
// hints of the plot whose frame holds its centre and the picture is read again without what is under it.
//
// THE PICTURE is the committed one-trace plot with a fake legend sample painted inside its frame: a short sloped magenta stroke in
// the empty bottom-right corner, long enough to be read as a trace of its own.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Layout.Recognition.Image;
using CircuitRF.Design.Schematic.Recognition;
using CircuitRF.Ui.Recognition;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests.PlotRecognition;

public sealed class PlotBoxToolTests : IDisposable
{
    private readonly string _ws = Directory.CreateTempSubdirectory("crf-ptu6-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_ws, true); } catch { /* best effort */ }
    }

    /// <summary>Where the fake legend sample is drawn: a magenta stroke at about −24 to −27 dB from 4.0 to 4.6 GHz, where
    /// the curve never goes. Sloped, as a short curve piece: a level stroke reads as grid ink.</summary>
    private static readonly PixelBox Sample = new(600, 362, 690, 392);

    private const int Magenta = 0xC020C0;

    private async Task<ImageSourceViewModel> Read()
    {
        using var bmp = SKBitmap.Decode(FixturePaths.Require(Path.Combine(ForeignPlots.Folder, "plot-plain.png")));
        using (var canvas = new SKCanvas(bmp))
        using (var pen = new SKPaint { Color = new SKColor(0xC0, 0x20, 0xC0), StrokeWidth = 2.5f, IsAntialias = true, Style = SKPaintStyle.Stroke })
            canvas.DrawLine(Sample.Left + 4, Sample.Top + 4, Sample.Right - 4, Sample.Bottom - 4, pen);
        var vm = new ImageSourceViewModel(_ws, [], 0, null, true, debounce: TimeSpan.Zero, presetDirectory: Path.Combine(_ws, "presets"));
        vm.ReadPlot(ImageSource.FromBytes(RasterImage.FromBitmap(bmp).EncodePng(), "plot.png").Source!);
        await vm.Recognition;
        return vm;
    }

    private static bool ReadsTheSample(ImageSourceViewModel vm) =>
        vm.Plots.Session!.Traces.Any(t => Math.Abs((t.Curve.Rgb >> 16) - (Magenta >> 16)) < 40 && (t.Curve.Rgb & 0xFF) > 0x80
                                          && ((t.Curve.Rgb >> 8) & 0xFF) < 0x80);

    [Fact]
    public async Task ReadOnlyHere_CropsAndReplaces_RestoreBringsTheOriginalBack()
    {
        var vm = await Read();
        var original = vm.Plots.Pictures[0].Source;

        vm.ReadOnlyHereArmed = true;
        Assert.True(vm.CommitBox(new PixelRect(400.4, 30, 100, 480)));   // dragged right to left: still the box
        Assert.Equal(PlotBoxTool.None, vm.BoxTool);

        var cropped = vm.Plots.Pictures.Single().Source;
        Assert.Equal((301, 450), (cropped.Raster.Width, cropped.Raster.Height));
        Assert.Same(cropped.Raster, vm.Raster);
        Assert.Equal("plot (cropped).png", cropped.OriginalName);
        Assert.True(vm.IsPlotPicture);
        Assert.True(vm.CanRestorePicture);
        await vm.Recognition;

        vm.RestorePictureCommand.Execute(null);
        Assert.Same(original, vm.Plots.Pictures.Single().Source);
        Assert.Same(original, vm.Source);
        Assert.False(vm.CanRestorePicture);
        await vm.Recognition;
    }

    [Fact]
    public async Task IgnoreHere_LandsInThePlotsHints_AndTheSampleIsNotRead()
    {
        var vm = await Read();
        Assert.True(ReadsTheSample(vm), string.Join(", ", vm.Plots.Session!.Traces.Select(t => t.Curve.Hex)));

        vm.IgnoreHereArmed = true;
        Assert.True(vm.CommitBox(new PixelRect(Sample.Left - 6, Sample.Top - 6, Sample.Right + 7, Sample.Bottom + 7)));
        var hints = vm.Plots.ReadingOptions().Hints;
        Assert.Equal([1], hints.Keys);
        Assert.Equal(new PixelBox(Sample.Left - 6, Sample.Top - 6, Sample.Right + 6, Sample.Bottom + 6), hints[1].Exclude.Single());

        await vm.Recognition;
        Assert.False(ReadsTheSample(vm));
        Assert.NotEmpty(vm.Plots.Session!.Traces);

        // Replace starts over: the box goes with the session it was drawn on.
        vm.Replace(vm.Plots.Pictures[0].Source);
        Assert.Empty(vm.Plots.ReadingOptions().Hints);
        await vm.Recognition;
    }

    [Fact]
    public async Task IgnoreHere_OutsideEveryFrame_IsRefused()
    {
        var vm = await Read();
        vm.IgnoreHereArmed = true;
        Assert.False(vm.CommitBox(new PixelRect(2, 2, 60, 40)));
        Assert.Contains("outside every plot", vm.Status);
        Assert.Empty(vm.Plots.IgnoreBoxes);
    }
}
