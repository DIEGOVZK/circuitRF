// brief-img-13-board-photos.md §4 — a photographed board's copper, under the mask and exposed, traces onto the top copper
// and a highlight leaves no hole (R-im13-3, R-im13-4).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Clipper2Lib;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Recognition.Image;
using CircuitRF.Ui.Recognition;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Imaging;

public sealed class BoardPhotoTraceTests(ITestOutputHelper output)
{
    [Fact]
    public void ALineUnderTheMask_AndItsExposedPads_TraceOntoTheTopCopper_AndAHighlightLeavesNoHole()
    {
        var photo = BoardPhotoPictures.Source();
        var reading = BoardPhoto.Read(photo, BoardPhotoPictures.Corners());
        Assert.True(reading.Ok, reading.Refusal);
        var trace = ImageTrace.Trace(BoardPhoto.Apply(new ImageTraceInput { Source = photo, Technology = TracePictures.Tech() }, reading));
        foreach (string line in trace.Report.Lines()) output.WriteLine(line);
        Assert.True(trace.Ok, trace.Refusal);
        Assert.Equal(ImageScaleKind.Corners, trace.Scale!.Kind);

        var top = trace.Layers.Single(l => l.Name == "Top Copper").Shapes;
        (double X, double Y) At(double xMm, double yMm)
        {
            var p = reading.RectangleToPixel(xMm * 1e-3, yMm * 1e-3);
            return trace.Frame!.Value.ToTarget(p.X, p.Y);
        }

        // One shape holds the line end to end, both pads, and the spots under both highlights.
        var along = new List<(double, double)>();
        for (double x = BoardPhotoPictures.LineX0 + 1; x <= BoardPhotoPictures.LineX1 - 1; x += 1) along.Add((x, BoardPhotoPictures.LineY));
        foreach (var (x0, x1) in BoardPhotoPictures.Pads) along.Add(((x0 + x1) / 2, BoardPhotoPictures.LineY));
        along.AddRange(BoardPhotoPictures.Highlights);
        var holder = top.Where(s => Contains(s, At(along[0].Item1, along[0].Item2))).ToList();
        var shape = Assert.Single(holder);
        foreach (var (x, y) in along) Assert.True(Contains(shape, At(x, y)), $"({x}, {y}) mm is not copper");

        // The mask beside the line is not copper.
        Assert.DoesNotContain(top, s => Contains(s, At(30, BoardPhotoPictures.LineY + 3)));
        Assert.DoesNotContain(top, s => Contains(s, At(30, BoardPhotoPictures.LineY - 3)));
    }

    /// <summary>R-im13-2 in the dialog: Board corners… shows the photo as taken, its handles dragged onto the board and
    /// the size typed read it square — the canvas then shows the rectified picture and the scale is the corners'.</summary>
    [Fact]
    public async Task TheDialogsCornerTool_ReadsThePhotoSquare()
    {
        string ws = Directory.CreateTempSubdirectory("crf-im13-").FullName;
        try
        {
            string tech = Path.Combine(ws, "board.ctech");
            TechPersistence.SaveToFile(tech, TracePictures.Tech());
            var photo = BoardPhotoPictures.Source();
            var vm = new ImageSourceViewModel(ws, [new ImageTechnologyChoice("board", tech)], 0, photo, makeSchematic: false,
                                              ImageRecognitionRunner.Instance, debounce: TimeSpan.Zero, presetDirectory: Path.Combine(ws, "presets"));
            await vm.Recognition;

            vm.Scale.SelectedChoice = vm.Scale.Choices.Single(c => c.Kind == ImageScaleChoiceKind.Corners);
            Assert.Same(photo.Raster, vm.Raster);
            var corners = BoardPhotoPictures.Corners().Points;
            for (int k = 0; k < 4; k++) vm.Scale.MoveCorner(k, corners[k]);
            vm.Scale.SizeText = "60 mm × 40 mm";
            vm.Scale.ApplyCornersCommand.Execute(null);
            await vm.Recognition;

            Assert.NotSame(photo.Raster, vm.Raster);
            Assert.StartsWith("Board corners", vm.Scale.SelectedChoice!.Label);
            Assert.True(vm.CreateCommand.CanExecute(null), vm.CreateTip);
        }
        finally
        {
            try { Directory.Delete(ws, true); } catch { /* best effort */ }
        }
    }

    private static bool Contains(LayoutShape shape, (double X, double Y) p)
    {
        var pt = new Point64((long)Math.Round(p.X), (long)Math.Round(p.Y));
        static Path64 Ring(long[] xy) { var r = new Path64(); for (int i = 0; i < xy.Length; i += 2) r.Add(new Point64(xy[i], xy[i + 1])); return r; }
        switch (shape)
        {
            case RectShape r:
                return pt.X >= Math.Min(r.X1, r.X2) && pt.X <= Math.Max(r.X1, r.X2) && pt.Y >= Math.Min(r.Y1, r.Y2) && pt.Y <= Math.Max(r.Y1, r.Y2);
            case PolygonShape g:
                if (Clipper.PointInPolygon(pt, Ring(g.Xy)) == PointInPolygonResult.IsOutside) return false;
                return g.Holes is null || g.Holes.All(h => Clipper.PointInPolygon(pt, Ring(h)) == PointInPolygonResult.IsOutside);
            default:
                return false;
        }
    }
}
