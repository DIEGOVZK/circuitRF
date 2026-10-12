using System.Globalization;
using SkiaSharp;

namespace CircuitRF.SchematicSynth;

/// <summary>
/// What real pictures hold that is not circuit (brief-iml3 R-iml3-3), drawn and labelled so the reader learns to leave
/// it out: analysis, variable and directive blocks (<c>controller</c> parts, their words <c>other</c>); titles, figure
/// captions and notes (words <c>title</c>/<c>other</c>, boxes <c>title</c>/<c>other</c>); dashed sub-circuit frames and
/// DUT boxes (<c>frame</c>); current, voltage and dimension arrows (<c>arrow</c>); probes on a wire (<c>probe</c> parts);
/// a ring drawn round a part, a mouse cursor and a selection rectangle (<c>other</c>); a page border and title block
/// (<c>frame</c>). How often each appears is the pack's <c>distractors</c> table. Window chrome is
/// <see cref="ForeignShot"/>'s; two circuits in one picture are <see cref="SceneBuilder"/>'s.
///
/// <para>Every word here is generic: no tool's analysis names beyond the plain words for an analysis, no part numbers.</para>
/// </summary>
public static class Distractors
{
    private static readonly Dictionary<string, string[][]> ControllerRows = new(StringComparer.Ordinal)
    {
        ["S-PARAMETERS"] = [["SP1", "Start=1.0 GHz", "Stop=10.0 GHz", "Step=10 MHz"], ["SP1", "Start=0.5 GHz", "Stop=18 GHz", "Step=0.1 GHz", "CalcNoise=yes"]],
        ["s-parameter simulation"] = [["SP1", "Type=lin", "Start=1 GHz", "Stop=10 GHz", "Points=19"], ["SP1", "Type=log", "Start=100MHz", "Stop=10GHz", "Points=500"]],
        ["DC"] = [["DC1"], ["DC1", "SweepVar=\"Vgs\"", "Start=0 V", "Stop=2 V"]],
        ["AC"] = [["AC1", "Start=1 Hz", "Stop=100 kHz", "Points=101"]],
        ["TRANSIENT"] = [["TR1", "StopTime=5 usec", "MaxTimeStep=0.2 nsec"], ["Tran1", "StopTime=100 ns"]],
        ["transient simulation"] = [["TR1", "Type=lin", "Stop=100ns", "Points=25000"]],
        ["HARMONIC BALANCE"] = [["HB1", "Freq[1]=2.4 GHz", "Order[1]=7"], ["HB1", "Freq=900 MHz", "Order=5"]],
        ["VAR"] = [["VAR1", "Vbias=2", "Z0=50"], ["VAR2", "w1=0.5 mm", "l1=12 mm"]],
        ["equation"] = [["Eqn1", "r_vec=[1,2,3,4]", "r_now=r_vec[setup]"], ["Eqn2", "gm=0.13", "tau=2e-12"]],
        ["parameter sweep"] = [["SW1", "Sim=SP1", "Param=setup", "Type=lin", "Start=1", "Stop=4"]],
        ["SUBSTRATE"] = [["Sub1", "er=4.5", "h=0.3937 mm", "t=35 um", "tand=2e-4"], ["Sub2", "H=20 mil", "Er=2.94", "T=1.4 mil"]],
    };

    private static readonly string[] Directives =
    [
        ".tran 3", ".tran 0 1 0 .01 startup", ".tran 400", ".ac dec 100 100Hz 6GHz", ".ac oct 200 1MEG 150MEG", ".op",
        ".noise V(out) V1 oct 10 1K 100K", ".param tol=0.05", ".step param R 1k 10k 1k", ".model SW1 SW(Ron=1m Roff=10Meg Vt=2.5)",
        ".tran 0 10m 0", "AC 1 0", ".dc V1 0 5 0.1",
    ];

    private static readonly string[] Titles =
    [
        "Low-Pass Filter", "Matching Network", "Bias Tee", "2.4 GHz LNA", "Inverting Amplifier Circuit", "20 dB Pi Attenuator",
        "Test Circuit", "Pure Analog Filter", "Output Match", "Level conversion from high to low voltage", "Typical Application Circuit",
        "Evaluation Board Schematic", "RF Front End", "Balanced Amplifier", "Power Splitter",
    ];

