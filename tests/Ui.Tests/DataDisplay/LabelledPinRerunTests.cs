// ================================================================
//  LabelledPinRerunTests.cs
//  A pin written as a LABEL (`DC1.I["IP1"]`) follows the label across a re-run.
//
//  Reported: renaming an IProbe IP1 → IDC and re-running left a Table trace on DC1.I["IP1"]
//  showing a current — IDC's, read through the index "IP1" had when the spec was parsed. Only
//  deleting and re-adding the trace showed it as invalid.
// ================================================================

using System.Linq;
using CircuitRF.Render.DataDisplay;
using RfCore;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Ui.Tests.DataDisplay;

public sealed class LabelledPinRerunTests
{
    private static DataSet DcRun(string[] probes, double[] amps)
    {
        var ds = new DataSet();
        ds.AddToGroup("DC1", "I", new DataCube(
            new[] { new Axis("branch", Enumerable.Range(0, probes.Length).Select(i => (double)i).ToArray(), "", probes) },
            amps));
        return ds;
    }

    private static Trace PinnedTo(string label, int index)
    {
        var t = new Trace(new SNP(new[] { 1e9 }, 2), MatrixType.S, 0, 0, DependentVarFormat.Db);
        t.CubeName   = "DC1.I";
        t.Expression = $"DC1.I[\"{label}\"]";
        t.Slice      = new[] { new AxisSlice("branch", AxisRole.PinToIndex, index, Label: label) };
        return t;
    }

    [Fact]
    public void RenamedProbe_IsInvalid_AndRenamingBackRestoresIt()
    {
        var t = PinnedTo("IP1", 0);
        TraceResolve.SetCubeDataFrom(t, DcRun(new[] { "IP1" }, new[] { 0.5 }), PlotType.Table, FreqUnit.GHz);
        Assert.Null(t.InvalidSpecText);
        Assert.Equal(0.5, t.CubeReal![0]);

        // Same shape, different name: the stale index would still read IDC's 0.7 A.
        TraceResolve.SetCubeDataFrom(t, DcRun(new[] { "IDC" }, new[] { 0.7 }), PlotType.Table, FreqUnit.GHz);
        Assert.Equal("DC1.I[\"IP1\"]", t.InvalidSpecText);
        Assert.Contains("IP1", t.ExpressionError);

        TraceResolve.SetCubeDataFrom(t, DcRun(new[] { "IP1" }, new[] { 0.9 }), PlotType.Table, FreqUnit.GHz);
        Assert.Null(t.InvalidSpecText);
        Assert.Equal(0.9, t.CubeReal![0]);
    }

    [Fact]
    public void ReorderedProbes_ReadTheNamedOne()
    {
        var t = PinnedTo("IP1", 0);
        TraceResolve.SetCubeDataFrom(t, DcRun(new[] { "IP2", "IP1" }, new[] { 0.2, 0.1 }), PlotType.Table, FreqUnit.GHz);
        Assert.Null(t.InvalidSpecText);
        Assert.Equal(0.1, t.CubeReal![0]);
        Assert.Equal(1, t.Slice![0].Index);
    }
}
