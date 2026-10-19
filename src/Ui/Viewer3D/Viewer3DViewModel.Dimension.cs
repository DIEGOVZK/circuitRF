// brief-sar-12 R-sar12-2 — a dimension drawn in the view: a device's separation from the phantom face it was placed against.
//
// Like a measurement it is NOT document state: two world points and a label, projected by the overlay each frame from the camera
// alone. The editor measures it again from the geometry whenever the document changes, so a device moved by hand afterwards
// shows its new separation, and nothing in the file remembers the placement.

namespace CircuitRF.Ui.Viewer3D;

/// <summary>A dimension line between two world points (metres), with its label.</summary>
public sealed record Viewer3DDimension(Viewer3DMeasurePoint A, Viewer3DMeasurePoint B, string Label);

public sealed partial class Viewer3DViewModel
{
    /// <summary>The dimension the overlay draws, or null.</summary>
    public Viewer3DDimension? Dimension { get; private set; }

    public void SetDimension(Viewer3DDimension? dimension)
    {
        if (Dimension == dimension) return;
        Dimension = dimension;
        FrameRequested?.Invoke();
    }
}
