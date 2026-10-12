using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Design.Schematic;
using CircuitRF.Render;

namespace CircuitRF.SchematicSynth;

/// <summary>How the canvas's pixels became the picture's: everything <see cref="Labels.Build"/> maps through.</summary>
/// <param name="World">A schematic-unit point to a final pixel.</param>
/// <param name="Logical">A point on the renderer's logical canvas (before the Retina scale) to a final pixel.</param>
/// <param name="Canvas">Where the canvas is in the final picture: [x0, y0, x1, y1].</param>
/// <param name="Pen">A wire's drawn width, final pixels.</param>
public sealed record ViewMap(Func<double, double, Px> World, Func<double, double, Px> Logical, double[] Canvas, double Pen);

/// <summary>
/// The exact label of a generated picture (brief-iml-2 R-iml2-4), from the model and the renderer's own measurements —
/// nothing read off pixels.
/// </summary>
public static class Labels
{
    public static (MlLabel Label, IReadOnlyList<string> Classes) Build(
        string imageName, Drawing d, SchematicModel rm, IReadOnlyList<SchematicPlacedText> placed, ViewMap map,
        Shot shot, string style)
    {
        double[] cv = map.Canvas;
        bool Inside(Px p) => p.X >= cv[0] && p.X <= cv[2] && p.Y >= cv[1] && p.Y <= cv[3];

        // ── texts ────────────────────────────────────────────────────────────
        var texts = new List<LabelText>();
        var ignore = new List<double[]>();
        var designatorOf = new Dictionary<string, string>(StringComparer.Ordinal);
        var valuesOf = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var t in placed)
        {
            if (t.Role == SchematicTextRole.Chrome) continue;
            if (t.Role == SchematicTextRole.SymbolText && PictureMaker.IsGlyphMark(t.Text)) continue;
            Px[] quad = [map.Logical(t.TopLeft.X, t.TopLeft.Y), map.Logical(t.TopRight.X, t.TopRight.Y),
                         map.Logical(t.BottomRight.X, t.BottomRight.Y), map.Logical(t.BottomLeft.X, t.BottomLeft.Y)];
            int inside = quad.Count(Inside);
            if (inside < 4)
            {
                if (inside > 0 || Overlaps(Box(quad), cv)) ignore.Add(Clip(Box(quad), cv));
                continue;
            }
            string role = t.Role switch
            {
                SchematicTextRole.InstanceName => "designator",
                SchematicTextRole.Parameter => ValueText.Reads(t.Text) ? "value" : "other",
                SchematicTextRole.NetLabel => "net",
                SchematicTextRole.PinName => "pin-name",
                SchematicTextRole.SymbolText => t.Text.All(char.IsAsciiDigit) ? "pin-number"
                                              : t.Text is "RF" or "LO" or "IF" or "TX" or "RX" ? "pin-name" : "other",
                _ => "other",
            };
            string id = "t" + (texts.Count + 1).ToString(CultureInfo.InvariantCulture);
            texts.Add(new LabelText(id, quad, t.Text, role));
            if (t.ComponentId is { } cid)
            {
                if (role == "designator") designatorOf[cid] = id;
                if (role == "value") (valuesOf.TryGetValue(cid, out var l) ? l : valuesOf[cid] = []).Add(id);
            }
        }

