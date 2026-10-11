// Where the plots on a picture are — brief-img-14-sparameter-plots-to-touchstone.md R-im14-1, R-im14-2.
//
// THE INK is every pixel unlike the background by more than InkLevels on some channel — the background being the
// commonest colour on the picture's border, so a dark-background plot reads the same way as a white one. A light grid
// is ink; the faintest minor grid of some tools is not, and is not needed.
//
// LINES are runs of ink at least MinLine long, on one row (or column), joined across neighbouring rows into one line
// with a thickness. A plot's axes are two of them meeting at a corner: a vertical line whose foot is a horizontal
// line's left end (R-im14-1). Grid lines are the lines inside that box spanning most of it; ticks are short runs just
// outside each axis. A datasheet page with several plots gives several such corners (R-im14-2).
//
// CIRCLES are tested, not searched for: a Smith or polar chart draws its real axis as a diameter, so every long
// horizontal line is offered as one, and the circle it would be the diameter of is sampled at 720 angles. A circle
// found is refitted to the ink near it (least squares, Fit.Circle) and classified by what else is drawn inside: circles
// tangent at its right-hand point (an impedance Smith chart's constant-r family), at its left-hand point (an
// admittance chart), or concentric (a polar chart).

using CircuitRF.Design.Schematic.Recognition;
using Clipper2Lib;

namespace CircuitRF.Design.Imaging.Plots;

/// <summary>One straight line of a plot picture: an axis, a grid line or a chart's diameter.</summary>
/// <param name="Position">Its centre across the line, pixels (y of a horizontal line, x of a vertical one).</param>
/// <param name="From">Where it starts along its length, pixels.</param>
/// <param name="To">Where it ends, pixels (inclusive).</param>
/// <param name="Thickness">How many rows (or columns) it covers.</param>
/// <param name="Rgb">Its colour, 0xRRGGBB, at its middle.</param>
public sealed record PlotLine(bool Horizontal, double Position, int From, int To, int Thickness, int Rgb)
{
    public int Length => To - From + 1;
}

/// <summary>One plot found on a picture (R-im14-2).</summary>
public sealed record PlotFrame
{
    /// <summary>Its number on its picture, 0-based, in reading order (top to bottom, then left to right).</summary>
    public int Index { get; init; }

    public PlotType Type { get; init; }

    /// <summary>Its area: a rectangular plot's axes box; a chart's bounding square.</summary>
    public PixelBox Box { get; init; }

    /// <summary>A rectangular plot's axis lines, pixels: the y axis at Left, the x axis at Bottom.</summary>
    public double Left { get; init; }
    public double Top { get; init; }
    public double Right { get; init; }
    public double Bottom { get; init; }

    /// <summary>Where grid lines and ticks cross the x axis (x pixels) and the y axis (y pixels), ascending.</summary>
    public IReadOnlyList<double> XTicks { get; init; } = [];
    public IReadOnlyList<double> YTicks { get; init; } = [];

    /// <summary>The frame's own lines — axes and grid lines — so trace pixels lying on them can be told apart.</summary>
    public IReadOnlyList<PlotLine> Lines { get; init; } = [];

    /// <summary>A chart's outer circle: centre and radius, pixels.</summary>
    public double Cx { get; init; }
    public double Cy { get; init; }
    public double Radius { get; init; }

    /// <summary>A Smith chart drawn as an admittance chart (its constant-g circles tangent on the left).</summary>
    public bool Admittance { get; init; }

    /// <summary>Both families drawn (a Z-Y chart): read as Z (R-im14-1).</summary>
    public bool Overlaid { get; init; }

    /// <summary>A Smith chart's constant-r (or g) circles found where they must lie — r = 1 through the centre first.</summary>
    public IReadOnlyList<double> CirclesChecked { get; init; } = [];

    /// <summary>A polar chart's concentric rings, as shares of the outer radius.</summary>
    public IReadOnlyList<double> Rings { get; init; } = [];

    /// <summary>The outer circle's fit residual, pixels (RMS).</summary>
    public double Residual { get; init; }

    /// <summary>The frame lines' typical thickness, pixels.</summary>
    public double Pen { get; init; } = 1;

    public bool IsChart => Type is PlotType.Smith or PlotType.Polar;

