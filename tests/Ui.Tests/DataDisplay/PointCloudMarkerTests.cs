using System.Numerics;
using CircuitRF.Render.DataDisplay;
using RfCore;
using Xunit;

namespace CircuitRF.Ui.Tests.DataDisplay;

/// <summary>
/// Model Composer review 3: a marker on a point cloud (<see cref="Trace.IsPointCloud"/> — a bias plane's (Vds, Vgs)) is the
/// point nearest the pointer in two dimensions, and reads back as that point. By the X-only rule a curve uses, a click
/// beside the top point of a column landed on the column's first point, whatever its Y.
/// </summary>
public sealed class PointCloudMarkerTests
{
    [Theory]
    [InlineData(true, 3)]
    [InlineData(false, 0)]
    public void AMarker_IsTheNearestPoint_InTwoDimensions_OnAPointCloud(bool cloud, int expected)
    {
        // Two columns of points at Vds 0 and 5, Vgs −3 … 0, in sweep order.
        double[] x = [0, 0, 0, 0, 5, 5, 5, 5];
        double[] y = [-3, -2, -1, 0, -3, -2, -1, 0];
        var t = new Trace(new SNP([1e9], 2), MatrixType.S, 0, 0, DependentVarFormat.Real, false) { CubeName = "extrinsic", IsPointCloud = cloud };
        t.SetCubeData(x, null, y, "Vds", "V", PlotType.Rect, FreqUnit.Hz);
        var plot = new Plot(PlotType.Rect, FreqUnit.Hz);
        plot.Traces.Add(t);
        plot.SetAxesViewport();
        plot.Autoscale(force: true);
        var tf = PlotRenderer.BuildTransforms(plot, (400, 300));

        // Beside the top point of the first column.
        var snap = t.SnapToCubeMarker(new Vector2(0.1f, -0.05f), tf);
        Assert.NotNull(snap);
        var marker = new Marker(t, 0, false, false, 1)
        {
            MarkerKind = MarkerKind.Polyline,
            PositionStatic = t.MarkersResolve2D ? snap.Value.Pos : new Vector2(snap.Value.CubeX, snap.Value.CurveIndex),
        };
        Assert.Equal(t.Points[expected], t.GetMarkerDataLocation(marker));
    }
}
