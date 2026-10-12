using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Design.Schematic;
using CircuitRF.Render;
using SkiaSharp;

namespace CircuitRF.SchematicSynth;

/// <summary>
/// IML-2's drawings drawn again in a style pack's conventions (brief-iml3 R-iml3-1): the same placement, wires, dots and
/// net names — the layout a designer made, or circuitRF's own Tidy one — with every part redrawn in the pack's glyph for
/// its class, every word in the pack's face, stack and spelling.
///
/// <para><b>A glyph lands on the placement's pins.</b> Its pins are fitted onto the circuitRF part's pins (a scale and an
/// offset per axis, then the part's own turn and mirror); a glyph drawn smaller than circuitRF's — the pack's symbol
/// scale — keeps its pins where the wires end by drawing the gap as lead. A box glyph (a block, an IC, an op-amp, an
/// analysis box) is stretched over the part's body and joined to each pin by a lead run until it meets the drawing.
/// The label's body is the glyph's own body (<see cref="Glyph.Body"/>, GlyphBody's rule) through the same transform, so
/// nothing is measured off pixels.</para>
///
/// <para><b>Substitutions mean the same circuit.</b> A class circuitRF never draws is drawn only where it stands for what
/// the circuit already holds: a capacitor as a polarised one, an S-parameter block as an IC box or (three ports) an op-amp,
/// a TLIN shorted to a ground symbol as one shorted-stub glyph, a supply net's name as a power port, a net's name as a
/// terminal. The netlist truth is unchanged by every one of them.</para>
/// </summary>
public static class SceneBuilder
{
    private const double Gap = 25;

    private sealed record Placed(ScenePart Part, EditableComponent? Comp, SchematicComponent Rc, bool Words);

    public static Scene Build(IReadOnlyList<Drawing> drawings, Look look, Random r)
    {
        var scene = new Scene();
        var placed = new List<Placed>();
        var netLabels = new List<(SchematicNetLabel Label, double Ox, double Oy)>();
        for (int k = 0; k < drawings.Count; k++)
        {
            var d = drawings[k];
            var (rm, _) = d.Model.BuildRenderModel();
            double ox = 0, oy = 0;
            if (k > 0 && DocumentExtents.SchematicBox(d.Model, rm) is { } e)
            {
                var have = scene.Extent();
                double gap = 400 + r.NextDouble() * 600;
                if (r.NextDouble() < 0.6) { ox = have.X1 - e.X0 + gap; oy = have.Y0 - e.Y0; }
                else { ox = have.X0 - e.X0; oy = have.Y1 - e.Y0 + gap; }
                ox = Math.Round(ox / 100) * 100;
                oy = Math.Round(oy / 100) * 100;
            }
            int before = scene.Parts.Count, wiresBefore = scene.WirePaths.Count;
            AddDrawing(scene, placed, d, rm, look, r, ox, oy, k == 0 ? "" : "b.");
            scene.Circuits.Add(Bounds(scene.Parts.Skip(before).SelectMany(p => new[] { new W(p.Body.X0, p.Body.Y0), new W(p.Body.X1, p.Body.Y1) })
                                          .Concat(scene.WirePaths.Skip(wiresBefore).SelectMany(w => w))));
            netLabels.AddRange(rm.NetLabels.Select(l => (l, ox, oy)));
        }

        foreach (var p in placed.Where(p => p.Words)) Words(scene, p, look, r);
        foreach (var (l, ox, oy) in netLabels) NetLabel(scene, l, new W(l.X + ox, l.Y + oy), look, r);
        return scene;
    }

    internal static (double X0, double Y0, double X1, double Y1) Bounds(IEnumerable<W> pts)
    {
        var l = pts.ToList();
        return l.Count == 0 ? (0, 0, 100, 100) : (l.Min(p => p.X), l.Min(p => p.Y), l.Max(p => p.X), l.Max(p => p.Y));
    }

    // ── one drawing ──────────────────────────────────────────────────────────