    /// <summary>Whether a pixel lies within the plotting area — inside the axes box, or inside the chart's circle.</summary>
    public bool Inside(double x, double y, double inset = 0) => IsChart
        ? (x - Cx) * (x - Cx) + (y - Cy) * (y - Cy) <= (Radius - inset) * (Radius - inset)
        : x >= Left + inset && x <= Right - inset && y >= Top + inset && y <= Bottom - inset;
}

/// <summary>The ink of a plot picture: which pixels differ from the background.</summary>
public sealed class PlotInk
{
    /// <summary>A pixel is ink when some channel differs from the background's by more than this many sRGB levels.
    /// A light grid (#e0e0e0 on white) is 31 away; JPEG ringing at quality 60 stays within about 20.</summary>
    public const int InkLevels = 26;

    private PlotInk(RasterImage raster, int background, byte[] mask)
    {
        Raster = raster;
        Background = background;
        Mask = mask;
    }

    public RasterImage Raster { get; }
    public int Width => Raster.Width;
    public int Height => Raster.Height;

    /// <summary>The background colour, 0xRRGGBB.</summary>
    public int Background { get; }

    /// <summary>1 where a pixel is ink.</summary>
    public byte[] Mask { get; }

    public bool At(int x, int y) => (uint)x < (uint)Width && (uint)y < (uint)Height && Mask[y * Width + x] != 0;

    /// <summary>The background is dark: a dark-background plot, whose ink is light.</summary>
    public bool DarkBackground => Luma(Background) < 96;

    public static double Luma(int rgb) => 0.299 * ((rgb >> 16) & 0xFF) + 0.587 * ((rgb >> 8) & 0xFF) + 0.114 * (rgb & 0xFF);

    /// <summary>The largest channel difference between two colours.</summary>
    public static int Distance(int a, int b) =>
        Math.Max(Math.Abs(((a >> 16) & 0xFF) - ((b >> 16) & 0xFF)),
                 Math.Max(Math.Abs(((a >> 8) & 0xFF) - ((b >> 8) & 0xFF)), Math.Abs((a & 0xFF) - (b & 0xFF))));

    /// <summary>Whether <paramref name="p"/> is colour <paramref name="a"/>, or a blend of it with <paramref name="b"/> — an
    /// anti-aliased edge of a line of colour a drawn on b.</summary>
    public static bool Between(int p, int a, int b)
    {
        if (Distance(p, a) < 48) return true;
        int ab = Distance(a, b);
        return ab > 0 && Distance(p, a) + Distance(p, b) <= ab + 36;
    }

    public static PlotInk Of(RasterImage raster)
    {
        int w = raster.Width, h = raster.Height;
        // The commonest border colour, each channel quantised to 8 levels so JPEG noise votes for one bin; the
        // background is that bin's mean.
        var votes = new Dictionary<int, (int Count, long R, long G, long B)>();
        void Vote(int x, int y)
        {
            int rgb = raster.Rgb(x, y);
            int key = ((rgb >> 21) & 7) << 6 | ((rgb >> 13) & 7) << 3 | ((rgb >> 5) & 7);
            votes.TryGetValue(key, out var v);
            votes[key] = (v.Count + 1, v.R + ((rgb >> 16) & 0xFF), v.G + ((rgb >> 8) & 0xFF), v.B + (rgb & 0xFF));
        }
        for (int x = 0; x < w; x++) { Vote(x, 0); Vote(x, h - 1); }
        for (int y = 1; y < h - 1; y++) { Vote(0, y); Vote(w - 1, y); }
        var best = votes.OrderByDescending(kv => kv.Value.Count).ThenBy(kv => kv.Key).First().Value;
        int bg = (int)(best.R / best.Count) << 16 | (int)(best.G / best.Count) << 8 | (int)(best.B / best.Count);

        var mask = new byte[w * h];
        var p = raster.Rgba;
        int br = (bg >> 16) & 0xFF, bgG = (bg >> 8) & 0xFF, bb = bg & 0xFF;
        for (int i = 0, k = 0; i < mask.Length; i++, k += 4)
            if (Math.Abs(p[k] - br) > InkLevels || Math.Abs(p[k + 1] - bgG) > InkLevels || Math.Abs(p[k + 2] - bb) > InkLevels)
                mask[i] = 1;
        return new PlotInk(raster, bg, mask);
    }
}

/// <summary>Finds the plots on a picture (R-im14-1, R-im14-2).</summary>
public static class PlotFrames
{
    /// <summary>A line is at least this share of the picture's shorter side, and at least <see cref="MinLinePx"/>.</summary>
    public const double MinLineShare = 0.12;
    public const int MinLinePx = 40;

