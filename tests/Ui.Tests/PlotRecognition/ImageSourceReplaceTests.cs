// brief-ptu-1-dialog-layout-bugs.md: Replace… reads a picture INSTEAD of a plot session's pictures, while Add picture…
// and a drop still join it (R-ptu1-2); and the recognition panels show what was read in text that can be copied
// (R-ptu1-3). The pictures are drawn in memory and written to a temporary folder only because Browse… picks a path.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using CircuitRF.Design.Imaging;
using CircuitRF.Ui.Recognition;
using CircuitRF.Ui.Tests.Imaging;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests.PlotRecognition;

public sealed class ImageSourceReplaceTests : IDisposable
{
    private readonly string _ws = Directory.CreateTempSubdirectory("crf-ptu1-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_ws, true); } catch { /* best effort */ }
    }

    private string Picture(string name)
    {
        string path = Path.Combine(_ws, name);
        using var pen = Pictures.Stroke(SKColors.Blue, 2);
        File.WriteAllBytes(path, Pictures.Png(Pictures.Draw(120, 80, c => c.DrawLine(10, 70, 110, 10, pen))));
        return path;
    }

    /// <summary>A dialog reading picture A as a plot, its picker answering with picture B.</summary>
    private (ImageSourceViewModel Vm, string B) PlotSession()
    {
        var vm = new ImageSourceViewModel(_ws, [], 0, null, true, debounce: TimeSpan.Zero,
                                          presetDirectory: Path.Combine(_ws, "presets"));
        vm.ReadPlot(ImageSource.FromFile(Picture("a.png")).Source!);
        string b = Picture("b.png");
        vm.PickPictureAsync = () => Task.FromResult<string?>(b);
        Assert.True(vm.IsPlotPicture);
        return (vm, b);
    }

    private static string[] Names(ImageSourceViewModel vm) => [.. vm.Plots.Pictures.Select(p => p.Source.OriginalName)];

    [Fact]
    public async Task Replace_InPlotMode_LeavesOnlyTheNewPicture()
    {
        var (vm, _) = PlotSession();
        await vm.BrowseCommand.ExecuteAsync(null);
        Assert.Equal(["b.png"], Names(vm));
        Assert.True(vm.IsPlotPicture);
        Assert.Equal("b.png", vm.SourceName);
        await vm.Recognition;
    }

    [Fact]
    public async Task AddPicture_InPlotMode_Joins()
    {
        var (vm, _) = PlotSession();
        await vm.AddPictureCommand.ExecuteAsync(null);
        Assert.Equal(["a.png", "b.png"], Names(vm));
        await vm.Recognition;
    }

    [Fact]
    public async Task Drop_InPlotMode_StillJoins()
    {
        var (vm, b) = PlotSession();
        Assert.True(vm.LoadFile(b));
        Assert.Equal(["a.png", "b.png"], Names(vm));
        await vm.Recognition;
    }

    /// <summary>The read-outs R-ptu1-3 names, by the property each binds.</summary>
    private static readonly string[] Outputs =
        ["SourceName", "SizeText", "Calibration", "RangeText", "FrequencyEvidence", "Source", "Refusal", "FailureText", "Status"];

    [Fact]
    public void NoReadOut_IsAPlainTextBlock()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var found = new List<string>();
        foreach (string file in Directory.GetFiles(Path.Combine(dir!.FullName, "src", "Ui", "Recognition"), "*.axaml"))
        {
            foreach (var block in XDocument.Load(file).Descendants().Where(e => e.Name.LocalName == "TextBlock"))
            {
                // The block's own Text, and its Runs'.
                var texts = block.DescendantsAndSelf().Where(e => e == block || e.Name.LocalName == "Run")
                                 .Select(e => (string?)e.Attribute("Text") ?? "");
                foreach (string text in texts)
                    if (Outputs.Any(p => text == "{Binding " + p + "}") || (text == "{Binding}" && block.Ancestors().Any(a => a.Name.LocalName == "DataTemplate")))
                        found.Add($"{Path.GetFileName(file)}: {text}");
            }
        }
        Assert.Empty(found);
    }
}