    private static readonly string[] Captions =
    [
        "Schematic of the matching network.", "Simulated circuit.", "Proposed amplifier topology.", "Equivalent circuit model.",
        "Schematic of the test fixture.", "The bias network.", "Architecture of the filter.", "Circuit used for the measurements.",
    ];

    private static readonly string[][] Notes =
    [
        ["Notes:", "1. All capacitors 0402.", "2. Resistors 1%."], ["Unless otherwise noted:", "R in ohms, C in pF"],
        ["low-pass filter, 1GHz cutoff", "3rd order, PI-type,", "impedance 50 Ω"], ["Dielectric loss effect", "- Loss tangent 0.02"],
        ["To plot gain vs. frequency:", "add the measurement below"], ["DNP = do not place"],
    ];

    private static readonly string[] FrameNames = ["Bias network", "Input match", "Output filter", "1K Source Impedance", "Fixture", "Stage 1", "Matching"];

    public static void Add(Scene scene, Look look, Random r)
    {
        var pack = look.Pack;
        bool paper = pack.Medium != "screen";
        bool Roll(string what) => r.NextDouble() < pack.Chance(what);

        if (Roll("frame")) SubFrame(scene, look, r);
        if (Roll("dut")) Dut(scene, look, r);
        if (Roll("arrows")) Arrows(scene, look, r);
        if (Roll("probe")) Probe(scene, look, r);
        if (Roll("ring")) Ring(scene, look, r);
        if (Roll("controller"))
        {
            int n = r.Next(1, 4);
            for (int k = 0; k < n; k++) Controller(scene, look, r);
        }
        if (Roll("directive")) Directive(scene, look, r);
        if (Roll("title")) Title(scene, look, r);
        if (Roll("note")) Note(scene, look, r);
        if (paper && Roll("caption") || scene.Circuits.Count > 1 && Roll("caption")) Caption(scene, look, r);
        if (Roll("cursor")) Cursor(scene, look, r);
        if (Roll("selection")) Selection(scene, look, r);
        if (r.NextDouble() < pack.PageFrame) Page(scene, look, r, r.NextDouble() < pack.TitleBlock);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static SceneText Text(string s, string role, Look look, double scale, SKColor? colour = null, SKTypeface? face = null) =>
        SceneBuilder.Word(s, role, face ?? look.Face, look.TextSize * scale, colour ?? look.Text);

    private static (double X0, double Y0, double X1, double Y1) Box(IEnumerable<W> pts) => SceneBuilder.Bounds(pts);

    private static List<W> Rect(double x0, double y0, double x1, double y1) => [new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1)];

    /// <summary>A free spot of the given size in the margins round the circuit: below, above, left, right — corners first.</summary>
    private static (double X, double Y)? Free(Scene scene, double w, double h, Random r)
    {
        var c = scene.Extent();
        double g = 120;
        var spots = new List<(double, double)>
        {
            (c.X0, c.Y1 + g), (c.X1 - w, c.Y1 + g), (c.X0, c.Y0 - g - h), (c.X1 - w, c.Y0 - g - h),
            (c.X0 - g - w, c.Y0), (c.X1 + g, c.Y0), ((c.X0 + c.X1 - w) / 2, c.Y1 + g), (c.X0 - g - w, c.Y1 - h),
        };
        // Some pictures put a block inside the drawing's own empty space, as real canvases do.
        for (int k = 0; k < 6; k++) spots.Insert(r.Next(spots.Count + 1), (c.X0 + r.NextDouble() * Math.Max(1, c.X1 - c.X0 - w), c.Y0 + r.NextDouble() * Math.Max(1, c.Y1 - c.Y0 - h)));
        foreach (var (x, y) in spots)
            if (scene.Clear((x, y, x + w, y + h), air: 60) && !scene.TouchesWire((x - 80, y - 80, x + w + 80, y + h + 80))) return (x, y);
        return null;
    }