    /// <summary>A line is at most this thick; thicker ink is a filled area, not a line.</summary>
    public const int MaxLineThickness = 6;

    /// <summary>A grid line spans at least this share of its plot's box.</summary>
    public const double GridSpan = 0.85;

    /// <summary>A circle is drawn when ink lies on it at at least this share of 720 sampled angles.</summary>
    public const double CircleCoverage = 0.85;

    /// <summary>An inner circle (a constant-r circle, a polar ring) is drawn at this share: labels and traces cover some.</summary>
    public const double InnerCoverage = 0.6;

    /// <summary>The plots on <paramref name="ink"/>, in reading order. Pure.</summary>
    public static IReadOnlyList<PlotFrame> Find(PlotInk ink)
    {
        ArgumentNullException.ThrowIfNull(ink);
        int minLine = Math.Max(MinLinePx, (int)(MinLineShare * Math.Min(ink.Width, ink.Height)));
        var lines = Lines(ink, minLine);
        var horizontal = lines.Where(l => l.Horizontal).ToList();
        var vertical = lines.Where(l => !l.Horizontal).ToList();

        var frames = new List<PlotFrame>();
        // Charts first: a chart drawn inside a box is the chart, not a rectangular plot.
        foreach (var d in horizontal.OrderByDescending(l => l.Length).ThenBy(l => l.Position))
        {
            if (frames.Any(f => Math.Abs(f.Cy - d.Position) < 3 && Math.Abs(f.Cx - (d.From + d.To) / 2.0) < 3)) continue;
            if (Chart(ink, d) is { } chart) frames.Add(chart);
        }
        foreach (var r in Rectangles(ink, horizontal, vertical))
        {
            // A box holding a chart that fills it is the chart's border.
            if (frames.Any(c => c.IsChart && c.Cx > r.Left && c.Cx < r.Right && c.Cy > r.Top && c.Cy < r.Bottom
                                && 2 * c.Radius >= 0.5 * Math.Min(r.Right - r.Left, r.Bottom - r.Top))) continue;
            frames.Add(r);
        }
        return [.. frames.OrderBy(f => f.Box.Top / 32).ThenBy(f => f.Box.Left).ThenBy(f => f.Box.Top).Select((f, i) => f with { Index = i })];
    }

    // ── lines ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Every horizontal and vertical line at least <paramref name="minLength"/> long.</summary>
    internal static List<PlotLine> Lines(PlotInk ink, int minLength)
    {
        var result = new List<PlotLine>();
        result.AddRange(Runs(ink, minLength, horizontal: true));
        result.AddRange(Runs(ink, minLength, horizontal: false));
        return result;
    }

