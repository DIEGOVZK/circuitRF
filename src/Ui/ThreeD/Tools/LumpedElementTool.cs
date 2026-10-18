// brief-sar-3 R-sar3-3 — Lumped element: drawn exactly as a port is (two snapped clicks on the drawing plane), made a
// LUMPED ELEMENT record, not an object. Which way its current runs is not the tool's to decide: the port's own contact rule
// resolves it (C3dLumpedElements), and the view draws the capacitor glyph across that axis.

using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D.Edit;

namespace CircuitRF.Ui.ThreeD.Tools;

public sealed class LumpedElementTool(IC3dDrawHost host, Func<C3dLumpedElement> template) : C3dRectangleTool(host)
{
    public override C3dToolKind Kind => C3dToolKind.LumpedElement;
    public override string Name => "Lumped element";

    public override string Prompt => Step == 0
        ? "Lumped element: click the first corner on the drawing plane (across the gap between the two conductors)."
        : "Lumped element: click the opposite corner (or type the width, Tab, the depth). Esc cancels.";

    /// <summary>The element the last click made — the editor inserts it as one undo entry.</summary>
    public C3dLumpedElement? Made { get; private set; }

    public C3dLumpedElement? TakeMade() { var m = Made; Made = null; return m; }

    public override C3dToolStep Click(in C3dDrawInput input) => Finish(RectangleClick(input)!.Value);

    public override C3dToolStep Typed(long?[] values, in C3dDrawInput input)
        => Step == 1 ? Finish(TypedRectangle(values, input)) : new(false);

    private C3dToolStep Finish(C3dToolStep step)
    {
        if (!step.Advanced || Step != 2) return step;
        Step = 0;
        var e = template();
        e.Plane = Plane.Plane;
        e.Offset = Plane.OffsetDbu;
        e.Rect = new C3dRect
        {
            Min = new C3dPoint2(Math.Min(A.U, B.U), Math.Min(A.V, B.V)),
            Size = new C3dPoint2(Math.Abs(B.U - A.U), Math.Abs(B.V - A.V)),
        };
        Made = e;
        return C3dToolStep.Finish;
    }

    public override void Preview(in C3dDrawInput input, List<DrawSegment> rubber, List<Point3> fixedPoints)
        => PreviewRectangle(input, rubber, fixedPoints);
}