    private static void Lines(Scene scene, IReadOnlyList<SceneText> texts, double x, double y, double step)
    {
        for (int i = 0; i < texts.Count; i++)
        {
            texts[i].Origin = new W(x, y - texts[i].Metrics().Ascent + i * step);
            scene.Texts.Add(texts[i]);
        }
    }

    private static (double W, double H) Measure(IReadOnlyList<SceneText> texts, double step) =>
        (texts.Max(t => t.Metrics().Width), step * (texts.Count - 1) - texts[0].Metrics().Ascent + texts[^1].Metrics().Descent);

    // ── analysis, variable and directive blocks ──────────────────────────────

    private static void Controller(Scene scene, Look look, Random r)
    {
        var titles = look.Pack.ControllerTitles;
        string title = titles[r.Next(titles.Count)];
        var rows = ControllerRows.TryGetValue(title, out var options) ? options[r.Next(options.Length)] : ["SIM1"];
        var head = Text(title, "other", look, 0.9, look.Type);
        var (tw, ta, td) = head.Metrics();
        var g = look.Glyph("controller");
        // A box glyph may keep part of itself for an icon; the title fills the rest.
        double pad = look.TextSize * 0.45, il = g.Inset[0], ir = g.Inset[2];
        double bw = (tw + 2 * pad) / Math.Max(0.3, 1 - il - ir), bh = td - ta + 2 * pad;
        double step = look.TextSize * 1.15;
        var body = rows.Select(s => Text(s, "other", look, 1.0, look.Type)).ToList();
        var (rw, rh) = Measure(body, step);
        if (Free(scene, Math.Max(bw, rw), bh + 25 + rh, r) is not { } at) return;

        var part = new ScenePart { Id = "ctl" + scene.Parts.Count.ToString(CultureInfo.InvariantCulture), Class = "controller", Body = (at.X, at.Y, at.X + bw, at.Y + bh), Pins = [] };
        foreach (var st in g.Strokes)
            part.Inks.Add(new Ink(Layer.Symbol, st.Ink == "fill" ? look.Fill : look.Symbol, [.. st.Points.Select(q => new W(at.X + q.X * bw, at.Y + q.Y * bh))], st.Closed, st.Fill, st.Pen, 's', st.Dash));
        scene.Parts.Add(part);
        head.Origin = new W(at.X + il * bw + pad, at.Y + pad - ta);
        scene.Texts.Add(head);
        Lines(scene, body, at.X + pad * 0.5, at.Y + bh + 25, step);
    }

    private static void Directive(Scene scene, Look look, Random r)
    {
        var lines = Enumerable.Range(0, r.Next(1, 3)).Select(_ => Text(Directives[r.Next(Directives.Length)], "other", look, 1.0, look.Accent)).ToList();
        double step = look.TextSize * 1.2;
        var (w, h) = Measure(lines, step);
        if (Free(scene, w, h, r) is not { } at) return;
        Lines(scene, lines, at.X, at.Y, step);
        scene.AnnotationBoxes.Add(new SceneAnnotation((at.X, at.Y, at.X + w, at.Y + h), "other"));
    }

    // ── titles, captions, notes ──────────────────────────────────────────────

    private static void Title(Scene scene, Look look, Random r)
    {
        var t = Text(Titles[r.Next(Titles.Length)], "title", look, 1.2 + r.NextDouble() * 0.8, r.NextDouble() < 0.5 ? look.Accent : look.Text);
        var (w, a, d) = t.Metrics();
        var c = scene.Extent();
        double x = r.NextDouble() < 0.5 ? (c.X0 + c.X1 - w) / 2 : c.X0, y = c.Y0 - 150 - d;
        t.Origin = new W(x, y);
        if (!scene.Clear(t.Box(), air: 20)) return;
        scene.Texts.Add(t);
        scene.AnnotationBoxes.Add(new SceneAnnotation(t.Box(), "title"));
    }

