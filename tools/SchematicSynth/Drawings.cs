using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Schematic;

namespace CircuitRF.SchematicSynth;

/// <summary>How a picture's schematic was laid out (brief-iml-2 R-iml2-2).</summary>
public enum DrawMode { Tidy, Hand, Authored }

/// <summary>A drawn schematic and the netlist it must extract to.</summary>
/// <param name="Truth">The netlist truth written beside the picture.</param>
/// <param name="Source">The recipes, or the authored file's name.</param>
public sealed record Drawing(SchematicEditModel Model, TestBench Truth, DrawMode Mode, string Source);

/// <summary>
/// The three ways a picture's schematic is laid out (R-iml2-2): <b>Tidy</b> — <see cref="NetlistSchematic.Build"/>'s
/// own arrangement; <b>hand-placed</b> — parts at random grid positions and turns, wired by
/// <see cref="SchematicAutoRouter"/>, some nets labelled; and <b>authored</b> — one of the repository's own
/// <c>.csch</c> files, perturbed. Every drawing is extracted through <see cref="NetExtractor"/> and compared with its
/// truth before it is used (R-iml2-6); one that does not extract to its circuit is never drawn.
/// </summary>
public static class Drawings
{
    private const double P = NetlistSchematic.Grid;

    // ── Tidy ─────────────────────────────────────────────────────────────────

    public static Drawing? Tidy(GeneratedCircuit c, Random r)
    {
        var built = NetlistSchematic.Build(c.Library, c.TestBench);
        if (built.Schematic is not { } model) return null;
        if (!Netlists.Same(c.TestBench, NetExtractor.Extract(model).TestBench, values: true, out _)) return null;

        // Tidy labels every net; a designer labels some. Take labels off the wired nets and keep the drawing only if
        // it still extracts to the same circuit — a label can be the only thing joining two runs.
        double keep = r.NextDouble() * 0.6;
        var removed = model.NetLabels.Where(_ => r.NextDouble() > keep).ToList();
        foreach (var l in removed) model.NetLabels.Remove(l);
        if (removed.Count > 0 && !Netlists.Same(c.TestBench, NetExtractor.Extract(model).TestBench, values: true, out _))
            model.NetLabels.AddRange(removed);

        Dress(model, r);
        return new Drawing(model, c.TestBench, DrawMode.Tidy, string.Join("+", c.Recipes));
    }

    // ── Hand-placed ──────────────────────────────────────────────────────────