    private static void AddDrawing(Scene scene, List<Placed> placed, Drawing d, SchematicModel rm, Look look, Random r,
                                   double ox, double oy, string prefix)
    {
        var pack = look.Pack;
        var comps = d.Model.Components.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var open = Labels.OpenPins(d.Truth);
        var instances = d.Truth.Instances.ToDictionary(i => i.InstanceName, StringComparer.Ordinal);
        W World(double x, double y) => new(x + ox, y + oy);

        // Shorted stubs: a TLIN whose grounded end meets a ground symbol pin to pin, or by one wire nothing else touches.
        var dropComps = new HashSet<string>(StringComparer.Ordinal);
        var dropWires = new HashSet<int>();
        var shortStubs = new Dictionary<string, int>(StringComparer.Ordinal);   // TLIN id → its connected port
        var dropDots = new HashSet<(long, long)>();
        if (look.Draws("stub-short") && r.NextDouble() < pack.Substitute.GetValueOrDefault("stub-short", 1))
            FindShortStubs(rm, instances, open, dropComps, dropWires, shortStubs, dropDots);

        int n = 0;
        foreach (var c in rm.Components)
        {
            if (dropComps.Contains(c.Id)) continue;
            comps.TryGetValue(c.Id, out var ec);
            if (!ClassList.OfKind.TryGetValue(c.Symbol, out var cls)) throw new InvalidOperationException($"{c.InstanceName}: {c.Symbol} has no class");

            int pre = ClassList.CanonicalTurn(c.Symbol);
            var pinMap = c.Ports.Select((p, i) => (p.Name, i)).ToList();
            if (c.Symbol == SymbolKind.Tline && open.TryGetValue(c.InstanceName, out var openPins) && openPins.Count == 1)
            {
                int connected = openPins.Contains(0) ? 1 : 0;
                cls = "stub-open";
                pre = connected == 1 ? 180 : 0;
                pinMap = [("1", connected)];
            }
            else if (shortStubs.TryGetValue(c.Id, out int conn))
            {
                cls = "stub-short";
                pre = conn == 1 ? 180 : 0;
                pinMap = [("1", conn)];
            }
            else if (cls == "capacitor" && look.Draws("capacitor-polarised") && r.NextDouble() < pack.Substitute.GetValueOrDefault("capacitor-polarised"))
                cls = "capacitor-polarised";
            else if (cls == "block")
            {
                if (c.Ports.Count == 3 && look.Draws("op-amp") && r.NextDouble() < pack.Substitute.GetValueOrDefault("op-amp")) cls = "op-amp";
                else if (look.Draws("ic-box") && r.NextDouble() < pack.Substitute.GetValueOrDefault("ic-box")) cls = "ic-box";
            }

            var glyph = look.Glyph(cls);
            string id = prefix + (c.InstanceName.Length > 0 ? c.InstanceName : "part" + (++n).ToString(CultureInfo.InvariantCulture));
            var part = glyph.BoxFrame
                ? PlaceBox(c, glyph, cls, id, pinMap, look, World)
                : PlaceFitted(c, glyph, cls, id, pre, pinMap, look, World);
            scene.Parts.Add(part);
            foreach (var (_, at) in part.Pins) PinMarker(scene, at, look);
            if (cls == "ic-box") IcBox(scene, part, c, look, r, World);
            bool words = cls is not ("ground" or "via" or "no-connect");
            placed.Add(new Placed(part, ec, c, words));
        }

        // ── wires, dots, crossings ───────────────────────────────────────────
        var crossings = Labels.Crossings(rm).Select(p => World(p.X, p.Y)).ToList();
        scene.Crossings.AddRange(crossings);
        for (int i = 0; i < rm.Wires.Count; i++)
        {
            if (dropWires.Contains(i)) continue;
            var pts = rm.Wires[i].Points.Select(p => World(p.X, p.Y)).ToList();
            scene.WirePaths.Add(pts);
            scene.Wires.Add(new Ink(Layer.Wire, look.Wire, look.Hops ? Hopped(pts, crossings) : pts, false, false, 1, 'w', null));
        }
        foreach (var dot in rm.ConnectionDots)
        {
            if (dropDots.Contains(((long)Math.Round(dot.X * 2), (long)Math.Round(dot.Y * 2)))) continue;
            var at = World(dot.X, dot.Y);
            scene.Junctions.Add(at);
            if (look.Dots == "none" && Degree(rm, dot.X, dot.Y) < 4) continue;
            string shape = look.Dots == "none" ? "circle" : look.Dots;
            scene.Markers.Add(new Marker(Layer.Wire, look.Wire, at, shape, look.DotSize, 3, true));
        }
    }

