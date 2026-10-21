using CircuitRF.Render.DataDisplay;
using RfCore;
using Xunit;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// Model Composer review 3: a report section's plots are re-flowed to fill the room the page has for them. Six DC plots in
/// the display's two columns took half a landscape page; three columns of two fill it.
/// </summary>
public sealed class ModelReportPageLayoutTests
{
    [Fact]
    public void SixPlots_OnALandscapeRegion_FillItInThreeColumns()
    {
        var plots = Enumerable.Range(0, 6).Select(k => new PlacedPlot
        {
            Plot = new Plot(PlotType.Rect, FreqUnit.GHz),
            ViewLeft = 24 + k % 2 * 424, ViewTop = 24 + k / 2 * 314, ViewWidth = 400, ViewHeight = 290, LogicalWidth = 400,
        }).ToList();
        const float width = 720, height = 540;

        var (placed, _) = ModelReportPages.Arrange(plots, width, height);

        Assert.Equal(3, placed.Select(p => p.ViewLeft).Distinct().Count());
        double right = placed.Max(p => p.ViewLeft + p.ViewWidth), bottom = placed.Max(p => p.ViewTop + p.ViewHeight);
        Assert.True(right > 0.95 * width && right <= width + 0.01, $"right edge {right}");
        Assert.True(bottom > 0.95 * height && bottom <= height + 0.01, $"bottom edge {bottom}");
    }
}