    private static IEnumerable<PlotLine> Runs(PlotInk ink, int minLength, bool horizontal)
    {
        int along = horizontal ? ink.Width : ink.Height, across = horizontal ? ink.Height : ink.Width;
        bool At(int a, int c) => horizontal ? ink.At(a, c) : ink.At(c, a);
        // Runs per row, a gap of one pixel bridged (JPEG and anti-aliasing break a line now and then).
        var open = new List<(int From, int To, int First, int Last, List<int> Rows)>();
        var done = new List<(int From, int To, int First, int Last, List<int> Rows)>();
        for (int c = 0; c < across; c++)
        {
            var runs = new List<(int From, int To)>();
            for (int a = 0; a < along;)
            {
                if (!At(a, c)) { a++; continue; }
                int s = a, e = a;
                while (a < along && (At(a, c) || (a + 1 < along && At(a + 1, c) && a > s)))
                {
                    if (At(a, c)) e = a;
                    a++;
                }
                if (e - s + 1 >= minLength) runs.Add((s, e));
            }
            var next = new List<(int From, int To, int First, int Last, List<int> Rows)>();
            var usedRuns = new bool[runs.Count];
            foreach (var o in open)
            {
                int k = -1;
                for (int r = 0; r < runs.Count; r++)
                {
                    if (usedRuns[r]) continue;
                    int ov = Math.Min(o.To, runs[r].To) - Math.Max(o.From, runs[r].From);
                    if (ov >= 0.8 * Math.Min(o.To - o.From, runs[r].To - runs[r].From)) { k = r; break; }
                }
                if (k < 0) { done.Add(o); continue; }
                usedRuns[k] = true;
                o.Rows.Add(c);
                next.Add((Math.Min(o.From, runs[k].From), Math.Max(o.To, runs[k].To), o.First, c, o.Rows));
            }
            for (int r = 0; r < runs.Count; r++)
                if (!usedRuns[r]) next.Add((runs[r].From, runs[r].To, c, c, [c]));
            open = next;
        }
        done.AddRange(open);

        foreach (var l in done)
        {
            int thickness = l.Last - l.First + 1;
            if (thickness > MaxLineThickness) continue;
            // The centre across, weighted by how unlike the background each row's middle stretch is.
            double sw = 0, sp = 0;
            int mid = (l.From + l.To) / 2;
            int strongestRow = l.First;
            double strongest = -1;
            for (int c = l.First; c <= l.Last; c++)
            {
                double wgt = 0;
                for (int a = Math.Max(l.From, mid - 20); a <= Math.Min(l.To, mid + 20); a++)
                {
                    int p = horizontal ? ink.Raster.Rgb(a, c) : ink.Raster.Rgb(c, a);
                    wgt += PlotInk.Distance(p, ink.Background);
                }
                sw += wgt;
                sp += wgt * c;
                if (wgt > strongest) { strongest = wgt; strongestRow = c; }
            }
            // Its colour: the commonest along its strongest row — a curve lying on a grid line for a stretch must not
            // lend the line its colour.
            var bins = new Dictionary<int, (int N, long R, long G, long B)>();
            for (int a = l.From; a <= l.To; a++)
            {
                int p = horizontal ? ink.Raster.Rgb(a, strongestRow) : ink.Raster.Rgb(strongestRow, a);
                int key = ((p >> 20) & 0xF) << 8 | ((p >> 12) & 0xF) << 4 | ((p >> 4) & 0xF);
                bins.TryGetValue(key, out var b);
                bins[key] = (b.N + 1, b.R + ((p >> 16) & 0xFF), b.G + ((p >> 8) & 0xFF), b.B + (p & 0xFF));
            }
            var top = bins.OrderByDescending(kv => kv.Value.N).ThenBy(kv => kv.Key).First().Value;
            int rgb = (int)(top.R / top.N) << 16 | (int)(top.G / top.N) << 8 | (int)(top.B / top.N);
            yield return new PlotLine(horizontal, sw > 0 ? sp / sw : (l.First + l.Last) / 2.0, l.From, l.To, thickness, rgb);
        }
    }

    // ── rectangular plots ───────────────────────────────────────────────────────────────────────────────────

