// The schematic picture's scale handles — the layout editor's bbox handle set (LayoutScaleHandles)
// in the schematic's double-precision world units. Same 8 handles, same index convention, so a
// picture resizes the same way in both editors: a corner scales uniformly, a side stretches one axis,
// and the anchor is the opposite handle (or the centre with Alt).

using System;
using System.Collections.Generic;

namespace CircuitRF.Render;

/// <summary>Index convention is <see cref="LayoutScaleHandle"/>'s: Corner 0=(MinX,MinY), 1=(MaxX,MinY),
/// 2=(MaxX,MaxY), 3=(MinX,MaxY); Side 0=MinY midpoint, 1=MaxX midpoint, 2=MaxY midpoint, 3=MinX midpoint.
/// The opposite handle is always <c>(Index + 2) % 4</c> of the same kind.</summary>
public readonly record struct SchematicScaleHandle(ScaleHandleKind Kind, double X, double Y, int Index);

public static class SchematicScaleHandles
{
    /// <summary>The 8 handles of a rect given as top-left + size (the SchematicBitmap convention).</summary>
    public static IReadOnlyList<SchematicScaleHandle> Build(double x, double y, double w, double h)
    {
        double x1 = x, y1 = y, x2 = x + w, y2 = y + h, mx = x + w / 2, my = y + h / 2;
        return
        [
            new SchematicScaleHandle(ScaleHandleKind.Corner, x1, y1, 0),
            new SchematicScaleHandle(ScaleHandleKind.Corner, x2, y1, 1),
            new SchematicScaleHandle(ScaleHandleKind.Corner, x2, y2, 2),
            new SchematicScaleHandle(ScaleHandleKind.Corner, x1, y2, 3),
            new SchematicScaleHandle(ScaleHandleKind.Side,   mx, y1, 0),
            new SchematicScaleHandle(ScaleHandleKind.Side,   x2, my, 1),
            new SchematicScaleHandle(ScaleHandleKind.Side,   mx, y2, 2),
            new SchematicScaleHandle(ScaleHandleKind.Side,   x1, my, 3),
        ];
    }

    public static SchematicScaleHandle Opposite(IReadOnlyList<SchematicScaleHandle> handles, SchematicScaleHandle h)
    {
        int oppositeIndex = (h.Index + 2) % 4;
        foreach (var candidate in handles)
            if (candidate.Kind == h.Kind && candidate.Index == oppositeIndex)
                return candidate;
        return h;
    }

    /// <summary>The nearest handle within <paramref name="tol"/> world units, or null.</summary>
    public static SchematicScaleHandle? HitTest(IReadOnlyList<SchematicScaleHandle> handles, double x, double y, double tol)
    {
        SchematicScaleHandle? best = null;
        double bestDist = double.MaxValue;
        foreach (var h in handles)
        {
            double dist = Math.Sqrt((h.X - x) * (h.X - x) + (h.Y - y) * (h.Y - y));
            if (dist > tol || dist >= bestDist) continue;
            best = h; bestDist = dist;
        }
        return best;
    }

    /// <summary>The live scale factors for dragging <paramref name="handle"/> to (<paramref name="px"/>,
    /// <paramref name="py"/>) about the anchor — <c>LayoutEditorViewModel.ComputeLiveScaleFactors</c>'s
    /// rule: a corner is uniform by distance ratio, a side scales its own axis only. Floored at 1% so a
    /// drag through the anchor never inverts the picture.</summary>
    public static (double Fx, double Fy) Factors(SchematicScaleHandle handle, double anchorX, double anchorY,
                                                 double px, double py)
    {
        const double minFactor = 0.01;
        double dhx = handle.X - anchorX, dhy = handle.Y - anchorY;
        double dcx = px - anchorX, dcy = py - anchorY;

        if (handle.Kind == ScaleHandleKind.Corner)
        {
            double orig = Math.Sqrt(dhx * dhx + dhy * dhy);
            double cur  = Math.Sqrt(dcx * dcx + dcy * dcy);
            double f = orig > 1e-9 ? Math.Max(cur / orig, minFactor) : 1.0;
            return (f, f);
        }

        if (handle.Index is 1 or 3)
            return (Math.Abs(dhx) > 1e-9 ? Math.Max(dcx / dhx, minFactor) : 1.0, 1.0);
        return (1.0, Math.Abs(dhy) > 1e-9 ? Math.Max(dcy / dhy, minFactor) : 1.0);
    }
}
