using Avalonia;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.Controls;
using CircuitRF.Ui.Tests.RailRf;
using Xunit;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// The network panel's canvas stays fitted as its panel is resized or re-arranged, until the user pans or zooms; the
/// schematic editor's canvas, which leaves <see cref="SchematicCanvas.FitOnResize"/> off, keeps its view.
/// </summary>
public sealed class NetworkPanelFitTests
{
    private static void Resize(SchematicCanvas c, double w, double h)
    {
        c.Measure(new Size(w, h));
        c.Arrange(new Rect(0, 0, w, h));
    }

    /// <summary>Where a canvas fitted at this size from the start puts the parts.</summary>
    private static ((double, double), (double, double)) FittedAt(double w, double h)
    {
        var c = Canvas(fitOnResize: false);
        Resize(c, w, h);
        c.ZoomToFit();
        return Placed(c);
    }

    private static ((double, double), (double, double)) Placed(SchematicCanvas c) => (c.WorldToScreen(0, 0), c.WorldToScreen(1000, 0));

    private static SchematicCanvas Canvas(bool fitOnResize)
    {
        RailBoardViewTests.CursorFactoryShim.Install();
        var m = new SchematicEditModel();
        m.Components.Add(new EditableComponent { InstanceName = "R1", Symbol = SymbolKind.Resistor, X = 0, Y = 0 });
        m.Components.Add(new EditableComponent { InstanceName = "R2", Symbol = SymbolKind.Resistor, X = 1000, Y = 0 });
        var c = new SchematicCanvas { FitOnResize = fitOnResize, Model = m.BuildRenderModel().Model };
        Resize(c, 400, 300);
        c.ZoomToFit();
        return c;
    }

    [Fact]
    public void AResize_RefitsUntilTheUserZooms()
    {
        var c = Canvas(fitOnResize: true);
        Resize(c, 900, 300);
        Assert.Equal(FittedAt(900, 300), Placed(c));

        c.ZoomAtPoint(new Point(10, 10), +1);
        var zoomed = c.WorldToScreen(1000, 0);
        Resize(c, 400, 300);
        Assert.Equal(zoomed, c.WorldToScreen(1000, 0));

        c.ZoomToFit();
        Resize(c, 600, 300);
        Assert.Equal(FittedAt(600, 300), Placed(c));
    }

    [Fact]
    public void WithoutFitOnResize_AResizeKeepsTheView()
    {
        var c = Canvas(fitOnResize: false);
        var before = c.WorldToScreen(1000, 0);
        Resize(c, 900, 300);
        Assert.Equal(before, c.WorldToScreen(1000, 0));
    }
}
