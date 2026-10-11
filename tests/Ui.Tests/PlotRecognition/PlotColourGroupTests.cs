// brief-ptu-5-one-row-per-colour.md R-ptu5-1 … R-ptu5-3: the traces table's colour groups, and the join before assembly.
// The picture is the committed two-trace legend plot; one of its curves is cut into pieces here, as the reader cuts a
// curve where it crosses a grid line, so the gate does not wait on a reader that fragments.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Design.Imaging.Plots;
using CircuitRF.Ui.Recognition;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.PlotRecognition;

public sealed class PlotColourGroupTests(ITestOutputHelper output)
{
    private static readonly PlotSession Read =
        PlotPictureSet.Read(null, FixturePaths.Require(Path.Combine(ForeignPlots.Folder, "plot-legend.png")));

    private static PlotTraceRead Longest => Read.Traces.OrderByDescending(t => t.Samples.Count).First();

    /// <summary>The share <paramref name="lo"/>..<paramref name="hi"/> of a trace's pixel columns, as trace T<paramref name="number"/>.</summary>
    private static PlotTraceRead Cut(PlotTraceRead t, int number, double lo, double hi, double dy = 0)
    {
        double a = t.Curve.Points.Min(p => p.X), b = t.Curve.Points.Max(p => p.X);
        bool In(double x) => x >= a + lo * (b - a) && x <= a + hi * (b - a);
        return t with
        {
            Number = number,
            Curve = t.Curve with { Points = [.. t.Curve.Points.Where(p => In(p.X)).Select(p => (p.X, p.Y + dy))] },
            Samples = [.. t.Samples.Where(s => In(s.Px))],
        };
    }

    /// <summary>The longest curve in three disjoint pieces, every other trace left as read.</summary>
    private static (PlotSession Session, List<PlotTraceRead> Pieces) Thirds()
    {
        var t = Longest;
        List<PlotTraceRead> pieces = [Cut(t, 101, 0, 0.3), Cut(t, 102, 0.35, 0.65), Cut(t, 103, 0.7, 1)];
        return (Read with { Traces = [.. Read.Traces.Where(x => x != t), .. pieces] }, pieces);
    }

    private static PlotSessionViewModel Apply(PlotSession session)
    {
        var vm = new PlotSessionViewModel();
        vm.Apply(session, TouchstoneAssembly.Build(session));
        return vm;
    }

    [Fact]
    public void PiecesOfOneColour_DisjointInX_AreOneGroup()
    {
        var (session, pieces) = Thirds();
        var vm = Apply(session);
        foreach (var g in vm.Groups) output.WriteLine($"{g.Title}: {g.Names}");
        var group = Assert.Single(vm.Groups, g => g.Members.Any(m => m.Name == "T101"));
        Assert.Equal(pieces.Select(p => p.Name), group.Members.Select(m => m.Name));
        Assert.Equal(session.Traces.Count - 2, vm.Groups.Count);
    }

    [Fact]
    public void TwoSameColourCurves_OverTheSameX_AreTwoGroups()
    {
        var t = Longest;
        var twin = Cut(t, 200, 0, 1, dy: 40);
        var vm = Apply(Read with { Traces = [.. Read.Traces, twin] });
        Assert.NotSame(vm.GroupOf(vm.TraceRow(t.Name)), vm.GroupOf(vm.TraceRow(twin.Name)));
        Assert.Single(vm.GroupOf(vm.TraceRow(twin.Name))!.Members);
    }

    [Fact]
    public void AssigningAGroup_AssignsEveryPiece_AndSimpleDetailedKeepsIt()
    {
        var vm = Apply(Thirds().Session);
        var group = vm.Groups.Single(g => g.Members.Any(m => m.Name == "T101"));
        int changed = 0;
        vm.Changed += () => changed++;

        int s12 = PlotTraceRowViewModel.Entries.ToList().IndexOf("S12");
        group.EntryIndex = s12;
        Assert.All(group.Members, m => Assert.Equal("S12", m.Assignment!.Entry));
        Assert.Equal(1, changed);   // one re-read for the group, not one a piece

        vm.Detailed = true;
        vm.Detailed = false;
        Assert.Equal(s12, group.EntryIndex);
        Assert.All(group.Members, m => Assert.Equal(s12, m.EntryIndex));

        // One piece assigned otherwise (Detailed): the group says mixed.
        group.Members[1].EntryIndex = 1;
        Assert.Equal(-1, group.EntryIndex);
    }

    [Fact]
    public void TheAssembledS21_CoversTheUnionOfItsPieces()
    {
        var (session, pieces) = Thirds();
        var s21 = new PlotAssignment(2, 1, PlotQuantityKind.Db, "user");
        var ignored = new PlotAssignment(0, 0, PlotQuantityKind.Ignored, "user");
        session = session with { Traces = [.. session.Traces.Select(t => t with { Assignment = pieces.Any(p => p.Number == t.Number) ? s21 : ignored })] };

        var built = PlotSessionViewModel.Assemble(session, s => TouchstoneAssembly.Build(s));
        foreach (var l in built.Report) output.WriteLine(l);
        Assert.True(built.Ok, built.Refusal?.Sentence);
        Assert.Contains("joined S21 db: T101 + T102 + T103", built.Report);
        double lo = pieces[0].Samples.Min(s => s.X), hi = pieces[2].Samples.Max(s => s.X);
        // The default grid rounds inwards to a 1-2-5 step: within one step of the union at either end.
        var g = built.Grid!;
        double step = (g.StopHz - g.StartHz) / (g.Points - 1);
        Assert.InRange(g.StartHz, lo, lo + step);
        Assert.InRange(g.StopHz, hi - step, hi);
    }
}
