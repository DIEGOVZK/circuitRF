namespace CircuitRF.SchematicSynth;

/// <summary>One thing a label check found.</summary>
public sealed record LabelFinding(bool Error, string Message)
{
    public override string ToString() => (Error ? "error: " : "warning: ") + Message;
}

/// <summary>
/// The structural checks <c>crfml.validate</c> makes (IML-1 §2.2), ported for the generator: it runs them on every
/// picture before writing it, and <c>SchematicSynthTests</c> runs them with no Python. The Python validator stays the
/// reference — this port is held to it by checking the same things in the same words — and it closes up a value's
/// spaces before reading it, as <see cref="ValueText"/> says.
///
/// <para>Two checks the Python validator makes against the PICTURE are made here against the symbol and wire masks,
/// which a generator has and a real picture does not: a pin is on ink of the symbol it belongs to (or at a wire end),
/// not merely on some ink.</para>
/// </summary>
public static class LabelChecks
{
    /// <param name="symbolMask">The symbol mask at the picture's size, one byte a pixel, or null to skip the ink checks.</param>
    public static IReadOnlyList<LabelFinding> Check(MlLabel label, byte[]? symbolMask = null)
    {
        var found = new List<LabelFinding>();
        void Err(string m) => found.Add(new LabelFinding(true, m));
        void Warn(string m) => found.Add(new LabelFinding(false, m));

        int W = label.Width, H = label.Height;
        double pen = label.Wires.Count > 0 ? Median(label.Wires.Select(w => w.Pen)) : 2.0;
        double reach = Math.Max(pen, 1.5) + 1.0;

        var texts = new Dictionary<string, LabelText>(StringComparer.Ordinal);
        foreach (var t in label.Texts)
        {
            if (!texts.TryAdd(t.Id, t)) Err($"text id {t.Id} is used twice");
            if (t.Quad.Any(q => q.X < -0.5 || q.Y < -0.5 || q.X > W + 0.5 || q.Y > H + 0.5))
                Err($"text {t.Id} ({t.String}) runs off the picture");
            if (t.String.FirstOrDefault(ch => !ClassList.Charset.Contains(ch)) is var bad and not '\0')
                Err($"text {t.Id} ({t.String}) draws '{bad}', which the text net cannot output");
        }

        var wireEnds = label.Wires.SelectMany(w => new[] { w.Points[0], w.Points[^1] }).ToList();
        var allPins = new List<Px>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in label.Parts)
        {
            if (!ids.Add(p.Id)) Err($"part id {p.Id} is used twice");
            if (!ClassList.Classes.TryGetValue(p.Class, out var cls))
            {
                Err($"part {p.Id}: class {p.Class} is not in classes.json");
                continue;
            }
            if (p.Variant is { } v && !cls.Variants.Contains(v)) Err($"part {p.Id}: {p.Class} has no variant {v}");
            var b = p.Body;
            if (!(b[0] < b[2] && b[1] < b[3])) Err($"part {p.Id}: body [{string.Join(", ", b)}] is empty or inverted");
            if (!p.Cut && (b[0] < -0.5 || b[1] < -0.5 || b[2] > W + 0.5 || b[3] > H + 0.5))
                Err($"part {p.Id}: body runs off the picture and the part is not marked cut");

            var names = p.Pins.Select(q => q.Name).ToList();
            if (cls.Pins is null)
            {
                if (names.Count < 2 && !p.Cut) Err($"part {p.Id}: a {p.Class} has pins round its perimeter, at least 2; this one has {names.Count}");
                if (names.Distinct(StringComparer.Ordinal).Count() != names.Count) Err($"part {p.Id}: pin names repeat");
            }
            else if (!names.Order(StringComparer.Ordinal).SequenceEqual(cls.Pins.Order(StringComparer.Ordinal)))
                Err($"part {p.Id}: a {p.Class}'s pins are {string.Join(", ", cls.Pins)}; this one's are {string.Join(", ", names)}");

            foreach (var q in p.Pins)
            {
                allPins.Add(q.At);
                bool inPicture = q.At.X >= 0 && q.At.Y >= 0 && q.At.X <= W - 1 && q.At.Y <= H - 1;
                if (!inPicture)
                {
                    if (!p.Cut) Err($"part {p.Id}: pin {q.Name} is off the picture and the part is not marked cut");
                    continue;
                }
                if (symbolMask is null || p.Cut) continue;
                bool onInk = NearInk(symbolMask, W, H, q.At, reach);
                bool onEnd = wireEnds.Any(e => Dist(e, q.At) <= reach);
                if (!onInk && !onEnd) Err($"part {p.Id}: pin {q.Name} at ({q.At.X:0.#}, {q.At.Y:0.#}) is not on its symbol's ink or a wire end");
            }

            foreach (var refId in (p.Designator is { } d ? [d] : Array.Empty<string>()).Concat(p.Values))
                if (!texts.ContainsKey(refId)) Err($"part {p.Id}: text {refId} does not exist");
            foreach (var refId in p.Values)
                if (texts.TryGetValue(refId, out var t) && !ValueText.Reads(t.String))
                    Err($"part {p.Id}: value text {refId} ({t.String}) does not read as a value");
        }

