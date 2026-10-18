// brief-sar-6 R-sar6-5 — a SAR plot's peak marker: the averaging cube the peak came from, as a wireframe, with its value at the
// plot's drive. The overlay draws it, projected from the camera each frame, as it draws a temperature's hot spot.

using System.Numerics;
using CircuitRF.Render.Scene3D.Fields;

namespace CircuitRF.Ui.Viewer3D;

public sealed partial class Viewer3DViewModel
{
    /// <summary>Every drawn SAR layer's peak: the cube's eight corners in scene-local metres (null for local SAR, which has no
    /// cube), its centre, and the label.</summary>
    public IReadOnlyList<(Vector3[]? Corners, Vector3 At, string Label)> SarPeaks
        => [.. DrawnLayers.Where(l => l.Quantity is { } q && FieldNames.IsSar(q.Array.Name) && l.Volume is not null && l.Loaded is not null)
                          .Select(l => FieldSarPeak.Of(l.Volume!, l.Quantity!.Array.Name, l.Loaded!.DumpScale, l.Drive.Factor))
                          .OfType<FieldSarPeak>()
                          .Select(p => (Corners(p.Record.CubeLoM, p.Record.CubeHiM), Local(p.Record.CentreM), p.Label))];

    private Vector3 Local(double[] m) => Scene.ToLocal(m[0], m[1], m[2]);

    /// <summary>The cube's corners, corner b at (b&amp;1 ? hi : lo) in x, (b&amp;2) in y, (b&amp;4) in z.</summary>
    private Vector3[]? Corners(double[]? lo, double[]? hi)
        => lo is null || hi is null ? null
         : [.. Enumerable.Range(0, 8).Select(b => Scene.ToLocal((b & 1) != 0 ? hi[0] : lo[0], (b & 2) != 0 ? hi[1] : lo[1], (b & 4) != 0 ? hi[2] : lo[2]))];
}