        // ── parts ────────────────────────────────────────────────────────────
        var open = OpenPins(d.Truth);
        var parts = new List<LabelPart>();
        var classes = new List<string>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in rm.Components)
        {
            if (!ClassList.OfKind.TryGetValue(c.Symbol, out var cls))
                throw new InvalidOperationException($"{c.InstanceName}: {c.Symbol} has no class");
            var symbol = c.InstanceSymbol ?? BuiltInSymbols.Primitives(c.Symbol, c.Ports.Count);
            var body = c.InstanceSymbol is not null ? GlyphBody.Of(symbol) : GlyphBody.Of(c.Symbol, c.Ports.Count);
            Px Local(double lx, double ly)
            {
                var (wx, wy) = SchematicGeometry.LocalToWorld((float)lx, (float)ly, c.X, c.Y, c.Rotation, c.MirrorX);
                return map.World(wx, wy);
            }
            Px[] corners = [Local(body.MinX, body.MinY), Local(body.MaxX, body.MinY), Local(body.MaxX, body.MaxY), Local(body.MinX, body.MaxY)];
            double[] box = Box(corners);
            var pins = c.Ports.Select(p => new LabelPin(p.Name, Local(p.LocalX, p.LocalY))).ToList();
            if (!Overlaps(box, cv) && !pins.Any(p => Inside(p.At))) continue;

            int turn = c.Rotation switch { SymbolRotation.R90 => 90, SymbolRotation.R180 => 180, SymbolRotation.R270 => 270, _ => 0 };

            // A TLIN with one end left open is an open stub: its one drawn pin is the class's pin 1, wherever it sits.
            if (c.Symbol == SymbolKind.Tline && open.TryGetValue(c.InstanceName, out var openPins) && openPins.Count == 1)
            {
                cls = "stub-open";
                int connected = openPins.Contains(0) ? 1 : 0;
                if (connected == 1) turn = (turn + 180) % 360;
                pins = [new LabelPin("1", pins[connected].At)];
            }

            // A pin in the picture's last pixel column or row is past the last pixel centre: cut, as the checks read it.
            bool cut = !(box[0] >= cv[0] && box[1] >= cv[1] && box[2] <= cv[2] && box[3] <= cv[3])
                       || pins.Any(p => !Inside(p.At) || p.At.X > shot.Width - 1 || p.At.Y > shot.Height - 1);
            string partId = c.InstanceName.Length > 0 && !ids.Contains(c.InstanceName) ? c.InstanceName
                          : "part" + (parts.Count + 1).ToString(CultureInfo.InvariantCulture);
            ids.Add(partId);
            parts.Add(new LabelPart(
                partId, cls, ClassList.VariantOf(c.Symbol), turn, c.MirrorX,
                box, pins, designatorOf.GetValueOrDefault(c.Id), valuesOf.GetValueOrDefault(c.Id) ?? [], cut));
            classes.Add(cls);
        }

        // ── wires, junctions, crossings ──────────────────────────────────────
        var wires = new List<LabelWire>();
        foreach (var w in rm.Wires)
        {
            var pts = w.Points.Select(p => map.World(p.X, p.Y)).ToList();
            foreach (var piece in ClipPolyline(pts, cv)) wires.Add(new LabelWire(piece, map.Pen));
        }
        var junctions = rm.ConnectionDots.Select(p => map.World(p.X, p.Y)).Where(Inside).ToList();
        var crossings = Crossings(rm).Select(p => map.World(p.X, p.Y)).Where(Inside).ToList();

        var annotations = shot.Chrome.Select(b => new LabelAnnotation(b, "chrome")).ToList();
        string stem = Path.GetFileNameWithoutExtension(imageName);
        var masks = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["wire"] = stem + ".wire.png", ["symbol"] = stem + ".symbol.png",
            ["text"] = stem + ".text.png", ["annotation"] = stem + ".annotation.png",
        };
        var label = new MlLabel(imageName, shot.Width, shot.Height, style, parts, wires, junctions, crossings, texts,
                                annotations, ignore, masks);
        return (label, classes);
    }

    /// <summary>Per instance, the pins (by index) whose net reaches no other pin — an open end.</summary>
    private static Dictionary<string, HashSet<int>> OpenPins(TestBench tb)
    {
        var count = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var i in tb.Instances)
            foreach (var n in i.NetBindings) count[n] = count.GetValueOrDefault(n) + 1;
        var open = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        foreach (var i in tb.Instances)
            for (int k = 0; k < i.NetBindings.Count; k++)
                if (i.NetBindings[k] != "0" && count[i.NetBindings[k]] == 1)
                    (open.TryGetValue(i.InstanceName, out var s) ? s : open[i.InstanceName] = []).Add(k);
        return open;
    }

    /// <summary>Where two wires cross with neither turning or ending there and no dot — not a connection.</summary>
    internal static List<(double X, double Y)> Crossings(SchematicModel rm)
    {
        var segs = new List<(int Wire, double Ax, double Ay, double Bx, double By)>();
        for (int w = 0; w < rm.Wires.Count; w++)
        {
            var p = rm.Wires[w].Points;
            for (int i = 0; i + 1 < p.Count; i++) segs.Add((w, p[i].X, p[i].Y, p[i + 1].X, p[i + 1].Y));
        }
        var dots = rm.ConnectionDots.Select(d => (d.X, d.Y)).ToList();
        var found = new List<(double X, double Y)>();
        const double eps = 0.5;
        foreach (var h in segs.Where(s => Math.Abs(s.Ay - s.By) < eps && Math.Abs(s.Ax - s.Bx) >= eps))
            foreach (var v in segs.Where(s => Math.Abs(s.Ax - s.Bx) < eps && Math.Abs(s.Ay - s.By) >= eps))
            {
                double x = v.Ax, y = h.Ay;
                double hx0 = Math.Min(h.Ax, h.Bx), hx1 = Math.Max(h.Ax, h.Bx), vy0 = Math.Min(v.Ay, v.By), vy1 = Math.Max(v.Ay, v.By);
                if (x <= hx0 + eps || x >= hx1 - eps || y <= vy0 + eps || y >= vy1 - eps) continue;
                if (dots.Any(d => Math.Abs(d.X - x) < eps && Math.Abs(d.Y - y) < eps)) continue;
                if (!found.Any(f => Math.Abs(f.X - x) < eps && Math.Abs(f.Y - y) < eps)) found.Add((x, y));
            }
        return found;
    }

    // ── geometry ─────────────────────────────────────────────────────────────

    private static double[] Box(IEnumerable<Px> pts)
    {
        var l = pts.ToList();
        return [l.Min(p => p.X), l.Min(p => p.Y), l.Max(p => p.X), l.Max(p => p.Y)];
    }

    private static bool Overlaps(double[] a, double[] b) => a[0] < b[2] && b[0] < a[2] && a[1] < b[3] && b[1] < a[3];

    private static double[] Clip(double[] a, double[] b) =>
        [Math.Max(a[0], b[0]), Math.Max(a[1], b[1]), Math.Min(a[2], b[2]), Math.Min(a[3], b[3])];

    /// <summary>A polyline cut to the canvas: the pieces inside it, each ending on the edge where it leaves.</summary>
    private static List<List<Px>> ClipPolyline(List<Px> pts, double[] r)
    {
        var pieces = new List<List<Px>>();
        List<Px>? cur = null;
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            if (ClipSegment(pts[i], pts[i + 1], r) is not { } s)
            {
                cur = null;
                continue;
            }
            if (cur is not null && Same(cur[^1], s.A)) cur.Add(s.B);
            else
            {
                cur = [s.A, s.B];
                pieces.Add(cur);
            }
            if (!Same(s.B, pts[i + 1])) cur = null;
        }
        return [.. pieces.Where(p => p.Count >= 2 && !(p.Count == 2 && Same(p[0], p[1])))];
    }

    private static bool Same(Px a, Px b) => Math.Abs(a.X - b.X) < 1e-6 && Math.Abs(a.Y - b.Y) < 1e-6;

    /// <summary>Liang–Barsky.</summary>
    private static (Px A, Px B)? ClipSegment(Px a, Px b, double[] r)
    {
        double t0 = 0, t1 = 1, dx = b.X - a.X, dy = b.Y - a.Y;
        bool Edge(double p, double q)
        {
            if (Math.Abs(p) < 1e-12) return q >= 0;
            double t = q / p;
            if (p < 0) { if (t > t1) return false; if (t > t0) t0 = t; }
            else { if (t < t0) return false; if (t < t1) t1 = t; }
            return true;
        }
        if (!Edge(-dx, a.X - r[0]) || !Edge(dx, r[2] - a.X) || !Edge(-dy, a.Y - r[1]) || !Edge(dy, r[3] - a.Y)) return null;
        return (new Px(a.X + t0 * dx, a.Y + t0 * dy), new Px(a.X + t1 * dx, a.Y + t1 * dy));
    }
}
