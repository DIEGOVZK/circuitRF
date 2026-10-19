// The picture, and what was read drawn over it — brief-img-5-dialog.md R-im5-3, R-im5-4, R-im5-8.
//
// DRAWS; DECIDES NOTHING. Everything drawn over the picture is an ImageOverlayItem the reading produced (ImageOverlay);
// a click asks the overlay which item is under it, and a drag hands a rectangle to the view model as the scope. The
// measure tools hand their clicks to ImageScaleViewModel, which states them to the trace.
//
// Gestures: wheel or pinch zooms about the pointer; right- or middle-drag, or Space held with a left-drag, pans; a
// left-drag sets the scope rectangle; a left-click selects; a double-click, the Fit button or F fits the picture. A
// picture stays fitted as the canvas changes size — the dialog opening, the window resized — until it is zoomed or panned. With a tool armed a
// left-click is the tool's; with Board corners… armed (IM-13) a left-drag on a handle moves it, and the overlay — read
// on the rectified picture — is not drawn over the photo as taken. On a plot picture with Read only here or Ignore here
// armed (PTU-6) a left-drag is the tool's box, handed to the view model on release; an Ignore here box is drawn hatched,
// selected by a click and removed by Delete. On a plot picture the pointer near a traced curve shows a crosshair and a
// readout at the snapped point (brief-pdg-3 R-pdg3-2) without changing what a click does; with the Point tool armed a
// click pins a point there, drawn as a numbered dot, selected by a click and removed by Delete.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Layout.Recognition.Image;
using PixelPoint = CircuitRF.Design.Layout.Recognition.Image.PixelPoint;
using PixelRect = CircuitRF.Design.Layout.Recognition.Image.PixelRect;
using PixelBox = CircuitRF.Design.Schematic.Recognition.PixelBox;

namespace CircuitRF.Ui.Recognition;