    private static void Caption(Scene scene, Look look, Random r)
    {
        var face = look.Face;
        if (scene.Circuits.Count > 1)
        {
            // (a) and (b) under each circuit.
            double y = scene.Circuits.Max(b => b.Y1) + 200;
            for (int k = 0; k < scene.Circuits.Count; k++)
            {
                var b = scene.Circuits[k];
                var t = Text($"({(char)('a' + k)})", "title", look, 1.1, face: face);
                var (w, a, _) = t.Metrics();
                t.Origin = new W((b.X0 + b.X1 - w) / 2, Math.Max(y, b.Y1 + 200) - a);
                if (!scene.Clear(t.Box(), air: 10)) continue;
                scene.Texts.Add(t);
                scene.AnnotationBoxes.Add(new SceneAnnotation(t.Box(), "title"));
            }
            return;
        }
        string s = r.NextDouble() < 0.3 ? $"({(char)('a' + r.Next(4))})" : $"Fig. {r.Next(1, 12)}. {Captions[r.Next(Captions.Length)]}";
        var cap = Text(s, "title", look, 1.1);
        var (cw, ca, _) = cap.Metrics();
        var c = scene.Extent();
        cap.Origin = new W((c.X0 + c.X1 - cw) / 2, c.Y1 + 180 - ca);
        scene.Texts.Add(cap);
        scene.AnnotationBoxes.Add(new SceneAnnotation(cap.Box(), "title"));
    }

    private static void Note(Scene scene, Look look, Random r)
    {
        var rows = Notes[r.Next(Notes.Length)].Select(s => Text(s, "other", look, 0.9, r.NextDouble() < 0.5 ? look.Text : look.Type)).ToList();
        double step = look.TextSize * 1.1;
        var (w, h) = Measure(rows, step);
        if (Free(scene, w, h, r) is not { } at) return;
        Lines(scene, rows, at.X, at.Y, step);
        scene.AnnotationBoxes.Add(new SceneAnnotation((at.X, at.Y, at.X + w, at.Y + h), "other"));
    }

    // ── frames ───────────────────────────────────────────────────────────────

    private static void SubFrame(Scene scene, Look look, Random r)
    {
        var parts = scene.Parts.Where(p => p.Pins.Count > 0).ToList();
        if (parts.Count < 2) return;
        var seed = parts[r.Next(parts.Count)];
        double sx = (seed.Body.X0 + seed.Body.X1) / 2, sy = (seed.Body.Y0 + seed.Body.Y1) / 2;
        var group = parts.Where(p => Math.Abs((p.Body.X0 + p.Body.X1) / 2 - sx) < 450 && Math.Abs((p.Body.Y0 + p.Body.Y1) / 2 - sy) < 450).Take(r.Next(2, 5)).ToList();
        var b = Box(group.SelectMany(p => new[] { new W(p.Body.X0, p.Body.Y0), new W(p.Body.X1, p.Body.Y1) }));
        double m = 80;
        var rect = Rect(b.X0 - m, b.Y0 - m, b.X1 + m, b.Y1 + m);
        float[] dash = r.NextDouble() < 0.7 ? [20, 14] : [6, 8];
        scene.Annotations.Add(new Ink(Layer.Annotation, r.NextDouble() < 0.5 ? look.Symbol : look.Accent, rect, true, false, 0.8, 'a', dash));
        var box = (b.X0 - m, b.Y0 - m, b.X1 + m, b.Y1 + m);
        if (r.NextDouble() < 0.6)
        {
            var t = Text(FrameNames[r.Next(FrameNames.Length)], "other", look, 0.9, look.Accent);
            t.Origin = new W(box.Item1 + 20, box.Item4 - 20);
            scene.Texts.Add(t);
        }
        scene.AnnotationBoxes.Add(new SceneAnnotation(box, "frame"));
    }

    private static void Dut(Scene scene, Look look, Random r)
    {
        var parts = scene.Parts.Where(p => p.Pins.Count >= 2).ToList();
        if (parts.Count == 0) return;
        var p = parts.FirstOrDefault(x => x.Class is "amplifier" or "block" or "ic-box" or "op-amp" or "filter") ?? parts[r.Next(parts.Count)];
        double m = 60;
        var box = (p.Body.X0 - m, p.Body.Y0 - m, p.Body.X1 + m, p.Body.Y1 + m);
        scene.Annotations.Add(new Ink(Layer.Annotation, look.Symbol, Rect(box.Item1, box.Item2, box.Item3, box.Item4), true, false, 1, 'a', r.NextDouble() < 0.5 ? [16, 10] : null));
        var t = Text(r.NextDouble() < 0.7 ? "DUT" : "Device", "other", look, 0.9);
        t.Origin = new W(box.Item1 + 10, box.Item2 - 15);
        scene.Texts.Add(t);
        scene.AnnotationBoxes.Add(new SceneAnnotation(box, "frame"));
    }

