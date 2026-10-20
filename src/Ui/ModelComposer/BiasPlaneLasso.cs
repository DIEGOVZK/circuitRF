// Exclusion on the bias plane (brief-modelcomposer-14 R-mc14-6): a click or a lasso, through PlotControl's overlay seam
// (IPlotOverlay) so the plot stays the Data Display's own. Armed by the × lamp; disarmed, it answers no hit test and the
// plot behaves exactly as any other. What the points MEAN — an exclusion, or the network fit's bias points — is the view
// model's: this only says which points the gesture took.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.DataDisplay.Controls;
using SkiaSharp;

namespace CircuitRF.Ui.ModelComposer;

/// <summary>One point drawn on the bias plane: its data set, its id, and where it is drawn (x = Vds, y = Vgs).</summary>
public sealed record BiasPlanePoint(string Set, string Point, double X, double Y);

public sealed class BiasPlaneLasso(Func<IReadOnlyList<BiasPlanePoint>> points, Action<IReadOnlyList<(string Set, string Point)>> taken)
    : IPlotOverlay
{
    /// <summary>How near a click must land to take a point, canvas pixels.</summary>
    public const double ClickPixels = 8;

    private static readonly object Handle = new();
    private readonly List<(double X, double Y)> _path = [];
    private TransformSet _tf;
    private bool _dragging;

    /// <summary>True while a click or a drag on the plot takes points.</summary>
    public bool Armed { get; set; }

    public object? HitTest(double canvasX, double canvasY, TransformSet tf)
    {
        if (!Armed) return null;
        _tf = tf;
        return Handle;
    }

    public void DragBegin(object handle)
    {
        _path.Clear();
        _dragging = true;
    }

    public void DragTo(Complex world) => _path.Add((world.Real, world.Imaginary));

    public void DragEnd(bool cancelled)
    {
        _dragging = false;
        if (cancelled || _path.Count == 0) { _path.Clear(); return; }
        var hit = Take(points(), _path, _tf);
        _path.Clear();
        if (hit.Count > 0) taken(hit);
    }

    /// <summary>
    /// The points a gesture took: a path that stayed within <see cref="ClickPixels"/> of its start is a click and takes the
    /// nearest point within that distance; any other path is a lasso, closed back to its start, and takes every point inside.
    /// </summary>
    public static IReadOnlyList<(string Set, string Point)> Take(IReadOnlyList<BiasPlanePoint> candidates,
                                                                 IReadOnlyList<(double X, double Y)> path, TransformSet tf)
    {
        var canvas = path.Select(p => tf.PrimaryToCanvas(p.X, p.Y)).ToList();
        bool click = canvas.All(c => Dist(c, canvas[0]) <= ClickPixels);
        if (click)
        {
            var near = candidates.Select(c => (c, d: Dist(tf.PrimaryToCanvas(c.X, c.Y), canvas[0])))
                                 .Where(x => x.d <= ClickPixels).OrderBy(x => x.d).FirstOrDefault();
            return near.c is null ? [] : [(near.c.Set, near.c.Point)];
        }
        return [.. candidates.Where(c => Inside(path, c.X, c.Y)).Select(c => (c.Set, c.Point)).Distinct()];
    }

    private static double Dist(SKPoint a, SKPoint b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>Even–odd ray casting against the closed polygon.</summary>
    private static bool Inside(IReadOnlyList<(double X, double Y)> poly, double x, double y)
    {
        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            var (xi, yi) = poly[i];
            var (xj, yj) = poly[j];
            if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi) inside = !inside;
        }
        return inside;
    }

    public void Draw(SKCanvas canvas, TransformSet tf, RenderTheme theme)
    {
        if (!_dragging || _path.Count < 2) return;
        using var pen = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f,
            Color = theme.TextColor, PathEffect = SKPathEffect.CreateDash([4, 3], 0),
        };
        using var path = new SKPath();
        path.MoveTo(tf.PrimaryToCanvas(_path[0].X, _path[0].Y));
        foreach (var p in _path.Skip(1)) path.LineTo(tf.PrimaryToCanvas(p.X, p.Y));
        path.Close();
        canvas.DrawPath(path, pen);
    }
}