    public static Drawing? Hand(GeneratedCircuit c, Random r)
    {
        var tb = c.TestBench;
        // Every part placed at a provisional spot first; the drawing's own components (with the palette's parameters
        // and label choices) are then moved where this layout wants them.
        var provisional = new ExactPlacement
        {
            Instances = tb.Instances.Select((inst, k) => (inst.InstanceName, new ExactInstance(k * 3000, 0, SymbolRotation.R0, false)))
                                    .ToDictionary(t => t.InstanceName, t => t.Item2, StringComparer.Ordinal),
            PlaceOnly = true,
        };
        var built = NetlistSchematic.Build(c.Library, tb, exact: provisional);
        if (built.Schematic is not { } model) return null;

        var byName = model.Components.ToDictionary(x => x.InstanceName, StringComparer.Ordinal);
        var occupied = new List<(double X0, double Y0, double X1, double Y1)>();
        int n = tb.Instances.Count;
        double span = Math.Max(1600, Math.Sqrt(n) * 1100);

        var pins = new List<(EditableComponent Comp, double X, double Y, string Net, (int X, int Y) Out)>();
        foreach (var inst in tb.Instances)
        {
            var comp = byName[inst.InstanceName];
            bool placed = false;
            for (int attempt = 0; attempt < 60 && !placed; attempt++)
            {
                double grow = 1 + attempt / 20.0;
                comp.Rotation = PickRotation(r);
                comp.MirrorX = r.NextDouble() < 0.12;
                comp.X = Snap(r.NextDouble() * span * grow);
                comp.Y = Snap(r.NextDouble() * span * 0.7 * grow);
                var box = Keep(comp);
                if (occupied.Any(o => Overlaps(o, box))) continue;
                occupied.Add(box);
                placed = true;
            }
            if (!placed) return null;

            var offsets = NetlistSchematic.PinOffsets(inst, comp.Rotation, comp.MirrorX);
            if (offsets is null) return null;
            var (bcx, bcy) = BodyCentre(comp);
            foreach (var (ox, oy, net) in offsets)
            {
                double px = comp.X + ox, py = comp.Y + oy;
                pins.Add((comp, px, py, net, Outward(px - bcx, py - bcy)));
            }
        }

        // A ground symbol for each grounded pin, out along the pin's lead and turned to point away from the part, joined
        // to it by a net of its own: the router wires two terminals, and the ground net stays the ground net.
        var nets = new List<(EditableComponent Comp, double X, double Y, string Net)>();
        int g = 0;
        foreach (var (comp, px, py, net, (ox, oy)) in pins)
        {
            if (net != "0") { nets.Add((comp, px, py, net)); continue; }
            string own = "0#" + (++g).ToString(CultureInfo.InvariantCulture);
            var gnd = new EditableComponent { Symbol = SymbolKind.Ground, InstanceName = "GND" + g.ToString(CultureInfo.InvariantCulture) };
            gnd.Rotation = (ox, oy) switch { (0, -1) => SymbolRotation.R180, (1, 0) => SymbolRotation.R270, (-1, 0) => SymbolRotation.R90, _ => SymbolRotation.R0 };
            bool placed = false;
            foreach (int d in new[] { 200, 300, 400, 100, 500 })
            {
                gnd.X = Snap(px + ox * d);
                gnd.Y = Snap(py + oy * d);
                var box = Keep(gnd, 50);
                if (occupied.Any(o => Overlaps(o, box))) continue;
                occupied.Add(box);
                placed = true;
                break;
            }
            if (!placed) return null;
            model.Components.Add(gnd);
            nets.Add((comp, px, py, own));
            nets.Add((gnd, gnd.X, gnd.Y, own));
        }

        var blocks = new List<SchematicAutoRouter.Block>();
        foreach (var comp in model.Components)
        {
            var terminals = nets.Where(t => ReferenceEquals(t.Comp, comp)).Select(t => (t.X, t.Y, (string?)t.Net)).ToList();
            blocks.Add(new SchematicAutoRouter.Block(terminals, KeepOutCells(comp)));
        }
        var routed = SchematicAutoRouter.Route(blocks);
        if (routed.Unrouted.Any(u => u.Net.StartsWith("0#", StringComparison.Ordinal))) return null;

        foreach (var w in routed.Wires)
        {
            var wire = new EditableWire();
            wire.Points.AddRange(w);
            model.Wires.Add(wire);
        }
        // A terminal the router could not reach is joined by a net label on it, which is a real connection — and one more
        // on a terminal it did reach, so the wired part of the net carries the same name.
        foreach (var group in routed.Unrouted.GroupBy(u => u.Net, StringComparer.Ordinal))
        {
            foreach (var (x, y, net) in group)
                model.NetLabels.Add(new EditableNetLabel { X = x, Y = y, Name = net });
            var reached = nets.FirstOrDefault(t => t.Net == group.Key && !group.Any(u => Near((u.X, u.Y), (t.X, t.Y))));
            if (reached.Comp is not null)
                model.NetLabels.Add(new EditableNetLabel { X = reached.X, Y = reached.Y, Name = group.Key });
        }

        // Some wired nets carry their name, as a designer names the ones that matter.
        var named = nets.Select(t => t.Net).Where(x => !x.StartsWith("0#", StringComparison.Ordinal) && !x.StartsWith("nc", StringComparison.Ordinal))
                        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        foreach (var net in named)
        {
            if (r.NextDouble() > 0.3) continue;
            var netPins = nets.Where(t => t.Net == net).Select(t => (t.X, t.Y)).ToList();
            var wire = model.Wires.FirstOrDefault(w => w.Points.Any(p => netPins.Any(q => Near(p, q))));
            if (wire is null || wire.Points.Count < 2) continue;
            int seg = LongestSegment(wire);
            var (ax, ay) = wire.Points[seg];
            var (bx, by) = wire.Points[seg + 1];
            var label = new EditableNetLabel { Name = net };
            label.AnchorToWire(wire, Snap((ax + bx) / 2), Snap((ay + by) / 2));
            model.NetLabels.Add(label);
        }

        if (!Netlists.Same(tb, NetExtractor.Extract(model).TestBench, values: true, out _)) return null;
        Dress(model, r);
        return new Drawing(model, tb, DrawMode.Hand, string.Join("+", c.Recipes));
    }