    private static void Page(Scene scene, Look look, Random r, bool titleBlock)
    {
        var c = scene.Extent();
        double m = 200 + r.NextDouble() * 200;
        double x0 = c.X0 - m, y0 = c.Y0 - m, x1 = c.X1 + m, y1 = c.Y1 + m;
        if (titleBlock)
        {
            // The block in the bottom-right corner, the frame grown to hold it.
            var cells = new[] { "Title", $"Sheet 1 of {r.Next(1, 4)}", $"Rev {(char)('A' + r.Next(4))}", "Size A4", $"Date: 20{r.Next(10, 27)}-0{r.Next(1, 10)}-1{r.Next(0, 10)}" };
            var texts = cells.Select(s => Text(s, s == "Title" ? "title" : "other", look, 0.8)).ToList();
            double step = look.TextSize * 1.4, bw = texts.Max(t => t.Metrics().Width) + 80, bh = step * texts.Count + 20;
            y1 += bh + 60;
            double tx = x1 - bw, ty = y1 - bh;
            scene.Annotations.Add(new Ink(Layer.Annotation, look.Symbol, Rect(tx, ty, x1, y1), true, false, 1, 'a', null));
            for (int k = 1; k < texts.Count; k++)
                scene.Annotations.Add(new Ink(Layer.Annotation, look.Symbol, [new W(tx, ty + k * step + 10), new W(x1, ty + k * step + 10)], false, false, 0.7, 'a', null));
            Lines(scene, texts, tx + 30, ty + 18, step);
            scene.AnnotationBoxes.Add(new SceneAnnotation((tx, ty, x1, y1), "frame"));
        }
        scene.Annotations.Add(new Ink(Layer.Annotation, look.Symbol, Rect(x0, y0, x1, y1), true, false, 1.5, 'a', null));
        // The border is a thin frame, not a filled region: its four sides are its annotations.
        double e = 15;
        foreach (var b in new[] { (x0 - e, y0 - e, x1 + e, y0 + e), (x0 - e, y1 - e, x1 + e, y1 + e), (x0 - e, y0 - e, x0 + e, y1 + e), (x1 - e, y0 - e, x1 + e, y1 + e) })
            scene.AnnotationBoxes.Add(new SceneAnnotation(b, "frame"));
    }

    // ── arrows ───────────────────────────────────────────────────────────────

