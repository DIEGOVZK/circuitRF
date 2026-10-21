using System.Numerics;
using CircuitRF.Render.DataDisplay;
using RfCore;
using Xunit;

namespace CircuitRF.Ui.Tests.DataDisplay;

/// <summary>
/// A Smith chart's bottom row — <c>freq (a to b GHz)</c> — belongs to a trace over frequency. A cube traced over a loadpull's
/// <c>gridPoint</c> printed <c>freq (0 to 7.2E-08 GHz)</c> under the Model Validation Report's region-of-convergence chart.
/// </summary>
public sealed class SmithFrequencyRowTests
{
    [Theory]
    [InlineData("freq", "Hz", true)]
    [InlineData("gridPoint", null, false)]
    public void TheFrequencyRow_IsDrawnOnlyForATraceOverFrequency(string axis, string? unit, bool drawn)
    {
        var t = new Trace(new SNP([1e9], 2), MatrixType.S, 0, 0, DependentVarFormat.Db, false) { CubeName = "g" };
        double[] x = [0, 1e9, 2e9];
        t.SetCubeData(x, [new Complex(0.1, 0), new Complex(0.2, 0.1), new Complex(0.3, -0.1)], null, axis, unit,
                      PlotType.Smith, FreqUnit.GHz);
        var plot = new Plot(PlotType.Smith, FreqUnit.GHz);
        plot.Traces.Add(t);
        t.BuildPath(plot.PlotType, plot.FreqUnits);

        Assert.Equal(drawn, t.HasFrequencyX);
        const float w = 300f;
        float h = w + (float)PlotCanvasGeometry.BottomLabelExtraLogical(plot, w);
        string svg = PlotDocumentWriter.BuildSvgString(c => PlotRenderer.Draw(c, (w, h), plot, PlotDetail.Full, RenderTheme.Light),
                                                       new PagePlacement(w, h, 0));
        Assert.Equal(drawn, svg.Contains("freq ("));
    }
}
