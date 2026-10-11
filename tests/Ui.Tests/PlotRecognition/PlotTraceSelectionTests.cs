// brief-ptu-3-canvas-table-link-and-overlay.md R-ptu3-1, R-ptu3-2: the canvas and the traces table hold ONE selection.
// A trace chosen on the canvas (or a bridged gap of it) selects its row; a row chosen selects the trace's overlay item;
// and neither side's setter runs the other's back. The picture is the committed two-trace legend plot.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircuitRF.Design.Imaging;
using CircuitRF.Ui.Recognition;
using Xunit;

namespace CircuitRF.Ui.Tests.PlotRecognition;

public sealed class PlotTraceSelectionTests : IDisposable
{
    private readonly string _ws = Directory.CreateTempSubdirectory("crf-ptu3-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_ws, true); } catch { /* best effort */ }
    }

    private async Task<ImageSourceViewModel> Read()
    {
        var vm = new ImageSourceViewModel(_ws, [], 0, null, true, debounce: TimeSpan.Zero, presetDirectory: Path.Combine(_ws, "presets"));
        vm.ReadPlot(ImageSource.FromFile(FixturePaths.Require(Path.Combine(ForeignPlots.Folder, "plot-legend.png"))).Source!);
        await vm.Recognition;
        Assert.True(vm.Plots.Traces.Count >= 2, $"{vm.Plots.Traces.Count} traces read");
        return vm;
    }

    private static ImageOverlayItem TraceItem(ImageSourceViewModel vm, string name) =>
        vm.Overlay.Items.Single(i => i.Id == ImageOverlay.TracePrefix + name);

    [Fact]
    public async Task CanvasToTable_AndBack()
    {
        var vm = await Read();
        var (a, b) = (vm.Plots.Traces[0], vm.Plots.Traces[1]);

        vm.SelectedItem = TraceItem(vm, b.Name);
        Assert.Same(b, vm.Plots.SelectedTrace);

        vm.Plots.SelectedTrace = a;
        Assert.Same(TraceItem(vm, a.Name), vm.SelectedItem);

        // A click on nothing clears both.
        vm.SelectedItem = null;
        Assert.Null(vm.Plots.SelectedTrace);
    }

    [Fact]
    public async Task AGap_SelectsItsTrace()
    {
        var vm = await Read();
        var b = vm.Plots.Traces[1];
        var whole = TraceItem(vm, b.Name);
        vm.SelectedItem = new ImageOverlayItem($"{ImageOverlay.GapPrefix}{b.Name}:7", ImageOverlayClass.Trace, [[(0, 0), (1, 1)]], false,
                                               null, null, false, null, whole.Bounds);
        Assert.Same(b, vm.Plots.SelectedTrace);
        Assert.Same(whole, vm.SelectedItem);
    }

    [Fact]
    public async Task NoReentry_EachSideSetTwice_TheOtherChangesOnceEach()
    {
        var vm = await Read();
        var (a, b) = (vm.Plots.Traces[0], vm.Plots.Traces[1]);
        int items = 0, traces = 0;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ImageSourceViewModel.SelectedItem)) items++; };
        vm.Plots.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PlotSessionViewModel.SelectedTrace)) traces++; };

        vm.SelectedItem = TraceItem(vm, a.Name);
        vm.SelectedItem = TraceItem(vm, b.Name);
        Assert.Equal((2, 2), (items, traces));

        vm.Plots.SelectedTrace = a;
        vm.Plots.SelectedTrace = b;
        Assert.Equal((4, 4), (items, traces));
    }
}