    private static void Arrows(Scene scene, Look look, Random r)
    {
        var segs = scene.WirePaths.SelectMany(w => w.Zip(w.Skip(1))).Where(s => Math.Abs(s.First.X - s.Second.X) + Math.Abs(s.First.Y - s.Second.Y) > 300).ToList();
        if (segs.Count > 0)
        {
            var (a, b) = segs[r.Next(segs.Count)];
            bool horizontal = Math.Abs(a.Y - b.Y) < 1;
            double mx = (a.X + b.X) / 2, my = (a.Y + b.Y) / 2, len = 120, off = 55 * (r.NextDouble() < 0.5 ? 1 : -1);
            int dir = r.NextDouble() < 0.5 ? 1 : -1;
            W p0 = horizontal ? new W(mx - dir * len / 2, my + off) : new W(mx + off, my - dir * len / 2);
            W p1 = horizontal ? new W(mx + dir * len / 2, my + off) : new W(mx + off, my + dir * len / 2);
            Arrow(scene, look, p0, p1, filled: true);
            var t = Text(new[] { "I1", "ID", "IDS", "Ic", "I" }[r.Next(5)], "other", look, 0.9, look.Accent);
            var (w, ta, _) = t.Metrics();
            t.Origin = horizontal ? new W(mx - w / 2, my + off + (off > 0 ? -ta + 15 : -15)) : new W(mx + off + (off > 0 ? 15 : -15 - w), my - ta / 2);
            if (scene.Clear(t.Box(), air: 5)) scene.Texts.Add(t);
            scene.AnnotationBoxes.Add(new SceneAnnotation(Box([p0, p1]) is var bb ? (bb.X0 - 20, bb.Y0 - 20, bb.X1 + 20, bb.Y1 + 20) : default, "arrow"));
        }
        if (r.NextDouble() < 0.5)
        {
            // A voltage or dimension arrow beside the drawing, double-headed, with its words.
            var c = scene.Extent(false);
            double x = r.NextDouble() < 0.5 ? c.X0 - 150 : c.X1 + 150, y0 = c.Y0 + (c.Y1 - c.Y0) * 0.2, y1 = c.Y0 + (c.Y1 - c.Y0) * 0.8;
            if (y1 - y0 < 150) return;
            var p0 = new W(x, y0); var p1 = new W(x, y1);
            if (!scene.Clear((x - 30, y0, x + 30, y1), air: 10)) return;
            Arrow(scene, look, p0, p1, filled: r.NextDouble() < 0.6);
            if (r.NextDouble() < 0.6) Arrow(scene, look, p1, p0, filled: true, headOnly: true);
            var t = Text(new[] { "Vout", "Vin", "V1", "Vref = 1.25 V", "VDS" }[r.Next(5)], "other", look, 0.9);
            var (w, ta, _) = t.Metrics();
            t.Origin = new W(x + 25, (y0 + y1) / 2 - ta / 2);
            if (scene.Clear(t.Box(), air: 5)) scene.Texts.Add(t);
            scene.AnnotationBoxes.Add(new SceneAnnotation((x - 25, y0, x + 25, y1), "arrow"));
        }
    }

    private static void Arrow(Scene scene, Look look, W p0, W p1, bool filled, bool headOnly = false)
    {
        if (!headOnly) scene.Annotations.Add(new Ink(Layer.Annotation, look.Accent, [p0, p1], false, false, 1, 'a', null));
        var head = Glyph.Head(p0.X, p0.Y, p1.X, p1.Y, 30).Select(p => new W(p.X, p.Y)).ToList();
        scene.Annotations.Add(filled
            ? new Ink(Layer.Annotation, look.Accent, head, true, true, 1, 'a', null)
            : new Ink(Layer.Annotation, look.Accent, [head[1], head[0], head[2]], false, false, 1, 'a', null));
    }

    // ── a probe on a wire ────────────────────────────────────────────────────

    private static void Probe(Scene scene, Look look, Random r)
    {
        var segs = scene.WirePaths.SelectMany(w => w.Zip(w.Skip(1))).Where(s => Math.Abs(s.First.Y - s.Second.Y) < 1 && Math.Abs(s.First.X - s.Second.X) > 300).ToList();
        if (segs.Count == 0) return;
        var (a, b) = segs[r.Next(segs.Count)];
        var at = new W(Math.Round((a.X + b.X) / 200) * 100, a.Y);
        var g = look.Glyph("probe");
        var gp = g.Pins["1"];
        int turn = r.NextDouble() < 0.6 ? 0 : 180;
        W Map(double x, double y) { var t = SceneBuilder.Turn(new W(x - gp.X, y - gp.Y), turn); return new W(at.X + t.X, at.Y + t.Y); }
        var c0 = Map(g.Body.MinX, g.Body.MinY); var c1 = Map(g.Body.MaxX, g.Body.MaxY);
        var body = (Math.Min(c0.X, c1.X), Math.Min(c0.Y, c1.Y), Math.Max(c0.X, c1.X), Math.Max(c0.Y, c1.Y));
        if (!scene.Clear(body, air: 20)) return;
        var part = new ScenePart { Id = "probe" + scene.Parts.Count.ToString(CultureInfo.InvariantCulture), Class = "probe", Turn = turn, Body = body, Pins = [("1", at)] };
        foreach (var st in g.Strokes)
            part.Inks.Add(new Ink(Layer.Symbol, st.Ink == "fill" ? look.Fill : look.Symbol, [.. st.Points.Select(q => Map(q.X, q.Y))], st.Closed, st.Fill, st.Pen, 's', st.Dash));
        scene.Parts.Add(part);
        var t = Text(r.NextDouble() < 0.5 ? "Probe1" : "I_meas", "other", look, 0.8, look.Type);
        t.Origin = new W(body.Item3 + 20, (body.Item2 + body.Item4) / 2);
        if (scene.Clear(t.Box(), air: 5)) scene.Texts.Add(t);
    }