        for (int wi = 0; wi < label.Wires.Count; wi++)
        {
            var w = label.Wires[wi];
            if (w.Points.Count < 2) { Err($"wire {wi}: fewer than two points"); continue; }
            foreach (var end in new[] { w.Points[0], w.Points[^1] })
            {
                if (Math.Min(end.X, end.Y) <= reach || end.X >= W - 1 - reach || end.Y >= H - 1 - reach) continue;
                // A screenshot's canvas ends at its window chrome, inside the picture.
                if (label.Annotations.Any(a => a.Kind == "chrome" && a.Box[0] - reach <= end.X && end.X <= a.Box[2] + reach
                                               && a.Box[1] - reach <= end.Y && end.Y <= a.Box[3] + reach)) continue;
                if (allPins.Any(p => Dist(p, end) <= reach) || label.Junctions.Any(j => Dist(j, end) <= reach)) continue;
                if (label.Wires.Where((_, oi) => oi != wi).Any(o => DistToPolyline(end, o.Points) <= reach)) continue;
                Warn($"wire {wi}: its end at ({end.X:0.#}, {end.Y:0.#}) reaches no pin, junction, other wire, picture edge or chrome");
            }
        }
        return found;
    }

    private static double Median(IEnumerable<double> xs)
    {
        var l = xs.Order().ToList();
        return l.Count % 2 == 1 ? l[l.Count / 2] : (l[l.Count / 2 - 1] + l[l.Count / 2]) / 2;
    }

    private static bool NearInk(byte[] mask, int w, int h, Px p, double r)
    {
        int x0 = Math.Max(0, (int)Math.Floor(p.X - r)), x1 = Math.Min(w - 1, (int)Math.Ceiling(p.X + r));
        int y0 = Math.Max(0, (int)Math.Floor(p.Y - r)), y1 = Math.Min(h - 1, (int)Math.Ceiling(p.Y + r));
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
                if (mask[y * w + x] != 0 && (x - p.X) * (x - p.X) + (y - p.Y) * (y - p.Y) <= r * r) return true;
        return false;
    }

    private static double Dist(Px a, Px b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static double DistToPolyline(Px p, IReadOnlyList<Px> pts)
    {
        double best = double.MaxValue;
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            var (a, b) = (pts[i], pts[i + 1]);
            double dx = b.X - a.X, dy = b.Y - a.Y, len = dx * dx + dy * dy;
            double t = len == 0 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len, 0, 1);
            best = Math.Min(best, Dist(p, new Px(a.X + t * dx, a.Y + t * dy)));
        }
        return best;
    }
}
