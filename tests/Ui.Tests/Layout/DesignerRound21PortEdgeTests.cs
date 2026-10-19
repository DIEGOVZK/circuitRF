using CircuitRF.Render;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Tests.Layout;

/// <summary>
/// <b>A click on a trace that lies in a ground pour's clearance answers with the TRACE, not the
/// pour</b> (designer round 21). On an imported board the trace's end was covered by a footprint pad,
/// so the conductor lookup merged the overlapping copper — and every same-layer shape whose box
/// overlapped the trace's box went into that union, the pour included, since a pour's box is the
/// whole board. The piece answered was then the first one whose BOX enclosed the click, which the
/// pour's always does: the port snapped to the pour's clearance edge beside the trace, and no end of
/// the trace could be picked. The piece answered must contain the point.
/// </summary>
public class DesignerRound21PortEdgeTests
{
    private static readonly LayerKey Top = new(1, 0);

    private static Technology Tech() => new()
    {
        Layers = [new LayerDef { Key = Top, Name = "Top", Color = new Rgba(1, 2, 3), ZOrder = 0 }],
    };

    /// <summary>A pour with a clearance notch, a trace inside the notch, and a pad over the trace's
    /// right end (the footprint placed on the board's own pad). The pour comes first, as an imported
    /// board's does.</summary>
    private static LayoutView TraceInPourClearance()
    {
        var view = new LayoutView();
        view.Shapes.Add(new PolygonShape
        {
            Layer = Top,
            // The clearance is a NOTCH reaching the board edge, as the trace's is on the reported board.
            Xy    = [0, 0, 20_000, 0, 20_000, 10_000, 0, 10_000, 0, 7_000, 16_000, 7_000, 16_000, 3_000, 0, 3_000],
        });
        view.Shapes.Add(new RectShape { Layer = Top, X1 = 0,      Y1 = 4_600, X2 = 12_000, Y2 = 5_400 });
        view.Shapes.Add(new RectShape { Layer = Top, X1 = 11_500, Y1 = 4_400, X2 = 12_500, Y2 = 5_600 });
        return view;
    }

    [Fact]
    public void AClickNearTheTracesEnd_AnswersTheTrace_AndSnapsToItsEndFace()
    {
        var lookup = LayoutConductorLookup.LookupFor(TraceInPourClearance(), Tech(), "");

        var conductor = lookup(12_300, 5_000, null)!.Value;
        // The trace and its pad, not the board-sized pour.
        Assert.Equal(0,      conductor.Box.MinX);
        Assert.Equal(4_400,  conductor.Box.MinY);
        Assert.Equal(12_500, conductor.Box.MaxX);

        Assert.Equal((12_500L, 5_000L), LayoutPortDirection.NearestBoundaryPoint(conductor, 12_300, 5_000));
    }

    /// <summary>A solder-mask opening over the trace's end is SMALLER than the trace, so the
    /// smallest-shape rule picked it. With a stackup naming the metal, a gesture sees only metal.</summary>
    [Fact]
    public void ASolderMaskOpeningOverTheEnd_DoesNotWinTheClick_WhenTheStackupNamesTheMetal()
    {
        var mask = new LayerKey(5, 0);
        var tech = Tech();
        tech.Layers.Add(new LayerDef { Key = mask, Name = "Mask", Color = new Rgba(4, 5, 6), ZOrder = 1 });
        tech.Stackup.Layers.Add(new StackupLayer { Kind = StackupKind.Conductor, Name = "Top", ThicknessDbu = 35, SigmaSm = 5.8e7, DrawingLayers = [Top] });
        tech.Stackup.Layers.Add(new StackupLayer { Kind = StackupKind.Dielectric, Name = "Core", ThicknessDbu = 400, Epsr = 4.4 });

        var view = TraceInPourClearance();
        view.Shapes.Add(new RectShape { Layer = mask, X1 = 11_800, Y1 = 4_700, X2 = 12_400, Y2 = 5_300 });

        Assert.Equal(Top, LayoutConductorLookup.LookupFor(view, tech, "")(12_300, 5_000, null)!.Value.Shape!.Layer);
        // Without the stackup saying which layers are metal, nothing changes: the smallest shape wins.
        var noStackup = new Technology { Layers = [.. tech.Layers] };
        Assert.Equal(mask, LayoutConductorLookup.LookupFor(view, noStackup, "")(12_300, 5_000, null)!.Value.Shape!.Layer);
    }

    [Fact]
    public void AClickOnThePour_StillAnswersThePour()
    {
        var lookup = LayoutConductorLookup.LookupFor(TraceInPourClearance(), Tech(), "");

        var conductor = lookup(1_000, 1_000, null)!.Value;
        Assert.Equal(20_000, conductor.Box.MaxX);
    }
}