    // ── marks a person adds ──────────────────────────────────────────────────

    private static void Ring(Scene scene, Look look, Random r)
    {
        var parts = scene.Parts.Where(p => p.Pins.Count > 0).ToList();
        if (parts.Count == 0) return;
        var p = parts[r.Next(parts.Count)];
        double cx = (p.Body.X0 + p.Body.X1) / 2, cy = (p.Body.Y0 + p.Body.Y1) / 2;
        double rx = (p.Body.X1 - p.Body.X0) / 2 + 80, ry = (p.Body.Y1 - p.Body.Y0) / 2 + 80;
        var pts = Enumerable.Range(0, 64).Select(k => new W(cx + rx * Math.Cos(k * Math.PI / 32), cy + ry * Math.Sin(k * Math.PI / 32))).ToList();
        scene.Annotations.Add(new Ink(Layer.Annotation, look.Accent, pts, true, false, 1.6, 'a', r.NextDouble() < 0.6 ? [10, 8] : null));
        scene.AnnotationBoxes.Add(new SceneAnnotation((cx - rx - 10, cy - ry - 10, cx + rx + 10, cy + ry + 10), "other"));
    }

    private static void Cursor(Scene scene, Look look, Random r)
    {
        var c = scene.Extent();
        double x = c.X0 + r.NextDouble() * (c.X1 - c.X0), y = c.Y0 + r.NextDouble() * (c.Y1 - c.Y0), s = 60 + r.NextDouble() * 40;
        // A pointer: a tall triangle with a short tail, outlined.
        var shape = new List<W> { new(x, y), new(x, y + s), new(x + s * 0.28, y + s * 0.76), new(x + s * 0.48, y + s * 1.12), new(x + s * 0.6, y + s * 1.06), new(x + s * 0.42, y + s * 0.7), new(x + s * 0.72, y + s * 0.7) };
        var white = new SKColor(255, 255, 255);
        scene.Overlays.Add(new Ink(Layer.Annotation, r.NextDouble() < 0.7 ? white : new SKColor(0, 0, 0), shape, true, true, 1, 'a', null));
        scene.Overlays.Add(new Ink(Layer.Annotation, new SKColor(0, 0, 0), shape, true, false, 0.8, 'a', null));
        scene.AnnotationBoxes.Add(new SceneAnnotation(Box(shape) is var b ? (b.X0 - 5, b.Y0 - 5, b.X1 + 5, b.Y1 + 5) : default, "other"));
    }

    private static void Selection(Scene scene, Look look, Random r)
    {
        var c = scene.Extent(false);
        double x0 = c.X0 + r.NextDouble() * (c.X1 - c.X0) * 0.6, y0 = c.Y0 + r.NextDouble() * (c.Y1 - c.Y0) * 0.6;
        double x1 = x0 + (c.X1 - c.X0) * (0.15 + r.NextDouble() * 0.35), y1 = y0 + (c.Y1 - c.Y0) * (0.15 + r.NextDouble() * 0.35);
        var rect = Rect(x0, y0, x1, y1);
        scene.Overlays.Add(new Ink(Layer.None, new SKColor(60, 120, 255, 40), rect, true, true, 0, 'a', null));
        scene.Overlays.Add(new Ink(Layer.Annotation, new SKColor(60, 120, 255), rect, true, false, 0.7, 'a', [8, 6]));
        double e = 10;
        foreach (var b in new[] { (x0 - e, y0 - e, x1 + e, y0 + e), (x0 - e, y1 - e, x1 + e, y1 + e), (x0 - e, y0 - e, x0 + e, y1 + e), (x1 - e, y0 - e, x1 + e, y1 + e) })
            scene.AnnotationBoxes.Add(new SceneAnnotation(b, "other"));
    }
}