public sealed class ImageOverlayCanvas : Control
{
    // The classes' colours are ImageOverlay.Colours — the flattened --overlay picture reads the same constants.
    private static Color Rgb(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    private static readonly Color Amber = Rgb(ImageOverlay.Colours.Amber);
    private static readonly Color Accent = Rgb(ImageOverlay.Colours.Accent);
    private static readonly Color PortColour = Rgb(ImageOverlay.Colours.Port);
    // A read trace not selected (R-ptu3-3): thin, dashed and neutral, so the picture's own curve — whose colour says
    // which trace it is — shows through and beside it.
    private static readonly Color TraceNeutral = Color.FromArgb(0xD0, 0x40, 0x40, 0x40);

    private ImageSourceViewModel? _vm;
    private RasterImage? _rasterShown;
    private WriteableBitmap? _picture;
    private ImageOverlay? _overlayShown;
    private WriteableBitmap? _hatch;
    private (ImageOverlay? Overlay, int Cluster) _flashKey = (null, -1);
    private WriteableBitmap? _flash;
    private readonly Dictionary<string, Geometry> _geometry = new(StringComparer.Ordinal);

    private double _zoom = 1;
    private Vector _pan;
    private bool _autoFit = true;

    private Point? _pressAt;
    private Point _lastPointer;
    private ImageOverlayItem? _dragEnd;
    private bool _panning, _scoping, _spaceHeld;
    private PixelPoint? _hover;
    private CircuitRF.Design.Imaging.Plots.TraceProbeHit? _probe;
    private int _dragCorner = -1;

    public ImageOverlayCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Cross);
        AddHandler(PinchEvent, OnPinch);
    }

    /// <summary>Raised when the view moves — the panel re-places its inline boxes.</summary>
    public event Action? ViewChanged;

    /// <summary>A picture pixel on the control.</summary>
    public Point ToView(double x, double y) => new(x * _zoom + _pan.X, y * _zoom + _pan.Y);

    /// <summary>A control point in picture pixels.</summary>
    public PixelPoint ToPicture(Point p) => new((p.X - _pan.X) / _zoom, (p.Y - _pan.Y) / _zoom);

    // ── the view model ──────────────────────────────────────────────────────────────────────────────

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmChanged;
            _vm.Scale.PropertyChanged -= OnVmChanged;
            _vm.Plots.PropertyChanged -= OnPointsChanged;
            _vm.Plots.ProbePoints.CollectionChanged -= OnPointsChanged;
            _vm.FitRequested -= Fit;
        }
        _vm = DataContext as ImageSourceViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmChanged;
            _vm.Scale.PropertyChanged += OnVmChanged;
            _vm.Plots.PropertyChanged += OnPointsChanged;
            _vm.Plots.ProbePoints.CollectionChanged += OnPointsChanged;
            _vm.FitRequested += Fit;
        }
        _autoFit = true;
        InvalidateArrange();
        InvalidateVisual();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        // A new picture, or a photo swapping between as taken and rectified: fit the one now shown.
        // The raster usually arrives after the canvas was arranged, so arrange again to fit it.
        if (e.PropertyName is nameof(ImageSourceViewModel.Source) or nameof(ImageSourceViewModel.Raster)
            && !ReferenceEquals(_vm?.Raster, _rasterShown))
        {
            _autoFit = true;
            InvalidateArrange();
        }
        if (e.PropertyName == nameof(ImageSourceViewModel.SelectedItem) && _vm?.SelectedItem is { } item) BringIntoView(item);
        if (e.PropertyName is nameof(ImageSourceViewModel.Overlay) or nameof(ImageSourceViewModel.Raster)) _probe = null;
        InvalidateVisual();
    }

    /// <summary>A point pinned, removed from the list or selected there: the dots are redrawn.</summary>
    private void OnPointsChanged(object? sender, EventArgs e)
    {
        if (e is PropertyChangedEventArgs p && p.PropertyName != nameof(PlotSessionViewModel.SelectedProbePoint)) return;
        InvalidateVisual();
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = base.ArrangeOverride(finalSize);
        if (_autoFit) Fit(size);
        return size;
    }

    /// <summary>The whole picture in the control, with a margin — and kept so as the control resizes.</summary>
    public void Fit()
    {
        _autoFit = true;
        Fit(Bounds.Size);
    }

    private void Fit(Size size)
    {
        if (_vm?.Raster is not { } r || size.Width <= 0 || size.Height <= 0) return;
        // The legend floats over the top edge and the prompt over the bottom: the picture is fitted between them.
        const double margin = 16, top = 44, bottom = 44;
        _zoom = Math.Max(1e-3, Math.Min((size.Width - 2 * margin) / r.Width, (size.Height - top - bottom) / r.Height));
        _pan = new Vector((size.Width - r.Width * _zoom) / 2, top + (size.Height - top - bottom - r.Height * _zoom) / 2);
        ViewChanged?.Invoke();
        InvalidateVisual();
    }

    /// <summary>Pans just enough to bring an item on screen.</summary>
    private void BringIntoView(ImageOverlayItem item)
    {
        var b = item.Bounds;
        var tl = ToView(b.Left, b.Top);
        var br = ToView(b.Right, b.Bottom);
        var view = new Rect(Bounds.Size).Deflate(24);
        double dx = 0, dy = 0;
        if (br.X - tl.X > view.Width || tl.X < view.Left || br.X > view.Right) dx = view.Center.X - (tl.X + br.X) / 2;
        if (br.Y - tl.Y > view.Height || tl.Y < view.Top || br.Y > view.Bottom) dy = view.Center.Y - (tl.Y + br.Y) / 2;
        if (dx == 0 && dy == 0) return;
        _autoFit = false;
        _pan += new Vector(dx, dy);
        ViewChanged?.Invoke();
    }

    // ── drawing ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The picture under a failure: faded enough to say nothing is drawn over it, not so far that it cannot be
    /// read — the failure note is small and sits at its foot.</summary>
    private const double FailureOpacity = 0.7;

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(new ImmutableSolidColorBrush(Color.Parse("#00000000")), new Rect(Bounds.Size));
        if (_vm?.Raster is not { } raster) return;
        EnsurePicture(raster);

        var pictureRect = new Rect(0, 0, raster.Width, raster.Height);
        using (context.PushTransform(Matrix.CreateScale(_zoom, _zoom) * Matrix.CreateTranslation(_pan.X, _pan.Y)))
        {
            context.FillRectangle(Brushes.White, pictureRect);
            using (context.PushRenderOptions(new RenderOptions
                   { BitmapInterpolationMode = _zoom >= 2 ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality }))
            using (context.PushOpacity(_vm.HasFailure ? FailureOpacity : 1.0))
                context.DrawImage(_picture!, pictureRect);

            if (!_vm.HasFailure && _vm.Scale.Tool != ImageMeasureTool.Corners) DrawOverlay(context, pictureRect);
        }
        context.DrawRectangle(new Pen(new ImmutableSolidColorBrush(Color.Parse("#808080")), 1),
                              new Rect(ToView(0, 0), ToView(raster.Width, raster.Height)));
        DrawTools(context);
    }

    private void DrawOverlay(DrawingContext context, Rect pictureRect)
    {
        var vm = _vm!;
        var overlay = vm.Overlay;
        if (!ReferenceEquals(overlay, _overlayShown))
        {
            _overlayShown = overlay;
            _geometry.Clear();
            _hatch = BuildHatch(overlay);
        }
        if (_hatch is not null && (vm.ShowIgnored || vm.ShowUnknowns)) context.DrawImage(_hatch, pictureRect);

        if (vm.HoveredLayer is { } hovered && overlay.Clusters is { } clusters)
        {
            if (_flashKey != (overlay, hovered.Cluster))
            {
                _flashKey = (overlay, hovered.Cluster);
                _flash = BuildMask(clusters, [hovered.Cluster], Accent, 150, stripes: false);
            }
            if (_flash is not null) context.DrawImage(_flash, pictureRect);
        }

        double px = 1 / _zoom;
        var litTraces = vm.Plots.Lit(PlotTraceLink.TraceName(vm.SelectedItem));
        foreach (var item in overlay.Items)
        {
            if (item.Class == ImageOverlayClass.Trace && item.Id.StartsWith(ImageOverlay.FitPrefix, StringComparison.Ordinal))
            {
                // A trace's fitted curve (brief-pdg-2 R-pdg2-4): thin and dashed in its own contrasting colour, over the trace.
                context.DrawGeometry(null, new Pen(new ImmutableSolidColorBrush(Rgb(item.Rgb ?? ImageOverlay.FitColour(0))), px,
                                                   new ImmutableDashStyle([4, 3], 0)), GeometryOf(item));
                continue;
            }
            if (item.Class == ImageOverlayClass.Trace)
            {
                DrawTrace(context, item, PlotTraceLink.TraceName(item) is { } traceName && litTraces.Contains(traceName), px);
                continue;
            }
            bool unknownShown = item.Unknown && vm.ShowUnknowns;
            if (!vm.Shows(item.Class) && !unknownShown) continue;
            bool selected = ReferenceEquals(item, vm.SelectedItem);
            var colour = item.Unknown ? Amber : ColourOf(item);
            var pen = new Pen(new ImmutableSolidColorBrush(selected ? Accent : colour), (selected ? 3 : item.Class == ImageOverlayClass.Copper ? 1.25 : 1.75) * px);

            switch (item.Class)
            {
                case ImageOverlayClass.Drill when item.Circle is { } c:
                    context.DrawEllipse(null, pen, new Point(c.X, c.Y), c.R, c.R);
                    break;
                case ImageOverlayClass.Port when item.Circle is { } c:
                    DrawPort(context, c, item.Label, selected ? Accent : PortColour, px);
                    break;
                case ImageOverlayClass.Wire when item.Circle is { } c:
                    // A junction dot filled; a dangling end a ring — the handle that is dragged onto a pin.
                    bool dot = item.Id.StartsWith("dot:", StringComparison.Ordinal);
                    context.DrawEllipse(dot ? new ImmutableSolidColorBrush(colour) : new ImmutableSolidColorBrush(Colors.White, 0.6), pen,
                                        new Point(c.X, c.Y), c.R, c.R);
                    break;
                case ImageOverlayClass.Plot when item.Circle is { } c:
                    // A chart's frame (IM-14): its outer circle.
                    context.DrawEllipse(null, pen, new Point(c.X, c.Y), c.R, c.R);
                    break;
                case ImageOverlayClass.Marker when item.Circle is { } c:
                    context.DrawEllipse(new ImmutableSolidColorBrush(Colors.White, 0.6), pen, new Point(c.X, c.Y), c.R * px * 1.2, c.R * px * 1.2);
                    break;
                case ImageOverlayClass.Annotation:
                    // An annotation stroke (IMF-3): dashed, so what was set aside is told from what was read as line work.
                    context.DrawGeometry(null, new Pen(pen.Brush, pen.Thickness, new ImmutableDashStyle([3, 3], 0)), GeometryOf(item));
                    break;
                case ImageOverlayClass.Crossing when item.Circle is { } c:
                    // Read as connected: a filled disc; not connected: a ring with a gap — what a click flips.
                    var ring = new Pen(new ImmutableSolidColorBrush(selected ? Accent : colour), 2 * px);
                    context.DrawEllipse(item.Rgb == ImageOverlay.Colours.Connected ? new ImmutableSolidColorBrush(colour, 0.35) : null, ring, new Point(c.X, c.Y), c.R, c.R);
                    if (selected && item.Label is { } l) DrawLabel(context, item, l, Accent, px);
                    break;
                default:
                    var g = GeometryOf(item);
                    // A halo under the outline: copper is outlined in its layer's colour, which is very often the
                    // colour the picture drew it in, and an outline the colour of what it outlines cannot be seen.
                    if (!selected && item.Class is ImageOverlayClass.Copper or ImageOverlayClass.Part)
                        context.DrawGeometry(null, new Pen(new ImmutableSolidColorBrush(Colors.White, 0.85), pen.Thickness + 2 * px), g);
                    IBrush? fill = item.Class == ImageOverlayClass.Copper
                        ? new ImmutableSolidColorBrush(colour, 0.18)
                        : selected && item.Class == ImageOverlayClass.Part ? new ImmutableSolidColorBrush(Accent, 0.12) : null;
                    context.DrawGeometry(fill, pen, g);
                    break;
            }
            if (item.Label is { Length: > 0 } label
                && item.Class is ImageOverlayClass.Part or ImageOverlayClass.Line or ImageOverlayClass.Text
                              or ImageOverlayClass.Plot or ImageOverlayClass.Marker or ImageOverlayClass.Annotation)
                DrawLabel(context, item, label, selected ? Accent : colour, px);
        }

        if (vm.IsPlotPicture)
            foreach (var b in vm.Plots.IgnoreBoxes)
                if (b.Picture == vm.Plots.ShownPicture) DrawIgnoreBox(context, b.Box, Equals(b, vm.Plots.SelectedIgnoreBox), px);

        if (vm.ScopeRect is { } s)
            context.DrawRectangle(new Pen(new ImmutableSolidColorBrush(Accent), 1.5 * px, new ImmutableDashStyle([4, 3], 0)),
                                  new Rect(s.Left, s.Top, s.Right - s.Left, s.Bottom - s.Top));
    }

    /// <summary>
    /// A read trace, or one of its bridged gaps (R-ptu3-3). Unselected: 1 px, dashed, neutral — never amber and never
    /// the trace's own colour, so the curve under it can be seen and its colour judged; whether it is assigned is the
    /// table's to say. Selected: the accent at 2 px, with its label, so the user sees which pixels the row means.
    /// </summary>
    private void DrawTrace(DrawingContext context, ImageOverlayItem item, bool selected, double px)
    {
        bool gap = item.Id.StartsWith(ImageOverlay.GapPrefix, StringComparison.Ordinal);
        var pen = selected
            ? new Pen(new ImmutableSolidColorBrush(Accent), 2 * px, gap ? new ImmutableDashStyle([2, 2], 0) : null)
            : new Pen(new ImmutableSolidColorBrush(TraceNeutral), px, new ImmutableDashStyle(gap ? [1, 4] : [3, 3], 0));
        context.DrawGeometry(null, pen, GeometryOf(item));
        if (selected && !gap && item.Label is { Length: > 0 } label)
            DrawLabel(context, item, string.Join(" · ", label.Split(' ', StringSplitOptions.RemoveEmptyEntries)), Accent, px);
    }

    /// <summary>An Ignore here box (R-ptu6-3): hatched in the Ignored class's grey, the accent when selected.</summary>
    private static void DrawIgnoreBox(DrawingContext context, PixelBox box, bool selected, double px)
    {
        var rect = new Rect(box.Left, box.Top, box.Width, box.Height);
        var grey = Rgb(ImageOverlay.Colours.Ignored);
        context.FillRectangle(new ImmutableSolidColorBrush(Colors.White, 0.35), rect);
        using (context.PushClip(rect))
        {
            var hatch = new Pen(new ImmutableSolidColorBrush(grey, 0.67), 1.5 * px);
            double step = 8 * px;
            for (double d = -rect.Height; d < rect.Width; d += step)
                context.DrawLine(hatch, new Point(rect.Left + d, rect.Bottom), new Point(rect.Left + d + rect.Height, rect.Top));
        }
        context.DrawRectangle(new Pen(new ImmutableSolidColorBrush(selected ? Accent : grey), (selected ? 2.5 : 1.25) * px), rect);
    }

    private static Color ColourOf(ImageOverlayItem item) => Rgb(ImageOverlay.ColourOf(item with { Unknown = false }));

    private Geometry GeometryOf(ImageOverlayItem item)
    {
        if (_geometry.TryGetValue(item.Id, out var g)) return g;
        var sg = new StreamGeometry();
        using (var ctx = sg.Open())
        {
            if (item.Closed) ctx.SetFillRule(FillRule.EvenOdd);
            foreach (var path in item.Paths)
            {
                if (path.Count == 0) continue;
                ctx.BeginFigure(new Point(path[0].X, path[0].Y), item.Closed);
                for (int i = 1; i < path.Count; i++) ctx.LineTo(new Point(path[i].X, path[i].Y));
                if (path.Count == 1) ctx.LineTo(new Point(path[0].X + 0.01, path[0].Y));
                ctx.EndFigure(item.Closed);
            }
        }
        _geometry[item.Id] = sg;
        return sg;
    }

    private static void DrawPort(DrawingContext context, (double X, double Y, double R) c, string? label, Color colour, double px)
    {
        // The schematic's port glyph: a circle with the port's number beside it.
        double r = 7 * px;
        var pen = new Pen(new ImmutableSolidColorBrush(colour), 1.75 * px);
        context.DrawEllipse(new ImmutableSolidColorBrush(Colors.White, 0.85), pen, new Point(c.X, c.Y), r, r);
        context.DrawLine(pen, new Point(c.X - r * 0.6, c.Y), new Point(c.X + r * 0.6, c.Y));
        if (label is null) return;
        var text = Text(label, colour, px);
        context.DrawText(text, new Point(c.X + r + 2 * px, c.Y - text.Height / 2));
    }

    private static void DrawLabel(DrawingContext context, ImageOverlayItem item, string label, Color colour, double px)
    {
        var text = Text(label, colour, px);
        Point at = item.Class == ImageOverlayClass.Line && item.Paths.Count > 0 && item.Paths[0].Count > 0
            ? Midpoint(item.Paths[0])
            : new Point(item.Bounds.Left, item.Bounds.Top - text.Height - 1 * px);
        if (item.Class == ImageOverlayClass.Line) at = new Point(at.X - text.Width / 2, at.Y - text.Height - 3 * px);
        context.FillRectangle(new ImmutableSolidColorBrush(Colors.White, 0.8),
                              new Rect(at.X - 2 * px, at.Y, text.Width + 4 * px, text.Height));
        context.DrawText(text, at);
    }

    private static Point Midpoint(IReadOnlyList<(double X, double Y)> path)
    {
        if (path.Count == 1) return new Point(path[0].X, path[0].Y);
        int i = path.Count / 2;
        return new Point((path[i - 1].X + path[i].X) / 2, (path[i - 1].Y + path[i].Y) / 2);
    }

    private static FormattedText Text(string s, Color colour, double px) =>
        new(s, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 11 * px,
            new ImmutableSolidColorBrush(colour));

    private void DrawTools(DrawingContext context)
    {
        var scale = _vm!.Scale;
        var pen = new Pen(new ImmutableSolidColorBrush(Accent), 2);
        void Mark(PixelPoint p)
        {
            var v = ToView(p.X, p.Y);
            context.DrawEllipse(new ImmutableSolidColorBrush(Colors.White), pen, v, 4, 4);
        }
        if (scale.Tool == ImageMeasureTool.TwoPoints)
        {
            if (scale.PointA is { } a)
            {
                var end = scale.PointB ?? _hover;
                if (end is { } b) context.DrawLine(new Pen(pen.Brush, 1.5, new ImmutableDashStyle([5, 3], 0)), ToView(a.X, a.Y), ToView(b.X, b.Y));
                Mark(a);
            }
            if (scale.PointB is { } pb) Mark(pb);
        }
        else if (scale.Tool == ImageMeasureTool.Impedance && scale.PointA is { } p) Mark(p);
        else if (scale.Tool == ImageMeasureTool.Corners && scale.CornerPoints is { Count: 4 } corners)
        {
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(ToView(corners[0].X, corners[0].Y), true);
                for (int k = 1; k < 4; k++) g.LineTo(ToView(corners[k].X, corners[k].Y));
                g.EndFigure(true);
            }
            context.DrawGeometry(new ImmutableSolidColorBrush(Accent, 0.08), new Pen(pen.Brush, 1.5), geometry);
            for (int k = 0; k < 4; k++)
            {
                var v = ToView(corners[k].X, corners[k].Y);
                context.DrawEllipse(new ImmutableSolidColorBrush(Colors.White), pen, v, 6, 6);
                context.DrawText(Text((k + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), Accent, 1), new Point(v.X + 8, v.Y - 16));
            }
        }

        if (_scoping && _pressAt is { } from)
            context.DrawRectangle(new ImmutableSolidColorBrush(Accent, 0.08), new Pen(pen.Brush, 1, new ImmutableDashStyle([4, 3], 0)),
                                  new Rect(from, _lastPointer).Normalize());
        if (_dragEnd is { Circle: { } c } && _pressAt is not null)
            context.DrawLine(new Pen(pen.Brush, 1.5, new ImmutableDashStyle([5, 3], 0)), ToView(c.X, c.Y), _lastPointer);

        if (_vm.IsPlotPicture && !_vm.HasFailure) DrawProbes(context);
    }

    /// <summary>The pinned points as numbered dots, and the hover's crosshair and readout (R-pdg3-2), in screen pixels so
    /// they keep their size at any zoom.</summary>
    private void DrawProbes(DrawingContext context)
    {
        var plots = _vm!.Plots;
        foreach (var p in plots.ProbePoints)
        {
            if (p.Picture != plots.ShownPicture || p.Hit is null) continue;
            bool selected = ReferenceEquals(p, plots.SelectedProbePoint);
            var at = ToView(p.Px, p.Py);
            var colour = selected ? Accent : PortColour;
            context.DrawEllipse(new ImmutableSolidColorBrush(colour), new Pen(Brushes.White, 1.5), at, 4.5, 4.5);
            Readout(context, p.Name, new Point(at.X + 6, at.Y - 18), colour);
        }
        if (_probe is not { } h || _panning || _scoping) return;
        var v = ToView(h.Px, h.Py);
        var cross = new Pen(new ImmutableSolidColorBrush(Accent), 1.25);
        context.DrawLine(cross, new Point(v.X - 7, v.Y), new Point(v.X - 2, v.Y));
        context.DrawLine(cross, new Point(v.X + 2, v.Y), new Point(v.X + 7, v.Y));
        context.DrawLine(cross, new Point(v.X, v.Y - 7), new Point(v.X, v.Y - 2));
        context.DrawLine(cross, new Point(v.X, v.Y + 2), new Point(v.X, v.Y + 7));
        Readout(context, plots.ProbeText(h), new Point(v.X + 10, v.Y + 8), Accent);
    }

    /// <summary>A line of text on a white backing at <paramref name="at"/>, kept inside the canvas.</summary>
    private void Readout(DrawingContext context, string text, Point at, Color colour)
    {
        var t = Text(text, colour, 1);
        double x = Math.Min(at.X, Bounds.Width - t.Width - 6), y = Math.Min(at.Y, Bounds.Height - t.Height - 4);
        context.FillRectangle(new ImmutableSolidColorBrush(Colors.White, 0.88), new Rect(x - 3, y - 1, t.Width + 6, t.Height + 2), 2);
        context.DrawText(t, new Point(x, y));
    }

    // ── bitmaps ─────────────────────────────────────────────────────────────────────────────────────

    private void EnsurePicture(RasterImage raster)
    {
        if (ReferenceEquals(raster, _rasterShown) && _picture is not null) return;
        _rasterShown = raster;
        _picture?.Dispose();
        _picture = FromRgba(raster.Width, raster.Height, raster.Rgba);
    }

    private static WriteableBitmap FromRgba(int w, int h, byte[] rgba)
    {
        var bmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul);
        using var fb = bmp.Lock();
        int row = w * 4;
        for (int y = 0; y < h; y++) Marshal.Copy(rgba, y * row, fb.Address + y * fb.RowBytes, row);
        return bmp;
    }

    /// <summary>Ignored colours hatched grey, colours mapped to nothing hatched amber.</summary>
    private static WriteableBitmap? BuildHatch(ImageOverlay overlay)
    {
        if (overlay.Clusters is not { } clusters || (overlay.IgnoredClusters.Count == 0 && overlay.UnmappedClusters.Count == 0)) return null;
        int w = clusters.Width, h = clusters.Height;
        var px = new byte[w * h * 4];
        var ignored = overlay.IgnoredClusters.ToHashSet();
        var unmapped = overlay.UnmappedClusters.ToHashSet();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (((x + y) & 7) >= 2) continue;
                int l = clusters.Label(x, y);
                Color? c = unmapped.Contains(l) ? Amber : ignored.Contains(l) ? Rgb(ImageOverlay.Colours.Ignored) : null;
                if (c is not { } col) continue;
                int i = (y * w + x) * 4;
                px[i] = col.R; px[i + 1] = col.G; px[i + 2] = col.B; px[i + 3] = 170;
            }
        return FromRgba(w, h, px);
    }

    private static WriteableBitmap BuildMask(ColourClusterSet clusters, IReadOnlyCollection<int> which, Color colour, byte alpha, bool stripes)
    {
        int w = clusters.Width, h = clusters.Height;
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (stripes && ((x + y) & 7) >= 2) continue;
                if (!which.Contains(clusters.Label(x, y))) continue;
                int i = (y * w + x) * 4;
                px[i] = colour.R; px[i + 1] = colour.G; px[i + 2] = colour.B; px[i + 3] = alpha;
            }
        return FromRgba(w, h, px);
    }

    // ── input ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The corner handle within reach of a control point, or −1.</summary>
    private int CornerAt(Point at)
    {
        if (_vm?.Scale is not { Tool: ImageMeasureTool.Corners, CornerPoints: { Count: 4 } corners }) return -1;
        int best = -1;
        double reach = 10 * 10;
        for (int k = 0; k < 4; k++)
        {
            var v = ToView(corners[k].X, corners[k].Y);
            double d = (v.X - at.X) * (v.X - at.X) + (v.Y - at.Y) * (v.Y - at.Y);
            if (d <= reach) { reach = d; best = k; }
        }
        return best;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        ZoomAbout(e.GetPosition(this), Math.Pow(1.15, e.Delta.Y));
        e.Handled = true;
    }

    private void OnPinch(object? sender, PinchEventArgs e)
    {
        ZoomAbout(e.ScaleOrigin, e.Scale > 0 ? Math.Pow(e.Scale, 0.2) : 1);
        e.Handled = true;
    }

    private void ZoomAbout(Point at, double factor)
    {
        double z = Math.Clamp(_zoom * factor, 0.01, 64);
        factor = z / _zoom;
        _autoFit = false;
        _pan = new Vector(at.X - (at.X - _pan.X) * factor, at.Y - (at.Y - _pan.Y) * factor);
        _zoom = z;
        ViewChanged?.Invoke();
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Space) { _spaceHeld = true; Cursor = new Cursor(StandardCursorType.Hand); e.Handled = true; }
        else if (e.Key == Key.Escape && _vm is not null)
        {
            _vm.CancelTools();
            _scoping = false;
            InvalidateVisual();
            e.Handled = true;
        }
        else if (e.Key is Key.Delete or Key.Back && _vm is { IsPlotPicture: true } && _vm.Plots.SelectedProbePoint is { } point)
        {
            _vm.Plots.RemoveProbePoint(point);
            InvalidateVisual();
            e.Handled = true;
        }
        else if (e.Key is Key.Delete or Key.Back && _vm?.Plots.SelectedIgnoreBox is { } box)
        {
            _vm.Plots.RemoveIgnoreBox(box);
            InvalidateVisual();
            e.Handled = true;
        }
        else if (e.Key is Key.Delete or Key.Back && _vm is { HasScope: true } scoped)
        {
            // The rectangle dragged on the picture is deleted like any other drawn box: the whole picture is read again.
            scoped.WholePictureCommand.Execute(null);
            InvalidateVisual();
            e.Handled = true;
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = null;
        if (_probe is null) return;
        _probe = null;
        InvalidateVisual();
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Key == Key.Space) { _spaceHeld = false; Cursor = new Cursor(StandardCursorType.Cross); }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var point = e.GetCurrentPoint(this);
        _lastPointer = point.Position;
        if (e.ClickCount == 2 && point.Properties.IsLeftButtonPressed && _vm?.Scale.Tool == ImageMeasureTool.None)
        {
            Fit();
            e.Handled = true;
            return;
        }
        _panning = point.Properties.IsRightButtonPressed || point.Properties.IsMiddleButtonPressed
                   || (point.Properties.IsLeftButtonPressed && _spaceHeld);
        _pressAt = point.Properties.IsLeftButtonPressed && !_panning ? point.Position : null;
        _scoping = false;
        _dragCorner = _pressAt is { } press ? CornerAt(press) : -1;
        // A dangling wire end pressed on a schematic picture is dragged, not scoped: dropped on a pin it joins it (R-im10-8).
        _dragEnd = _pressAt is { } at && _vm is { IsSchematicPicture: true } vm0
            ? vm0.Overlay.HitTest(ToPicture(at).X, ToPicture(at).Y, 4 / _zoom, vm0.Shows) is { } hit
              && hit.Id.StartsWith(ImageOverlay.EndPrefix, StringComparison.Ordinal) ? hit : null
            : null;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        _hover = ToPicture(p);
        _probe = _vm is { IsPlotPicture: true, HasFailure: false } && !_panning && _dragCorner < 0
            ? _vm.Plots.ProbeAt(_hover.Value.X, _hover.Value.Y, PlotSessionViewModel.SnapScreenPx / _zoom)
            : null;
        if (_panning)
        {
            _autoFit = false;
            _pan += p - _lastPointer;
            ViewChanged?.Invoke();
        }
        else if (_dragCorner >= 0 && _vm is not null)
        {
            _vm.Scale.MoveCorner(_dragCorner, ToPicture(p));
            ViewChanged?.Invoke();
        }
        else if (_pressAt is { } from && _dragEnd is null
                 && _vm is { BoxTool: PlotBoxTool.ReadOnlyHere or PlotBoxTool.IgnoreHere }
                          or { Scale.Tool: ImageMeasureTool.None, CanvasPick: false, IsLayoutPicture: true }
                 && (Math.Abs(p.X - from.X) > 4 || Math.Abs(p.Y - from.Y) > 4))
            _scoping = true;
        _lastPointer = p;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var p = e.GetPosition(this);
        e.Pointer.Capture(null);
        if (_panning) { _panning = false; return; }
        if (_pressAt is not { } from || _vm is not { } vm) return;
        _pressAt = null;
        if (_dragCorner >= 0) { _dragCorner = -1; return; }
        if (_dragEnd is { } end)
        {
            _dragEnd = null;
            if (Math.Abs(p.X - from.X) > 4 || Math.Abs(p.Y - from.Y) > 4) { vm.JoinEnd(end, ToPicture(p)); InvalidateVisual(); return; }
        }

        if (_scoping)
        {
            _scoping = false;
            var a = ToPicture(from);
            var b = ToPicture(p);
            if (vm.BoxTool != PlotBoxTool.None)
            {
                // Read only here crops and Ignore here excludes (R-ptu6-2, R-ptu6-3); either way the tool is done.
                vm.CommitBox(new PixelRect(a.X, a.Y, b.X, b.Y));
                InvalidateVisual();
                return;
            }
            if (vm.Raster is { } r)
                vm.ScopeRect = new PixelRect(Math.Clamp(Math.Min(a.X, b.X), 0, r.Width), Math.Clamp(Math.Min(a.Y, b.Y), 0, r.Height),
                                             Math.Clamp(Math.Max(a.X, b.X), 0, r.Width), Math.Clamp(Math.Max(a.Y, b.Y), 0, r.Height));
            InvalidateVisual();
            return;
        }

        var at = ToPicture(p);
        // A pinned point clicked is selected; with the Point tool armed any other click pins one (R-pdg3-2).
        if (vm.IsPlotPicture && vm.Plots.ProbePointAt(at.X, at.Y, 6 / _zoom) is { } pin)
        {
            vm.Plots.SelectedProbePoint = pin;
            vm.Plots.SelectedIgnoreBox = null;
            InvalidateVisual();
            return;
        }
        if (vm.BoxTool == PlotBoxTool.Point)
        {
            vm.PinAt(at, PlotSessionViewModel.SnapScreenPx / _zoom);
            InvalidateVisual();
            return;
        }
        if (vm.BoxTool != PlotBoxTool.None) return;   // a click is not a box: the tool stays armed
        if (vm.IsPlotPicture) vm.Plots.SelectedProbePoint = null;
        if (vm.CanvasPick) { vm.PickAt(at); return; }
        if (vm.IsPlotPicture)
        {
            vm.Plots.SelectedIgnoreBox = vm.Plots.IgnoreBoxAt(at.X, at.Y);
            InvalidateVisual();
            if (vm.Plots.SelectedIgnoreBox is not null) { vm.SelectedItem = null; return; }
        }
        if (vm.Scale.Click(at)) { ViewChanged?.Invoke(); InvalidateVisual(); return; }
        var picked = vm.Overlay.HitTest(at.X, at.Y, 4 / _zoom, vm.Shows);
        if (picked is { Class: ImageOverlayClass.Crossing }) { vm.ToggleCrossing(picked); return; }
        vm.SelectedItem = picked;
    }
}