    /// <summary>A TLIN with one end on ground is a shorted stub when that end meets a ground symbol directly, or through
    /// one wire nothing else touches: the stub glyph then draws the short itself, and the ground and wire are dropped.</summary>
    internal static void FindShortStubs(SchematicModel rm, Dictionary<string, Instance> instances, Dictionary<string, HashSet<int>> open,
                                       HashSet<string> dropComps, HashSet<int> dropWires, Dictionary<string, int> stubs,
                                       HashSet<(long, long)> dropDots)
    {
        static (long, long) Key(double x, double y) => ((long)Math.Round(x * 2), (long)Math.Round(y * 2));
        var touches = new Dictionary<(long, long), int>();
        void Touch(double x, double y) => touches[Key(x, y)] = touches.GetValueOrDefault(Key(x, y)) + 1;
        foreach (var c in rm.Components)
            foreach (var p in c.Ports) { var (x, y) = PortWorld(c, p); Touch(x, y); }
        foreach (var w in rm.Wires) foreach (var p in w.Points) Touch(p.X, p.Y);
        // A connection dot is not a third thing: circuitRF marks a pin meeting a pin with one. It goes with the ground.
        foreach (var l in rm.NetLabels) Touch(l.X, l.Y);

        var grounds = rm.Components.Where(c => c.Symbol == SymbolKind.Ground && c.Ports.Count == 1)
                                   .Select(c => (c, PortWorld(c, c.Ports[0]))).ToList();
        foreach (var t in rm.Components.Where(c => c.Symbol == SymbolKind.Tline && c.Ports.Count == 2))
        {
            if (!instances.TryGetValue(t.InstanceName, out var inst) || inst.NetBindings.Count != 2) continue;
            if (open.ContainsKey(t.InstanceName)) continue;
            int g = inst.NetBindings[0] == "0" ? 0 : inst.NetBindings[1] == "0" ? 1 : -1;
            if (g < 0 || inst.NetBindings[1 - g] == "0") continue;
            var (px, py) = PortWorld(t, t.Ports[g]);
            foreach (var (gc, (gx, gy)) in grounds)
            {
                if (dropComps.Contains(gc.Id)) continue;
                if (Key(px, py) == Key(gx, gy) && touches[Key(px, py)] == 2)
                {
                    dropComps.Add(gc.Id);
                    dropDots.Add(Key(px, py));
                    stubs[t.Id] = 1 - g;
                    break;
                }
                int wi = rm.Wires.ToList().FindIndex(w => w.Points.Count >= 2
                    && ((Key(w.Points[0].X, w.Points[0].Y) == Key(px, py) && Key(w.Points[^1].X, w.Points[^1].Y) == Key(gx, gy))
                     || (Key(w.Points[^1].X, w.Points[^1].Y) == Key(px, py) && Key(w.Points[0].X, w.Points[0].Y) == Key(gx, gy))));
                if (wi < 0 || dropWires.Contains(wi)) continue;
                var wire = rm.Wires[wi];
                bool alone = touches[Key(px, py)] == 2 && touches[Key(gx, gy)] == 2
                             && wire.Points.Skip(1).Take(wire.Points.Count - 2).All(p => touches[Key(p.X, p.Y)] == 1);
                if (!alone) continue;
                dropComps.Add(gc.Id);
                dropWires.Add(wi);
                dropDots.Add(Key(px, py));
                dropDots.Add(Key(gx, gy));
                stubs[t.Id] = 1 - g;
                break;
            }
        }
    }

    private static (double X, double Y) PortWorld(SchematicComponent c, SchematicPortDef p) =>
        SchematicGeometry.LocalToWorld(p.LocalX, p.LocalY, c.X, c.Y, c.Rotation, c.MirrorX);

    /// <summary>How many directions wires leave a point in — 4 at a crossing that connects.</summary>
    private static int Degree(SchematicModel rm, double x, double y)
    {
        var dirs = new HashSet<(int, int)>();
        foreach (var w in rm.Wires)
            for (int i = 0; i + 1 < w.Points.Count; i++)
            {
                var (a, b) = (w.Points[i], w.Points[i + 1]);
                bool onSeg = Math.Abs((b.X - a.X) * (y - a.Y) - (b.Y - a.Y) * (x - a.X)) < 1
                             && x >= Math.Min(a.X, b.X) - 0.5 && x <= Math.Max(a.X, b.X) + 0.5
                             && y >= Math.Min(a.Y, b.Y) - 0.5 && y <= Math.Max(a.Y, b.Y) + 0.5;
                if (!onSeg) continue;
                if (Math.Abs(a.X - x) > 0.5 || Math.Abs(a.Y - y) > 0.5) dirs.Add((Math.Sign(a.X - x), Math.Sign(a.Y - y)));
                if (Math.Abs(b.X - x) > 0.5 || Math.Abs(b.Y - y) > 0.5) dirs.Add((Math.Sign(b.X - x), Math.Sign(b.Y - y)));
            }
        return dirs.Count;
    }