    private static SymbolRotation PickRotation(Random r)
    {
        double x = r.NextDouble();
        return x < 0.5 ? SymbolRotation.R0 : x < 0.8 ? SymbolRotation.R90 : x < 0.9 ? SymbolRotation.R180 : SymbolRotation.R270;
    }

    /// <summary>The part's glyph and label block, with a grid step of air round it.</summary>
    private static (double X0, double Y0, double X1, double Y1) Keep(EditableComponent comp, double air = P)
    {
        var rc = comp.ToRenderComponent();
        return (Math.Min(rc.GlyphBbMinX, rc.FullBbMinX) - air, Math.Min(rc.GlyphBbMinY, rc.FullBbMinY) - air,
                Math.Max(rc.GlyphBbMaxX, Math.Min(rc.FullBbMaxX, rc.FullBbMinX + 700)) + air, Math.Max(rc.GlyphBbMaxY, rc.FullBbMaxY) + air);
    }

    private static bool Overlaps((double X0, double Y0, double X1, double Y1) a, (double X0, double Y0, double X1, double Y1) b)
        => a.X0 < b.X1 && b.X0 < a.X1 && a.Y0 < b.Y1 && b.Y0 < a.Y1;

    private static (double X, double Y) BodyCentre(EditableComponent comp)
    {
        var body = GlyphBody.Of(comp.Symbol, comp.PortCount);
        return SchematicGeometry.LocalToWorld((float)body.CenterX, (float)body.CenterY, comp.X, comp.Y, comp.Rotation, comp.MirrorX);
    }

    private static (int X, int Y) Outward(double dx, double dy)
        => Math.Abs(dx) >= Math.Abs(dy) ? (Math.Sign(dx) is var s and not 0 ? s : 1, 0) : (0, Math.Sign(dy));

    private static List<(double X, double Y)> KeepOutCells(EditableComponent comp)
    {
        var (x0, y0, x1, y1) = comp.ComputeGlyphBb();
        var cells = new List<(double X, double Y)>();
        for (double x = Math.Ceiling(x0 / P) * P; x <= x1; x += P)
            for (double y = Math.Ceiling(y0 / P) * P; y <= y1; y += P)
                cells.Add((x, y));
        return cells;
    }

    private static int LongestSegment(EditableWire w)
    {
        int best = 0;
        double len = -1;
        for (int i = 0; i + 1 < w.Points.Count; i++)
        {
            double l = Math.Abs(w.Points[i + 1].X - w.Points[i].X) + Math.Abs(w.Points[i + 1].Y - w.Points[i].Y);
            if (l > len) { len = l; best = i; }
        }
        return best;
    }

    private static bool Near((double X, double Y) a, (double X, double Y) b) => Math.Abs(a.X - b.X) < 0.5 && Math.Abs(a.Y - b.Y) < 0.5;

    private static double Snap(double v) => Math.Round(v / P) * P;

    // ── Authored ─────────────────────────────────────────────────────────────