    private static IEnumerable<PlotFrame> Rectangles(PlotInk ink, List<PlotLine> horizontal, List<PlotLine> vertical)
    {
        var boxes = new List<PlotFrame>();
        foreach (var v in vertical)
            foreach (var h in horizontal)
            {
                double tol = Math.Max(4, Math.Max(v.Thickness, h.Thickness) + 2);
                // The corner: the vertical's foot on the horizontal's left end. Either may overshoot the other a little
                // (an axis drawn past its origin), so the corner is the crossing, wherever it lies near both ends.
                if (Math.Abs(v.Position - h.From) > Math.Max(tol, 0.03 * h.Length)) continue;
                if (Math.Abs(h.Position - v.To) > Math.Max(tol, 0.03 * v.Length)) continue;
                double left = v.Position, bottom = h.Position, right = h.To, top = v.From;
                if (right - left < 64 || bottom - top < 48) continue;
                boxes.Add(Rectangle(ink, left, top, right, bottom, horizontal, vertical, (v.Thickness + h.Thickness) / 2.0));
            }

        // Largest first; a box overlapping a kept one is the same plot (its grid line taken for an axis).
        var kept = new List<PlotFrame>();
        foreach (var b in boxes.OrderByDescending(b => (b.Right - b.Left) * (b.Bottom - b.Top)).ThenBy(b => b.Left).ThenBy(b => b.Top))
        {
            if (kept.Any(k => Overlap(k, b) > 0.3)) continue;
            if (b.XTicks.Count < PlotKind.MinTicksPerAxis || b.YTicks.Count < PlotKind.MinTicksPerAxis) continue;
            kept.Add(b);
        }
        return kept;

        static double Overlap(PlotFrame a, PlotFrame b)
        {
            double w = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left), h = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
            if (w <= 0 || h <= 0) return 0;
            double small = Math.Min((a.Right - a.Left) * (a.Bottom - a.Top), (b.Right - b.Left) * (b.Bottom - b.Top));
            return w * h / small;
        }
    }

    private static PlotFrame Rectangle(PlotInk ink, double left, double top, double right, double bottom,
                                       List<PlotLine> horizontal, List<PlotLine> vertical, double pen)
    {
        double width = right - left, height = bottom - top;
        var own = new List<PlotLine>();
        var xs = new List<double>();
        var ys = new List<double>();
        foreach (var v in vertical)
        {
            if (v.Position < left - 2 || v.Position > right + 2) continue;
            if (Math.Min(v.To, bottom) - Math.Max(v.From, top) < GridSpan * height) continue;
            own.Add(v);
            xs.Add(v.Position);
        }
        foreach (var h in horizontal)
        {
            if (h.Position < top - 2 || h.Position > bottom + 2) continue;
            if (Math.Min(h.To, right) - Math.Max(h.From, left) < GridSpan * width) continue;
            own.Add(h);
            ys.Add(h.Position);
        }
        // Ticks just outside each axis: short runs perpendicular to it, within a few pixels of it.
        int reach = (int)Math.Max(6, pen * 4) + 8;
        int ax = (int)Math.Round(bottom), ay = (int)Math.Round(left);
        xs.AddRange(TickPositions(ink, (int)left, (int)right, ax + (int)Math.Ceiling(pen), reach, horizontalAxis: true));
        xs.AddRange(TickPositions(ink, (int)left, (int)right, ax - (int)Math.Ceiling(pen) - reach, reach, horizontalAxis: true));
        ys.AddRange(TickPositions(ink, (int)top, (int)bottom, ay - (int)Math.Ceiling(pen) - reach, reach, horizontalAxis: false));
        ys.AddRange(TickPositions(ink, (int)top, (int)bottom, ay + (int)Math.Ceiling(pen), reach, horizontalAxis: false));
        return new PlotFrame
        {
            Type = PlotType.Rectangular, Left = left, Top = top, Right = right, Bottom = bottom,
            Box = new PixelBox((int)Math.Floor(left), (int)Math.Floor(top), (int)Math.Ceiling(right), (int)Math.Ceiling(bottom)),
            XTicks = Dedupe(xs), YTicks = Dedupe(ys), Lines = own, Pen = pen,
        };
    }

    /// <summary>Short ink runs crossing a band <paramref name="reach"/> deep starting at <paramref name="start"/> beside
    /// an axis: tick marks, at their centre positions along the axis.</summary>
    private static IEnumerable<double> TickPositions(PlotInk ink, int from, int to, int start, int reach, bool horizontalAxis)
    {
        var columns = new List<int>();
        for (int a = from; a <= to; a++)
        {
            int count = 0;
            for (int d = 0; d < reach; d++)
                if (horizontalAxis ? ink.At(a, start + d) : ink.At(start + d, a)) count++;
            if (count >= 3 && count < reach) columns.Add(a);
        }
        // Adjacent columns are one tick, at most 3 px wide (wider is a label's stem or a trace).
        for (int i = 0; i < columns.Count;)
        {
            int j = i;
            while (j + 1 < columns.Count && columns[j + 1] == columns[j] + 1) j++;
            if (columns[j] - columns[i] <= 3)
            {
                // Must be a lone vertical stroke: nothing beside it in the band on either side.
                int a0 = columns[i] - 2, a1 = columns[j] + 2;
                bool lone = true;
                for (int d = 0; d < reach && lone; d++)
                    lone = !(horizontalAxis ? ink.At(a0, start + d) || ink.At(a1, start + d) : ink.At(start + d, a0) || ink.At(start + d, a1));
                if (lone) yield return (columns[i] + columns[j]) / 2.0;
            }
            i = j + 1;
        }
    }

    private static List<double> Dedupe(List<double> v)
    {
        v.Sort();
        var r = new List<double>();
        foreach (double x in v)
            if (r.Count == 0 || x - r[^1] > 2) r.Add(x);
        return r;
    }

    // ── charts ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The chart whose real axis is <paramref name="diameter"/>, or null when no circle stands on it.</summary>
    private static PlotFrame? Chart(PlotInk ink, PlotLine diameter)
    {
        double cx = (diameter.From + diameter.To) / 2.0, cy = diameter.Position, r = (diameter.To - diameter.From) / 2.0;
        if (r < 32) return null;
        if (Coverage(ink, cx, cy, r, Window(r)) < CircleCoverage) return null;

        // Refit to the ink nearest the circle at each angle.
        var pts = new List<PointD>();
        double win = Window(r);
        for (int k = 0; k < 720; k++)
        {
            double t = k * Math.PI / 360, c = Math.Cos(t), s = Math.Sin(t);
            double bestD = double.MaxValue;
            PointD? best = null;
            for (double d = -win; d <= win; d += 0.5)
            {
                int x = (int)Math.Round(cx + (r + d) * c), y = (int)Math.Round(cy - (r + d) * s);
                if (ink.At(x, y) && Math.Abs(d) < bestD) { bestD = Math.Abs(d); best = new PointD(x, y); }
            }
            if (best is { } p) pts.Add(p);
        }
        var fit = Fit.Circle(pts);
        if (!(fit.R > 0) || Math.Abs(fit.R - r) > 0.05 * r) return null;
        (cx, cy, r) = (fit.Cx, fit.Cy, fit.R);
        win = Window(r);

        // Smith: the constant-r circles tangent at the right (impedance) or left (admittance) — r = 1 must pass through
        // the centre. Polar: rings about the centre.
        var rs = new[] { 1.0, 0.5, 2.0, 0.2, 5.0 };
        var z = rs.Where(v => Coverage(ink, cx + r * v / (1 + v), cy, r / (1 + v), win, tangent: 0) >= InnerCoverage).ToList();
        var ys = rs.Where(v => Coverage(ink, cx - r * v / (1 + v), cy, r / (1 + v), win, tangent: Math.PI) >= InnerCoverage).ToList();
        bool zChart = z.Contains(1.0) && z.Count >= 2, yChart = ys.Contains(1.0) && ys.Count >= 2;
        var rings = new List<double>();
        for (int k = 1; k <= 19; k++)
        {
            double share = k / 20.0;
            if (Math.Abs(share - 0.5) < 1e-9 && (zChart || yChart)) continue;   // the r = 1 circle's far side is not a ring
            if (Coverage(ink, cx, cy, share * r, win) >= CircleCoverage) rings.Add(share);
        }

        PlotType type;
        bool admittance = false, overlaid = false;
        IReadOnlyList<double> checkedCircles = [];
        if (zChart || yChart)
        {
            type = PlotType.Smith;
            admittance = yChart && !zChart;
            overlaid = zChart && yChart;
            checkedCircles = admittance ? ys : z;
        }
        else if (rings.Count >= 1) type = PlotType.Polar;
        else return null;

        double residual = Math.Sqrt(pts.Average(p => Sq(Math.Sqrt(Sq(p.x - cx) + Sq(p.y - cy)) - r)));
        return new PlotFrame
        {
            Type = type, Cx = cx, Cy = cy, Radius = r, Admittance = admittance, Overlaid = overlaid,
            CirclesChecked = checkedCircles, Rings = type == PlotType.Polar ? rings : [], Residual = Math.Round(residual, 3),
            Box = new PixelBox((int)Math.Floor(cx - r), (int)Math.Floor(cy - r), (int)Math.Ceiling(cx + r), (int)Math.Ceiling(cy + r)),
            Left = cx - r, Right = cx + r, Top = cy - r, Bottom = cy + r, Pen = diameter.Thickness, Lines = [diameter],
        };
    }

    private static double Window(double r) => Math.Max(2, 0.008 * r);

    /// <summary>The share of 720 angles at which ink lies within <paramref name="window"/> of the circle — of a
    /// circle tangent to the outer one, the angles within 40° of the <paramref name="tangent"/> point, where every
    /// constant-r circle crowds and any ink at all would count, are left out.</summary>
    private static double Coverage(PlotInk ink, double cx, double cy, double r, double window, double? tangent = null)
    {
        int hits = 0, n = 0;
        for (int k = 0; k < 720; k++)
        {
            double t = k * Math.PI / 360;
            if (tangent is { } a && Math.Abs(Math.IEEERemainder(t - a, 2 * Math.PI)) < 40 * Math.PI / 180) continue;
            double c = Math.Cos(t), s = Math.Sin(t);
            n++;
            for (double d = -window; d <= window; d += 0.5)
                if (ink.At((int)Math.Round(cx + (r + d) * c), (int)Math.Round(cy - (r + d) * s))) { hits++; break; }
        }
        return n == 0 ? 0 : (double)hits / n;
    }

    private static double Sq(double v) => v * v;
}
