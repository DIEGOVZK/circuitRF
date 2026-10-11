// PTU-7: the file's port count is the user's to say. The entry combos offer S11 … Snn for it; an entry beyond it is
// kept (shown, and waited on) rather than cleared, and Undo puts back the assignments and the ports step by step.
// The picture is the committed two-trace legend plot.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircuitRF.Design.Imaging;
using CircuitRF.Ui.Recognition;
using Xunit;

namespace CircuitRF.Ui.Tests.PlotRecognition;

public sealed class PlotPortsTests : IDisposable
{
    private readonly string _ws = Directory.CreateTempSubdirectory("crf-ptu7-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_ws, true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task FewerPorts_KeepAnEntryBeyondThem_AndUndoPutsItBack()
    {
        var vm = new ImageSourceViewModel(_ws, [], 0, null, true, debounce: TimeSpan.Zero, presetDirectory: Path.Combine(_ws, "presets"));
        vm.ReadPlot(ImageSource.FromFile(FixturePaths.Require(Path.Combine(ForeignPlots.Folder, "plot-legend.png"))).Source!);
        await vm.Recognition;
        var plots = vm.Plots;
        string name = plots.Traces[0].Name;

        plots.PortsText = "4";
        Assert.Equal(2 + 16, plots.EntryChoices.Count);
        plots.Traces[0].EntryChoice = "S41";
        await vm.Recognition;

        plots.PortsText = "2";
        Assert.Equal(2 + 4, plots.EntryChoices.Count);
        var row = plots.TraceRow(name)!;
        Assert.Null(row.EntryChoice);
        Assert.Equal("S41 · beyond 2 ports", row.EntryPlaceholder);
        await vm.Recognition;
        Assert.Contains("beyond the file's 2 ports", plots.Refusal);

        plots.PortsText = "x";
        Assert.NotNull(plots.Blocked());

        plots.UndoCommand.Execute(null);   // "x" → "2"
        plots.UndoCommand.Execute(null);   // "2" → "4"
        Assert.Equal("4", plots.PortsText);
        plots.UndoCommand.Execute(null);   // the S41 assignment
        await vm.Recognition;
        Assert.NotEqual("S41", plots.TraceRow(name)!.EntryChoice);
        plots.UndoCommand.Execute(null);   // "4" → empty, the first change
        Assert.Equal("", plots.PortsText);
        Assert.False(plots.UndoCommand.CanExecute(null));
    }
}