    /// <summary>The repository's <c>.csch</c> files whose every part has a class — the rest would put unlabelled ink in
    /// a picture.</summary>
    public static IReadOnlyList<string> AuthoredSources(string repoRoot)
    {
        var found = new List<string>();
        foreach (string dir in new[] { "examples", "tests", "testdata" })
        {
            string root = Path.Combine(repoRoot, dir);
            if (!Directory.Exists(root)) continue;
            foreach (string f in Directory.EnumerateFiles(root, "*.csch", SearchOption.AllDirectories))
            {
                if (f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
                try
                {
                    var (model, _, _) = SchematicPersistence.LoadFromFile(f);
                    if (model.Components.Count == 0) continue;
                    if (model.Components.All(x => x.CellRef is null && ClassList.OfKind.ContainsKey(x.Symbol)))
                        found.Add(f);
                }
                catch (Exception) { /* not readable here: not a source */ }
            }
        }
        found.Sort(StringComparer.Ordinal);
        return found;
    }

    public static Drawing? Authored(string path, Random r)
    {
        var (model, _, _) = SchematicPersistence.LoadFromFile(path);
        model.SchematicDirectory = null;
        // A placed picture or a free text is not circuit, and nothing would label it: an example built from an image
        // keeps that image under its drawing, parts, words and all.
        model.CanvasObjects.Clear();
        var before = NetExtractor.Extract(model).TestBench;

        // Parts moved one grid step, their wires following: each wire end on a moved pin gets one more segment to
        // where the pin went. A move that changes the circuit (it lands a pin on something) is undone.
        int moves = r.Next(1, 4);
        var parts = model.Components.Where(x => x.Symbol != SymbolKind.Ground).ToList();
        for (int k = 0; k < moves && parts.Count > 0; k++)
        {
            var comp = parts[r.Next(parts.Count)];
            var (dx, dy) = r.Next(4) switch { 0 => (P, 0.0), 1 => (-P, 0.0), 2 => (0.0, P), _ => (0.0, -P) };
            var oldPins = Enumerable.Range(0, comp.PortCount).Select(comp.GetPortWorldCoord).ToList();
            var snapshot = model.Wires.Select(w => w.Points.ToList()).ToList();
            comp.X += dx;
            comp.Y += dy;
            foreach (var w in model.Wires)
            {
                if (w.Points.Count == 0) continue;
                if (oldPins.Any(p => Near(p, w.Points[0]))) w.Points.Insert(0, (w.Points[0].X + dx, w.Points[0].Y + dy));
                if (oldPins.Any(p => Near(p, w.Points[^1]))) w.Points.Add((w.Points[^1].X + dx, w.Points[^1].Y + dy));
            }
            if (!Netlists.Same(before, NetExtractor.Extract(model).TestBench, values: false, out _))
            {
                comp.X -= dx;
                comp.Y -= dy;
                for (int i = 0; i < model.Wires.Count; i++)
                {
                    model.Wires[i].Points.Clear();
                    model.Wires[i].Points.AddRange(snapshot[i]);
                }
            }
        }

        // Values re-drawn: a lumped part's value replaced by another in the same spelling family.
        foreach (var comp in model.Components)
        {
            if (r.NextDouble() > 0.6) continue;
            var p = comp.Parameters.FirstOrDefault(q => q.Name == comp.Symbol switch
            {
                SymbolKind.Resistor => "R", SymbolKind.Capacitor => "C", SymbolKind.Inductor => "L", _ => "",
            });
            if (p is null || !double.TryParse(p.Expression, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || v <= 0) continue;
            double nv = v * Math.Exp((r.NextDouble() - 0.5) * 2.0);
            p.Expression = (nv >= 100 ? Math.Round(nv) : Math.Round(nv, nv >= 10 ? 1 : 2)).ToString("0.##", CultureInfo.InvariantCulture);
        }

        var truth = NetExtractor.Extract(model).TestBench;
        if (!Netlists.Same(before, truth, values: false, out _)) return null;
        Dress(model, r, authored: true);
        return new Drawing(model, truth, DrawMode.Authored, Path.GetFileName(path));
    }

    // ── what a user leaves shown ─────────────────────────────────────────────

    internal static string PaletteUnit(string unit) => unit switch
    {
        "Ohm" => "Ω", "kOhm" => "kΩ", "MOhm" => "MΩ", "uH" => "µH", "um" => "µm", "uF" => "µF", _ => unit,
    };

    /// <summary>Some parameters shown and some hidden, as a user would leave them; now and then a type label hidden.</summary>
    private static void Dress(SchematicEditModel model, Random r, bool authored = false)
    {
        if (authored) return;
        double hideType = r.NextDouble() < 0.3 ? 0.5 : 0.0;
        foreach (var comp in model.Components)
        {
            if (comp.Symbol == SymbolKind.Ground) continue;
            if (r.NextDouble() < hideType) comp.ShowTypeLabel = false;
            if (r.NextDouble() < 0.04) comp.ShowInstanceName = false;
            bool palette = r.NextDouble() < 0.7;
            foreach (var p in comp.Parameters)
            {
                // The palette spells units Ω, kΩ, µH, µm; a netlist (and the reader) Ohm, kOhm, uH, um. A part placed
                // from the palette shows the first, one read from a netlist the second.
                if (palette) p.Unit = PaletteUnit(p.Unit);
                if (p.Name is "NumPorts") continue;
                if (p.ShowOnSchematic && r.NextDouble() < 0.12) p.ShowOnSchematic = false;
                else if (!p.ShowOnSchematic && p.Expression.Length is > 0 and < 14 && r.NextDouble() < 0.08) p.ShowOnSchematic = true;
            }
        }
    }
}

/// <summary>Whether a drawing's extraction is its circuit (R-iml2-6).</summary>
public static class Netlists
{
    /// <summary>
    /// The same instances by name and type, each pin on corresponding nets — one consistent renaming of nets, ground kept
    /// ground — and, with <paramref name="values"/>, the same parameters. Net names may differ: a drawing names an
    /// unlabelled net itself.
    /// </summary>
    public static bool Same(TestBench truth, TestBench drawn, bool values, out string why)
    {
        why = "";
        var d = drawn.Instances.ToDictionary(i => i.InstanceName, StringComparer.Ordinal);
        if (d.Count != truth.Instances.Count) { why = $"{truth.Instances.Count} instances against {d.Count}"; return false; }
        var fwd = new Dictionary<string, string>(StringComparer.Ordinal);
        var back = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var t in truth.Instances)
        {
            if (!d.TryGetValue(t.InstanceName, out var x)) { why = $"{t.InstanceName} is missing"; return false; }
            if (!string.Equals(t.Reference, x.Reference, StringComparison.OrdinalIgnoreCase)) { why = $"{t.InstanceName}: {t.Reference} against {x.Reference}"; return false; }
            if (t.NetBindings.Count != x.NetBindings.Count) { why = $"{t.InstanceName}: pin count"; return false; }
            for (int k = 0; k < t.NetBindings.Count; k++)
            {
                string a = t.NetBindings[k], b = x.NetBindings[k];
                if ((a == "0") != (b == "0")) { why = $"{t.InstanceName} pin {k + 1}: ground against {a}/{b}"; return false; }
                if (fwd.TryGetValue(a, out var fb) ? fb != b : back.TryGetValue(b, out var ba) && ba != a)
                {
                    why = $"{t.InstanceName} pin {k + 1}: net {a} is {b} here and elsewhere not";
                    return false;
                }
                fwd[a] = b;
                back[b] = a;
            }
            if (values)
            {
                string Ov(Instance i) => string.Join(";", i.Overrides.Select(o => $"{o.Name}={o.Expression}|{Drawings.PaletteUnit(o.Unit ?? "")}").Order(StringComparer.Ordinal));
                if (Ov(t) != Ov(x)) { why = $"{t.InstanceName}: {Ov(t)} against {Ov(x)}"; return false; }
            }
        }
        return true;
    }

    /// <summary>The netlist truth as written beside a picture.</summary>
    public static string Text(TestBench tb) => CnlWriter.Write(tb);
}