    /// <summary>A wire's drawn path with a semicircle hop over every non-connecting crossing on its horizontal runs.</summary>
    private static List<W> Hopped(List<W> pts, List<W> crossings)
    {
        const double h = 22;
        var o = new List<W> { pts[0] };
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            var (a, b) = (pts[i], pts[i + 1]);
            if (Math.Abs(a.Y - b.Y) < 0.5 && Math.Abs(a.X - b.X) > 4 * h)
            {
                int dir = Math.Sign(b.X - a.X);
                foreach (var c in crossings.Where(c => Math.Abs(c.Y - a.Y) < 0.5 && (c.X - a.X) * dir > 2 * h && (b.X - c.X) * dir > 2 * h)
                                           .OrderBy(c => (c.X - a.X) * dir))
                {
                    o.Add(new W(c.X - dir * h, a.Y));
                    for (int k = 1; k < 12; k++)
                    {
                        double t = Math.PI * k / 12;
                        o.Add(new W(c.X - dir * h * Math.Cos(t), a.Y - h * Math.Sin(t)));
                    }
                    o.Add(new W(c.X + dir * h, a.Y));
                }
            }
            o.Add(b);
        }
        return o;
    }

    // ── fitting a glyph onto a placement ─────────────────────────────────────

    /// <summary>A quarter-turn clockwise on a y-down page, <paramref name="deg"/> a multiple of 90.</summary>
    internal static W Turn(W p, int deg) => (((deg % 360) + 360) % 360) switch
    {
        90 => new W(-p.Y, p.X),
        180 => new W(-p.X, -p.Y),
        270 => new W(p.Y, -p.X),
        _ => p,
    };

    private static double? Slope(IReadOnlyList<(double G, double C)> v)
    {
        if (v.Count < 2 || v.Max(t => t.G) - v.Min(t => t.G) < 1) return null;
        double mg = v.Average(t => t.G), mc = v.Average(t => t.C);
        double num = v.Sum(t => (t.G - mg) * (t.C - mc)), den = v.Sum(t => (t.G - mg) * (t.G - mg));
        return num / den;
    }

    private static ScenePart PlaceFitted(SchematicComponent c, Glyph g, string cls, string id, int pre,
                                         List<(string Name, int Port)> pinMap, Look look, Func<double, double, W> world)
    {
        var pairs = new List<(W G, W C, string Name)>();
        foreach (var (name, port) in pinMap)
        {
            if (!g.Pins.TryGetValue(name, out var gp)) throw new InvalidOperationException($"glyph {g.Name} has no pin {name}");
            var pp = c.Ports[port];
            pairs.Add((Turn(new W(gp.X, gp.Y), pre), new W(pp.LocalX, pp.LocalY), name));
        }
        double? sx = Slope([.. pairs.Select(p => (p.G.X, p.C.X))]), sy = Slope([.. pairs.Select(p => (p.G.Y, p.C.Y))]);
        double ax = sx ?? sy ?? 1, ay = sy ?? sx ?? 1;
        if (ax <= 0 || ay <= 0 || ax / ay > 2 || ay / ax > 2)
        {
            double u = Math.Max(0.25, Math.Abs(sx ?? sy ?? 1));
            ax = ay = u;
        }
        double bx = pairs.Average(p => p.C.X - ax * p.G.X), by = pairs.Average(p => p.C.Y - ay * p.G.Y);

        // The body centre in the placement's local frame; a glyph drawn smaller shrinks about it when every pin is in
        // line with it, so its leads stay straight.
        var bc = Turn(new W(g.Body.CenterX, g.Body.CenterY), pre);
        double cx = ax * bc.X + bx, cy = ay * bc.Y + by;
        bool inLine = pairs.All(p => Math.Abs(ax * p.G.X + bx - cx) < 1 || Math.Abs(ay * p.G.Y + by - cy) < 1);
        double s = inLine ? look.SymbolScale : 1;
        W Local(W gpt)
        {
            var t = Turn(gpt, pre);
            double lx = ax * t.X + bx, ly = ay * t.Y + by;
            return new W(cx + s * (lx - cx), cy + s * (ly - cy));
        }
        W ToWorld(W local)
        {
            var (x, y) = SchematicGeometry.LocalToWorld((float)local.X, (float)local.Y, c.X, c.Y, c.Rotation, c.MirrorX);
            return world(x, y);
        }

        int turn = ClassList.Turn(c.Rotation, c.MirrorX, pre);
        var corners = new[] { new W(g.Body.MinX, g.Body.MinY), new W(g.Body.MaxX, g.Body.MinY), new W(g.Body.MaxX, g.Body.MaxY), new W(g.Body.MinX, g.Body.MaxY) }
            .Select(p => ToWorld(Local(p))).ToList();
        var part = new ScenePart
        {
            Id = id, Class = cls, Variant = g.Variant,
            Turn = turn, Mirror = c.MirrorX,
            Body = (corners.Min(p => p.X), corners.Min(p => p.Y), corners.Max(p => p.X), corners.Max(p => p.Y)),
            Pins = [.. pairs.Select(p => (p.Name, ToWorld(p.C)))],
        };
        foreach (var st in g.Strokes) part.Inks.Add(InkOf(st, st.Points.Select(q => ToWorld(Local(new W(q.X, q.Y)))).ToList(), look));
        // The gap between where the glyph puts a pin and where the placement's wire ends is lead.
        foreach (var p in pairs)
        {
            var from = Local(Untrn(p.G, pre));
            if (Math.Abs(from.X - p.C.X) + Math.Abs(from.Y - p.C.Y) > 0.5)
                part.Inks.Add(new Ink(Layer.Symbol, look.Symbol, [ToWorld(from), ToWorld(p.C)], false, false, 1, 's', null));
        }
        return part;
    }

    /// <summary>The inverse of <see cref="Turn"/>.</summary>
    private static W Untrn(W p, int deg) => Turn(p, 360 - deg);

    private static Ink InkOf(Stroke st, List<W> pts, Look look)
    {
        var colour = st.Ink switch { "fill" => look.Fill, "paper" => look.Paper, _ => look.Symbol };
        return new Ink(Layer.Symbol, colour, pts, st.Closed, st.Fill, st.Pen, 's', st.Dash);
    }

    /// <summary>A box glyph stretched over the part's own body; each pin joined to the drawing by a lead run inward
    /// until it meets a stroke (an op-amp's slanted side) or the body's edge.</summary>
    private static ScenePart PlaceBox(SchematicComponent c, Glyph g, string cls, string id, List<(string Name, int Port)> pinMap,
                                      Look look, Func<double, double, W> world)
    {
        var symbol = c.InstanceSymbol ?? BuiltInSymbols.Primitives(c.Symbol, c.Ports.Count);
        var body = c.InstanceSymbol is not null ? GlyphBody.Of(symbol) : GlyphBody.Of(c.Symbol, c.Ports.Count);
        double x0 = body.MinX, y0 = body.MinY, w = Math.Max(1, body.Width), h = Math.Max(1, body.Height);
        W Local(double u, double v) => new(x0 + u * w, y0 + v * h);
        W ToWorld(W local)
        {
            var (x, y) = SchematicGeometry.LocalToWorld((float)local.X, (float)local.Y, c.X, c.Y, c.Rotation, c.MirrorX);
            return world(x, y);
        }

        var segs = new List<(W A, W B)>();
        var localStrokes = g.Strokes.Select(st => (st, Pts: st.Points.Select(q => Local(q.X, q.Y)).ToList())).ToList();
        foreach (var (st, pts) in localStrokes)
        {
            for (int i = 0; i + 1 < pts.Count; i++) segs.Add((pts[i], pts[i + 1]));
            if (st.Closed && pts.Count > 2) segs.Add((pts[^1], pts[0]));
        }

        int turn = ClassList.Turn(c.Rotation, c.MirrorX, 0);
        var corners = new[] { Local(0, 0), Local(1, 0), Local(1, 1), Local(0, 1) }.Select(ToWorld).ToList();
        var part = new ScenePart
        {
            Id = id, Class = cls, Turn = turn, Mirror = c.MirrorX, Variant = g.Variant,
            Body = (corners.Min(p => p.X), corners.Min(p => p.Y), corners.Max(p => p.X), corners.Max(p => p.Y)),
            Pins = [.. pinMap.Select(p => (p.Name, ToWorld(new W(c.Ports[p.Port].LocalX, c.Ports[p.Port].LocalY))))],
        };
        foreach (var (st, pts) in localStrokes) part.Inks.Add(InkOf(st, [.. pts.Select(ToWorld)], look));
        foreach (var (_, port) in pinMap)
        {
            var p = new W(c.Ports[port].LocalX, c.Ports[port].LocalY);
            var end = LeadEnd(p, x0, y0, x0 + w, y0 + h, segs);
            if (Math.Abs(end.X - p.X) + Math.Abs(end.Y - p.Y) > 0.5)
                part.Inks.Add(new Ink(Layer.Symbol, look.Symbol, [ToWorld(p), ToWorld(end)], false, false, 1, 's', null));
        }
        return part;
    }

    /// <summary>From a pin outside a body box, straight in toward it until the first stroke it meets (or the far edge).</summary>
    internal static W LeadEnd(W p, double x0, double y0, double x1, double y1, List<(W A, W B)> segs)
    {
        double dx = 0, dy = 0;
        if (p.X < x0 + 0.5) dx = 1; else if (p.X > x1 - 0.5) dx = -1;
        else if (p.Y < y0 + 0.5) dy = 1; else if (p.Y > y1 - 0.5) dy = -1;
        else return p;
        double far = dx != 0 ? Math.Abs((dx > 0 ? x1 : x0) - p.X) : Math.Abs((dy > 0 ? y1 : y0) - p.Y);
        double best = far;
        foreach (var (a, b) in segs)
        {
            // Ray p + t·d against segment a + u·(b − a).
            double ex = b.X - a.X, ey = b.Y - a.Y, den = dx * ey - dy * ex;
            if (Math.Abs(den) < 1e-9) continue;
            double t = ((a.X - p.X) * ey - (a.Y - p.Y) * ex) / den, u = ((a.X - p.X) * dy - (a.Y - p.Y) * dx) / den;
            if (t > 0.5 && u >= -1e-6 && u <= 1 + 1e-6 && t < best) best = t;
        }
        return new W(p.X + dx * best, p.Y + dy * best);
    }

    private static void PinMarker(Scene scene, W at, Look look)
    {
        if (look.PinMarker == "none") return;
        bool fill = look.PinMarker.StartsWith("filled", StringComparison.Ordinal);
        string shape = look.PinMarker.EndsWith("circle", StringComparison.Ordinal) ? "circle" : "square";
        scene.Markers.Add(new Marker(Layer.Symbol, look.Symbol, at, shape, 9, 3, fill));
    }

    // ── an IC box: numbered pins outside, named pins inside, unused pins marked no-connect ─────────────

    private static readonly string[] PinNames = ["RFIN", "RFOUT", "VCC", "GND", "IN", "OUT", "VDD", "EN", "BIAS", "VS", "RF", "LO", "IF", "VREF"];

    private static void IcBox(Scene scene, ScenePart ic, SchematicComponent c, Look look, Random r, Func<double, double, W> world)
    {
        var (bx0, by0, bx1, by1) = ic.Body;
        double size = look.TextSize * 0.6;
        int number = 1;
        foreach (var (_, at) in ic.Pins)
        {
            // Which side the pin leaves from, in world terms.
            double dl = Math.Abs(at.X - bx0), dr = Math.Abs(at.X - bx1), dt = Math.Abs(at.Y - by0), db = Math.Abs(at.Y - by1);
            double m = Math.Min(Math.Min(dl, dr), Math.Min(dt, db));
            var side = m == dl ? 'L' : m == dr ? 'R' : m == dt ? 'T' : 'B';
            string num = (number++).ToString(CultureInfo.InvariantCulture);
            var numText = Word(num, "pin-number", look.Face, size, look.Text);
            var nameText = Word(PinNames[r.Next(PinNames.Length)], "pin-name", look.Face, size, look.Text);
            var (nw, na, _) = numText.Metrics();
            var (mw, ma, md) = nameText.Metrics();
            switch (side)
            {
                case 'L':
                    numText.Origin = new W(bx0 - nw - 15, at.Y - 12);
                    nameText.Origin = new W(bx0 + 15, at.Y - (ma + md) / 2);
                    break;
                case 'R':
                    numText.Origin = new W(bx1 + 15, at.Y - 12);
                    nameText.Origin = new W(bx1 - 15 - mw, at.Y - (ma + md) / 2);
                    break;
                case 'T':
                    numText.Origin = new W(at.X + 12, by0 - 15);
                    nameText.Origin = new W(at.X - mw / 2, by0 + 15 - ma);
                    break;
                default:
                    numText.Origin = new W(at.X + 12, by1 + 15 - na);
                    nameText.Origin = new W(at.X - mw / 2, by1 - 15);
                    break;
            }
            if (bx1 - bx0 > mw + 40 && by1 - by0 > -ma + 30) scene.Texts.Add(nameText);
            scene.Texts.Add(numText);
        }

        // One or two unused pins on a free side, a no-connect mark on each.
        if (r.NextDouble() < 0.6 && look.Draws("no-connect"))
        {
            var used = ic.Pins.Select(p => p.At).ToList();
            var nc = look.Glyph("no-connect");
            foreach (var (ax, ay, ox, oy) in new[] { ((bx0 + bx1) / 2, by1, 0.0, 1.0), ((bx0 + bx1) / 2, by0, 0.0, -1.0), (bx1, (by0 + by1) / 2, 1.0, 0.0), (bx0, (by0 + by1) / 2, -1.0, 0.0) })
            {
                if (used.Any(u => Math.Abs(u.X - ax) < 60 && Math.Abs(u.Y - ay) < 60)) continue;
                if (used.Any(u => Math.Abs((u.X - ax) * oy) < 1 && Math.Abs((u.Y - ay) * ox) < 1 && Math.Sign((u.X - ax) * ox + (u.Y - ay) * oy) > 0)) continue;
                var end = new W(ax + ox * 100, ay + oy * 100);
                ic.Inks.Add(new Ink(Layer.Symbol, look.Symbol, [new W(ax, ay), end], false, false, 1, 's', null));
                var gp = nc.Pins["1"];
                var pts = nc.Strokes.Select(st => (st, st.Points.Select(q => new W(end.X + q.X - gp.X, end.Y + q.Y - gp.Y)).ToList())).ToList();
                var ncPart = new ScenePart
                {
                    Id = ic.Id + ".nc" + (scene.Parts.Count + 1).ToString(CultureInfo.InvariantCulture), Class = "no-connect",
                    Body = (end.X + nc.Body.MinX - gp.X, end.Y + nc.Body.MinY - gp.Y, end.X + nc.Body.MaxX - gp.X, end.Y + nc.Body.MaxY - gp.Y),
                    Pins = [("1", end)],
                };
                foreach (var (st, p) in pts) ncPart.Inks.Add(InkOf(st, p, look));
                scene.Parts.Add(ncPart);
                if (r.NextDouble() < 0.5) break;
            }
        }
    }

    // ── words ────────────────────────────────────────────────────────────────

    internal static SceneText Word(string s, string role, SKTypeface face, double size, SKColor colour, ScenePart? owner = null, int sub = 0) =>
        new() { String = s, Role = role, Face = face, Size = size, Colour = colour, Owner = owner, SubscriptFrom = sub };

    private static void Words(Scene scene, Placed p, Look look, Random r)
    {
        var ec = p.Comp;
        if (ec is null) return;
        var part = p.Part;
        double size = look.TextSize;

        if (part.Class == "controller")
        {
            // A VAR block: its name inside the box, its rows under it.
            var title = Word(ec.TypeLabelText(), "other", look.Face, size * 0.8, look.Type);
            var (tw, ta, td) = title.Metrics();
            title.Origin = new W((part.Body.X0 + part.Body.X1) / 2 - tw / 2, (part.Body.Y0 + part.Body.Y1) / 2 - (ta + td) / 2);
            scene.Texts.Add(title);
            double y = part.Body.Y1 + Gap;
            foreach (var row in new[] { ec.ShowInstanceName ? ec.InstanceName : "" }.Concat(ec.LabelParameters().Select(q => q.Name + "=" + q.Expression)).Where(x => x.Length > 0))
            {
                var t = Word(row, "other", look.Face, size, look.Text);
                t.Origin = new W(part.Body.X0, y - t.Metrics().Ascent);
                y += size * 1.15;
                scene.Texts.Add(t);
            }
            return;
        }

        var lines = new List<List<SceneText>>();
        foreach (string kind in look.Stack.Lines)
        {
            switch (kind)
            {
                case "type" when ec.ShowTypeLabel:
                    lines.Add([Word(ec.TypeLabelText(), "other", look.Face, size, look.Type, part)]);
                    break;
                case "designator" when ec.ShowInstanceName && ec.InstanceName.Length > 0:
                {
                    string des = ec.InstanceName;
                    int sub = look.Subscripts ? des.TakeWhile(ch => !char.IsAsciiDigit(ch)).Count() : 0;
                    if (sub == des.Length || !des[sub..].All(char.IsAsciiDigit)) sub = 0;
                    var t = Word(des, "designator", look.Face, size, look.Text, part, sub);
                    part.Designator = t;
                    lines.Add([t]);
                    break;
                }
                case "values" or "value":
                    foreach (var q in ec.LabelParameters().Where(q => q.Expression.Length > 0))
                    {
                        var (text, si) = ForeignValues.Format(q, look, r);
                        bool reads = ValueText.Reads(text);
                        var t = Word(text, reads ? "value" : "other", look.Face, size, look.Text, part);
                        if (reads) part.Values.Add(t);
                        else if (si is not null) part.Params[q.Name] = si;
                        lines.Add([t]);
                        if (kind == "value") break;
                    }
                    break;
            }
        }
        if (lines.Count == 0) return;
        if (look.Stack.Row && lines.Count >= 2 && part.Designator is { } d)
        {
            // The designator and the line after it side by side, as `R1 10k` — two words, two roles.
            int i = lines.FindIndex(l => l.Contains(d));
            if (i >= 0 && i + 1 < lines.Count) { lines[i].AddRange(lines[i + 1]); lines.RemoveAt(i + 1); }
        }
        Stack(scene, part, lines, look, r);
    }

    /// <summary>Lays a block of lines beside the part — the pack's preferred side first, then the others — on the first
    /// spot clear of every body and word, and clear of wires if one is.</summary>
    private static void Stack(Scene scene, ScenePart part, List<List<SceneText>> lines, Look look, Random r)
    {
        double step = look.TextSize * 1.15, space = look.TextSize * 0.4;
        var widths = lines.Select(l => l.Sum(t => t.Metrics().Width) + space * (l.Count - 1)).ToList();
        double bw = widths.Max(), ascent = -lines[0][0].Metrics().Ascent;
        double bh = step * (lines.Count - 1) + ascent + lines[^1][0].Metrics().Descent;
        var (x0, y0, x1, y1) = part.Body;
        double cx = (x0 + x1) / 2, cy = (y0 + y1) / 2;
        bool vertical = y1 - y0 >= x1 - x0;
        bool rotated = look.Rotated && vertical;
        double g = Gap + 10;

        // Block top-left corners to try, in order.
        var right = (x1 + g, cy - bh / 2);
        var left = (x0 - g - bw, cy - bh / 2);
        var above = (r.NextDouble() < 0.5 ? cx - bw / 2 : x0, y0 - g - bh);
        var below = (r.NextDouble() < 0.5 ? cx - bw / 2 : x0, y1 + g);
        var order = look.Placement switch
        {
            "above" => new[] { above, below, right, left },
            "below" => new[] { below, above, right, left },
            _ => vertical ? new[] { right, left, above, below } : new[] { above, below, right, left },
        };
        if (rotated) { bw = step * lines.Count; order = [(x1 + g, cy - widths.Max() / 2), (x0 - g - bw, cy - widths.Max() / 2)]; }

        (double X, double Y)? chosen = null, fallback = null;
        foreach (var o in order)
        {
            var box = rotated ? (o.Item1, o.Item2, o.Item1 + bw, o.Item2 + widths.Max()) : (o.Item1, o.Item2, o.Item1 + bw, o.Item2 + bh);
            if (!scene.Clear(box, part)) continue;
            fallback ??= o;
            if (scene.TouchesWire(box)) continue;
            chosen = o;
            break;
        }
        var at = chosen ?? fallback ?? order[0];

        for (int i = 0; i < lines.Count; i++)
        {
            double x = 0;
            foreach (var t in lines[i])
            {
                if (rotated)
                {
                    t.Angle = 90;
                    // Columns left to right, each read bottom to top from the block's bottom edge.
                    t.Origin = new W(at.X + ascent + i * step, at.Y + widths.Max() - x);
                }
                else t.Origin = new W(at.X + x, at.Y + ascent + i * step);
                x += t.Metrics().Width + space;
                scene.Texts.Add(t);
            }
        }
    }

    // ── net names ────────────────────────────────────────────────────────────

    private static bool SupplyLike(string name)
    {
        string n = name.ToLowerInvariant();
        return n.StartsWith("vd", StringComparison.Ordinal) || n.StartsWith("vc", StringComparison.Ordinal) || n.StartsWith("vs", StringComparison.Ordinal)
               || n.StartsWith("vg", StringComparison.Ordinal) || n.StartsWith("vb", StringComparison.Ordinal) || n.StartsWith("vee", StringComparison.Ordinal)
               || n.StartsWith("vneg", StringComparison.Ordinal) || n is "vcc" or "vdd" or "+5v" or "+3v3";
    }

    private static void NetLabel(Scene scene, SchematicNetLabel l, W at, Look look, Random r)
    {
        string mode = look.Pack.NetLabels.Pick(r);
        // A supply's name is a power port in most editors that have one; any other name never is.
        bool supply = SupplyLike(l.Name) && look.Draws("power-port");
        if (supply && r.NextDouble() < 0.6) mode = "power-port";
        if (mode == "power-port" && !supply) mode = "text";
        if (mode == "terminal" && !look.Draws("terminal")) mode = "text";
        if (mode != "text" && NamedGlyph(scene, l.Name, at, mode, look)) return;

        var t = Word(l.Name, "net", look.Face, look.TextSize, look.Text);
        var (w, a, _) = t.Metrics();
        double g = 15;
        foreach (var (x, y) in new[] { (at.X + g, at.Y - g), (at.X - g - w, at.Y - g), (at.X + g, at.Y + g - a), (at.X - g - w, at.Y + g - a) })
        {
            t.Origin = new W(x, y);
            if (scene.Clear(t.Box(), air: 5)) break;
        }
        scene.Texts.Add(t);
    }

    /// <summary>A net's name drawn as a power port or terminal glyph at the label's point, pointing whichever way is
    /// clear, its name beyond it. False when no way is clear.</summary>
    private static bool NamedGlyph(Scene scene, string name, W at, string cls, Look look)
    {
        var g = look.Glyph(cls);
        var gp = g.Pins["1"];
        // The canonical body direction: a power port's body is above its pin, a terminal's below.
        var dirs = cls == "power-port" ? new[] { (0, -1), (0, 1), (1, 0), (-1, 0) } : new[] { (1, 0), (-1, 0), (0, 1), (0, -1) };
        foreach (var (dx, dy) in dirs)
        {
            int turn = cls == "power-port"
                ? (dx, dy) switch { (0, -1) => 0, (1, 0) => 90, (0, 1) => 180, _ => 270 }
                : (dx, dy) switch { (0, 1) => 0, (-1, 0) => 90, (0, -1) => 180, _ => 270 };
            W Map(double x, double y) { var t = Turn(new W(x - gp.X, y - gp.Y), turn); return new W(at.X + t.X, at.Y + t.Y); }
            var corners = new[] { Map(g.Body.MinX, g.Body.MinY), Map(g.Body.MaxX, g.Body.MaxY) };
            var body = (Math.Min(corners[0].X, corners[1].X), Math.Min(corners[0].Y, corners[1].Y), Math.Max(corners[0].X, corners[1].X), Math.Max(corners[0].Y, corners[1].Y));
            var probe = (body.Item1 + dx * 8, body.Item2 + dy * 8, body.Item3 + dx * 8, body.Item4 + dy * 8);
            if (!scene.Clear(body) || scene.TouchesWire(probe)) continue;

            var part = new ScenePart { Id = "net." + name + "." + scene.Parts.Count.ToString(CultureInfo.InvariantCulture), Class = cls, Turn = turn, Body = body, Pins = [("1", at)] };
            foreach (var st in g.Strokes) part.Inks.Add(InkOf(st, [.. st.Points.Select(q => Map(q.X, q.Y))], look));
            scene.Parts.Add(part);

            var t = Word(name, "net", look.Face, look.TextSize, look.Text);
            var (w, a, d) = t.Metrics();
            double gap = 15;
            t.Origin = (dx, dy) switch
            {
                (0, -1) => new W((body.Item1 + body.Item3) / 2 - w / 2, body.Item2 - gap - d),
                (0, 1) => new W((body.Item1 + body.Item3) / 2 - w / 2, body.Item4 + gap - a),
                (1, 0) => new W(body.Item3 + gap, (body.Item2 + body.Item4) / 2 - (a + d) / 2),
                _ => new W(body.Item1 - gap - w, (body.Item2 + body.Item4) / 2 - (a + d) / 2),
            };
            scene.Texts.Add(t);
            return true;
        }
        return false;
    }
}
