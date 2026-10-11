// brief-ptu-4-axis-label-click.md R-ptu4-1, R-ptu4-2: a tick label clicked on the canvas asks for its value, and two
// values on one axis calibrate it through the typed row's own grammar.
//
// THE PICTURE is the committed two-trace legend plot with its y axis made unreadable the way a misread makes it: every y
// label but the top one is erased and the top one's pixels are pasted again at the bottom tick, so two labels read the
// same number and no fit exists — the y axis is left uncalibrated with two labels boxed on it, which is what the user
// meets in the field.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Imaging.Plots;
using CircuitRF.Ui.Recognition;
using Xunit;

namespace CircuitRF.Ui.Tests.PlotRecognition;

public sealed class PlotAxisLabelClickTests : IDisposable
{
    private readonly string _ws = Directory.CreateTempSubdirectory("crf-ptu4-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_ws, true); } catch { /* best effort */ }
    }

    /// <summary>The dialog read the damaged picture; the two y labels' true values, top then bottom.</summary>
    private async Task<(ImageSourceViewModel Vm, double Top, double Bottom)> Read()
    {
        var source = ImageSource.FromFile(FixturePaths.Require(Path.Combine(ForeignPlots.Folder, "plot-legend.png"))).Source!;
        var first = PlotReading.Read([new PlotPictureInput(source, "plot-legend.png")]).Plots[0];
        var labels = first.Rect!.YLabels.OrderBy(l => l.Pixel).ToList();
        Assert.True(labels.Count >= 3, $"{labels.Count} y labels read");
        var (top, bottom) = (labels[0], labels[^1]);

        var r = source.Raster;
        byte[] rgba = (byte[])r.Rgba.Clone();
        int bg = r.Rgb(1, 1);
        foreach (var l in labels)
            for (int y = l.Box.Top - 1; y <= l.Box.Bottom + 1; y++)
                for (int x = l.Box.Left - 1; x <= l.Box.Right + 1; x++)
                    Set(x, y, bg);
        int dy = (int)Math.Round(bottom.Box.CentreY - top.Box.CentreY), dx = bottom.Box.Right - top.Box.Right;
        for (int y = top.Box.Top; y <= top.Box.Bottom; y++)
            for (int x = top.Box.Left; x <= top.Box.Right; x++)
            {
                Set(x, y, r.Rgb(x, y));
                Set(x + dx, y + dy, r.Rgb(x, y));
            }

        var vm = new ImageSourceViewModel(_ws, [], 0, null, true, debounce: TimeSpan.Zero, presetDirectory: Path.Combine(_ws, "presets"));
        vm.ReadPlot(ImageSource.FromBytes(new RasterImage(r.Width, r.Height, rgba).EncodePng(), "damaged.png").Source!);
        await vm.Recognition;
        Assert.True(vm.Plots.Frames[0].NeedsY, vm.Plots.Frames[0].Calibration);
        return (vm, top.Value, bottom.Value);

        void Set(int x, int y, int rgb)
        {
            int i = 4 * (y * r.Width + x);
            (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = ((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, 255);
        }
    }

    /// <summary>The y labels on the canvas, top first.</summary>
    private static ImageOverlayItem[] YLabels(ImageSourceViewModel vm) =>
        [.. vm.Overlay.Items.Where(i => i.Id.StartsWith("tick:1:10", StringComparison.Ordinal)).OrderBy(i => i.Bounds.CentreY)];

    private static ImageOverlayItem AnXLabel(ImageSourceViewModel vm) =>
        vm.Overlay.Items.First(i => i.Id.StartsWith("tick:1:", StringComparison.Ordinal) && int.Parse(i.Id.Split(':')[2]) < 1000);

    [Fact]
    public async Task ALabel_LeftOfTheFrame_IsY_Below_IsX()
    {
        var (vm, _, _) = await Read();
        vm.SelectedItem = YLabels(vm)[0];
        Assert.True(vm.Plots.AskingLabel);
        Assert.Equal('y', vm.Plots.Label!.Axis);
        vm.SelectedItem = AnXLabel(vm);
        Assert.Equal('x', vm.Plots.Label!.Axis);
        vm.SelectedItem = null;
        Assert.False(vm.Plots.AskingLabel);
    }

    [Fact]
    public async Task TwoYValues_CalibrateTheAxis()
    {
        var (vm, top, bottom) = await Read();
        var y = YLabels(vm);
        Assert.Equal(2, y.Length);

        vm.SelectedItem = y[0];
        vm.Plots.LabelText = top.ToString(System.Globalization.CultureInfo.InvariantCulture) + " dB";
        Assert.Null(vm.Plots.CommitLabel());
        vm.SelectedItem = y[1];
        vm.Plots.LabelText = bottom.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Null(vm.Plots.CommitLabel());
        await vm.Recognition;

        string text = vm.Plots.Frames[0].YAxisText;
        Assert.NotNull(PlotSessionViewModel.Clicks(text, 'y'));
        Assert.False(vm.Plots.Frames[0].NeedsY, vm.Plots.Frames[0].Calibration);
        Assert.True(vm.Plots.Frames[0].ShowsY);   // the row shows the pairs the clicks wrote
    }

    /// <summary>PTU-7: a typed value replaces what was read on the canvas at once — one value alone blocks the reading, so
    /// waiting for a re-read would leave the misread text standing.</summary>
    [Fact]
    public async Task ATypedValue_ShowsOnTheCanvas_BeforeAnyReading()
    {
        var (vm, _, _) = await Read();
        vm.SelectedItem = YLabels(vm)[0];
        vm.Plots.LabelText = "7";
        Assert.Null(vm.Plots.CommitLabel());
        Assert.NotNull(vm.Plots.Blocked());
        Assert.Equal("7", YLabels(vm)[0].Label);
    }

    [Fact]
    public void AThirdValue_ReplacesTheOlder() =>
        Assert.Equal("200px=-10,300px=-20", PlotAxisLabels.Merge("100px=0,200px=-10", 'y', 300, "-20"));

    [Fact]
    public async Task AnXNumber_WithNoUnitRead_IsRefused()
    {
        var (vm, _, _) = await Read();
        vm.SelectedItem = AnXLabel(vm);
        var plot = vm.Plots.Session!.Plots[0];
        // Whatever the picture's x labels say, a plot with no unit read refuses a bare number.
        var unstated = plot with { Rect = plot.Rect! with { XUnit = 1e9, XUnitStated = false, XIsFrequency = true } };
        Assert.Null(PlotAxisLabels.Value("2.4", 'x', unstated, "", out string? why));
        Assert.Contains("Say the unit", why);
        Assert.Equal("2.4GHz", PlotAxisLabels.Value("2.4 GHz", 'x', unstated, "", out _));

        // A refusal stays in the editor and reaches the status line.
        vm.Plots.LabelText = "two";
        Assert.Equal(vm.Plots.CommitLabel(), vm.Status);
        Assert.True(vm.Plots.AskingLabel);
    }
}
