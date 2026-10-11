// The picture's own scales — brief-imf-2-picture-scale-profile.md R-imf2-1 … R-imf2-3, R-imf2-5 (overview F1, L5).
//
// Every tolerance of the schematic reader was stated in one unit, the pen w. That is right for wire geometry — a
// residual, a dot, how near an end must come to count as reaching something — and wrong for anything the DRAWING sizes:
// a symbol's stroke, a label's distance, a glyph's height. A heavy pen (w = 8 px) draws its symbols 10–20 w long; a
// datasheet hairline at 1× draws them 10 w long with 4 w type, and at 2× the same pen is half as many w of the same
// drawing. So the picture is measured first, and each tolerance takes the unit it is about:
//
//   w  the pen          — the run-width mode (design note §9.1) the line work is read at, as before; the profile
//                         reports the pen as DRAWN, the ink summed across each stroke (a 1.1 px hairline binarises 2)
//   h  the text height  — the median height of glyph-shaped pieces that stand in rows of two or more
//   s  the symbol scale — the median length of the straight runs that end in bare paper at both ends (a plate, a bar)
//                         or are the sides of a small closed outline (a resistor's box, a triangle); 3 h with too few
//   g  the drawing grid — PictureGrids' canvas grid, else the modal spacing of parallel wires (reported only)
//
// THE REFERENCE PROFILE. Every default in SchematicImageOptions was measured on pictures whose text stands 5–7.5 w tall
// and whose symbol scale is 9–26 w — circuitRF's own schematics at every zoom the gates draw them at, its editor
// screenshot, the IEC drawing — so each option keeps its number, stated in w AT that profile, and is multiplied by its
// class's factor: 1 inside the reference band, and the picture's own ratio over the band's nearer edge outside it. A
// picture inside the band reads with exactly the defaults it always had; one outside scales with its unit.
//
// LOW RESOLUTION. A 1× screenshot of a datasheet has a 1–2 px hairline and 7–9 px type: too few pixels for a glyph's
// skeleton or a plate's gap. Such a picture — its pen as drawn under 1.6 px (the brief's "w ≤ 2 px": every such pen
// binarises 2 px wide, while a crisp 2 px pen with 14 px type, which this repository's own test pictures draw, reads as
// it is), or its type under 12 px; never a hairline drawing the IM-12 thickening already brings to the reference — is
// read enlarged ×2 or ×3 (Lanczos-3, Resample.cs), and everything reported is mapped back to the file's pixels
// (SchematicImageRead.ToSource).

using System.Globalization;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Layout.Recognition.Silkscreen;

namespace CircuitRF.Design.Schematic.Recognition;

public enum PictureResolution { Normal, Low }

/// <summary>How one of the picture's scales was found.</summary>
public enum ProfileBasis
{
    /// <summary>Measured from the picture.</summary>
    Measured,

    /// <summary>Stated by the user (R-imf2-5): it replaces the measurement.</summary>
    Stated,

    /// <summary>Too little to measure it on; derived from another scale.</summary>
    Derived,
}

/// <summary>One of a picture's scales, in the SOURCE picture's pixels, with how it was found.</summary>
/// <param name="Samples">How many pieces it was measured over (0 when stated or derived).</param>
public readonly record struct ProfileScale(double Px, int Samples, ProfileBasis Basis)
{
    public static ProfileScale Stated(double px) => new(px, 0, ProfileBasis.Stated);
}

/// <summary>Scales the user states instead of the measured ones (R-imf2-5), source pixels; null measures it.</summary>
public sealed record PictureProfileOptions
{
    public static PictureProfileOptions Auto { get; } = new();

    /// <summary>The pen w.</summary>
    public double? Pen { get; init; }

    /// <summary>The text cap height h.</summary>
    public double? TextHeight { get; init; }

    /// <summary>The symbol scale s.</summary>
    public double? SymbolScale { get; init; }

    public bool IsAuto => Pen is null && TextHeight is null && SymbolScale is null;
}

/// <summary>The scales a schematic picture was read at (R-imf2-1), and the factors its tolerances took (R-imf2-2).</summary>
public sealed record PictureProfile
{
    // ── The reference profile: the band every default was measured in (design note §18.2) ───────────────────────

    /// <summary>Text standing 5–7.5 w tall: circuitRF's hairline drawings once thickened (IM-12's 5 w), its renders at
    /// the gates' zooms and the IEC drawing.</summary>
    public const double ReferenceTextLow = 5.0, ReferenceTextHigh = 7.5;

    /// <summary>A symbol scale of 9–26 w, on the same pictures: circuitRF's renders measure 9.2–9.5, the IEC drawing
    /// 11.8–14.8 and the editor screenshot 23.4.</summary>
    public const double ReferenceSymbolLow = 9.0, ReferenceSymbolHigh = 26.0;

    /// <summary>A picture is Low below these (R-imf2-3): a pen drawn thinner than 1.6 px — which binarises 2 px wide
    /// whatever it was, so its weight is lost — or type under 12 px. A crisp 2 px pen with 14 px type reads as it is.</summary>
    public const double LowPen = 1.6, LowText = 12.0;

    /// <summary>… and is read enlarged until its pen and its text reach these, by ×2 or ×3.</summary>
    public const double TargetPen = 3.0, TargetText = 18.0;

    /// <summary>A picture is never enlarged past this many pixels: ×3 of a dense 1100 × 605 evaluation board (6 MP)
    /// took 8.8 s to read in a Debug build, ×2 (2.7 MP) a third of that — the time a paste has (overview O8).</summary>
    public const long MaxResampledPixels = 4_000_000;

    /// <summary>At least this many plates, bars and sides measure s; with fewer, s = 3 h.</summary>
    public const int MinSymbolSamples = 2;

    /// <summary>s with too few symbol strokes to measure it, in text heights.</summary>
    public const double SymbolInText = 3.0;

    // ── The scales ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The pen w.</summary>
    public ProfileScale Pen { get; init; }

    /// <summary>The text cap height h.</summary>
    public ProfileScale Text { get; init; }

    /// <summary>The text was measured on every glyph, not rows of them: the picture has too few rows.</summary>
    public bool TextAlone { get; init; }

    /// <summary>The symbol scale s.</summary>
    public ProfileScale Symbol { get; init; }

    /// <summary>The drawing grid g; null when the picture shows none and has too few parallel wires.</summary>
    public ProfileScale? Grid { get; init; }

    /// <summary>The grid is the canvas's (PictureGrids) rather than the wires' spacing.</summary>
    public bool GridFromCanvas { get; init; }

    public PictureResolution Resolution { get; init; }

    /// <summary>How many times larger the picture was read (1: as it is).</summary>
    public int Resample { get; init; } = 1;

    /// <summary>The multiplier a text tolerance took (1: the defaults as stated).</summary>
    public double TextFactor { get; init; } = 1;

    /// <summary>The multiplier a symbol tolerance took.</summary>
    public double SymbolFactor { get; init; } = 1;

    /// <summary>The factor of a scale standing <paramref name="ratio"/> w: 1 within [low, high], else the ratio over the
    /// nearer edge.</summary>
    public static double Factor(double ratio, double low, double high) =>
        !(ratio > 0) ? 1 : ratio < low ? ratio / low : ratio > high ? ratio / high : 1;

    /// <summary><paramref name="o"/> with every symbol and text tolerance multiplied by its factor (R-imf2-2): the
    /// options the reading uses. Pen tolerances, and everything else, are unchanged.</summary>
    public SchematicImageOptions Apply(SchematicImageOptions o)
    {
        double t = TextFactor, s = SymbolFactor;
        if (t == 1 && s == 1) return o;
        return o with
        {
            MinTextHeight = o.MinTextHeight * t, MaxTextHeight = o.MaxTextHeight * t, TextContact = o.TextContact * t,
            LabelGap = o.LabelGap * t,
            MaxSymbolStroke = o.MaxSymbolStroke * s, MinDiagonalLength = o.MinDiagonalLength * s, Grow = o.Grow * s,
            MaxHopSpan = o.MaxHopSpan * s,
        };
    }

    /// <summary>The profile in words, one scale per line (R-imf2-1): the report's and the overlay's.</summary>
    public IReadOnlyList<string> Lines()
    {
        static string N(double v) => v.ToString(v < 10 ? "0.#" : "0", CultureInfo.InvariantCulture);
        static string How(ProfileScale p, string measured) => p.Basis switch
        {
            ProfileBasis.Stated => "stated",
            ProfileBasis.Derived => measured,
            _ => measured,
        };
        var r = new List<string>
        {
            $"Pen {N(Pen.Px)} px ({How(Pen, $"the ink across the stroke, the mode over {Pen.Samples} skeleton pixels")}).",
            Text.Basis == ProfileBasis.Derived
                ? $"Text height {N(Text.Px)} px (no rows of glyphs; taken as {N(ReferenceTextLow)} pens)."
                : $"Text height {N(Text.Px)} px ({How(Text, TextAlone ? $"the upper quartile of {Text.Samples} glyphs, too few of them in rows"
                                                                      : $"the median of {Text.Samples} glyphs in rows")}).",
            Symbol.Basis == ProfileBasis.Derived
                ? $"Symbol scale {N(Symbol.Px)} px ({N(SymbolInText)} text heights: {Symbol.Samples} plate, bar or box side{(Symbol.Samples == 1 ? "" : "s")} to measure on)."
                : $"Symbol scale {N(Symbol.Px)} px ({How(Symbol, $"the median of {Symbol.Samples} plates, bars and box sides")}).",
        };
        if (Grid is { } g)
            r.Add(GridFromCanvas ? $"Drawing grid {N(g.Px)} px (the canvas grid)."
                                 : $"Drawing grid {N(g.Px)} px (the modal spacing of {g.Samples} pairs of parallel wires).");
        if (Resample > 1)
            r.Add($"Low resolution: read enlarged ×{Resample.ToString(CultureInfo.InvariantCulture)}, every position mapped back to the picture's pixels.");
        if (TextFactor != 1 || SymbolFactor != 1)
            r.Add("Tolerances scaled: " + string.Join(", ",
                new[] { TextFactor != 1 ? $"text ×{TextFactor.ToString("0.##", CultureInfo.InvariantCulture)}" : null,
                        SymbolFactor != 1 ? $"symbol ×{SymbolFactor.ToString("0.##", CultureInfo.InvariantCulture)}" : null }
                    .Where(x => x is not null)) + ".");
        return r;
    }

    // ── Measuring ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The resample factor a picture of pen <paramref name="w"/> and text <paramref name="h"/> (pixels) is read
    /// at, and whether it is Low; <paramref name="hairline"/>: a drawing the IM-12 thickening brings to the reference.</summary>
    /// <summary><paramref name="k"/> raised by <paramref name="steps"/>, no further than <see cref="MaxResampledPixels"/>
    /// allows for a picture of <paramref name="pixels"/> (IMF-9's enlarged candidate reading).</summary>
    internal static int Boosted(int k, int steps, long pixels)
    {
        int b = k + steps;
        while (b > k && pixels * b * b > MaxResampledPixels) b--;
        return b;
    }

    internal static (PictureResolution Resolution, int Factor) Resolve(double w, double h, bool hairline, long pixels)
    {
        bool low = (w < LowPen && !hairline) || (h > 0 && h < LowText);
        if (!low) return (PictureResolution.Normal, 1);
        int k = 2;
        if ((w > 0 && k * w < TargetPen) || (h > 0 && k * h < TargetText)) k = 3;
        while (k > 1 && pixels * k * k > MaxResampledPixels) k--;
        return (PictureResolution.Low, k);
    }

    /// <summary>The text cap height (R-imf2-1): the median height of the compact, glyph-shaped pieces clear of the line
    /// work — 2–20 w tall, 0.3–1.2 as wide as tall, inking 15–60 % of their box — that stand in a row with another piece
    /// of type: one of a like height (within 1.6 times, a subscript's ratio) beside it, overlapping it by 40 % of the
    /// shorter's height — so a subscripted R₁ counts its R, whose 1 is too narrow to be glyph-shaped — and no shorter
    /// than 85 % of any piece it stands in a row with, so a lower-case letter or a subscript beside a capital is not
    /// counted, nor is a subscript or a superscript. A piece touching line work is a symbol's (an arrowhead cut in two
    /// by its shaft), never type. With fewer
    /// than three in rows — a drawing labelled R and C — every glyph-shaped piece is counted, and the upper quartile
    /// taken.
    /// (0, 0, false) with fewer than two either way.</summary>
    internal static (double Height, int Glyphs, bool Alone) MeasureText(BinaryImage mask, double w)
    {
        var line = TextRegions.LongRuns(mask, (int)Math.Ceiling(20 * w) + 1);
        var rest = mask.Clone();
        for (int i = 0; i < rest.Pixels.Length; i++) if (line.Pixels[i] != 0) rest.Pixels[i] = 0;
        var lab = Components.Label(rest);
        int W = mask.Width, H = mask.Height;
        var touching = new HashSet<int>();
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                if (line.Pixels[y * W + x] == 0) continue;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                        if ((uint)(x + dx) < (uint)W && (uint)(y + dy) < (uint)H && lab.Labels[(y + dy) * W + x + dx] is var l and > 0)
                            touching.Add(l);
            }
        double minH = Math.Max(4, 2 * w), maxH = 20 * w;
        // Pieces of type: the glyph-shaped ones, and the narrow ones a row may also hold (a 1, an l, a subscript).
        var type = new List<(Component C, bool Glyph)>();
        foreach (var c in lab.Components)
        {
            int cw = c.Right - c.Left + 1, ch = c.Bottom - c.Top + 1;
            if (touching.Contains(c.Label) || ch < 0.6 * minH || ch > maxH || cw > 1.2 * ch) continue;
            double aspect = (double)cw / ch, fill = (double)c.Area / ((double)cw * ch);
            bool glyph = ch >= minH && aspect >= 0.3 && fill >= 0.15 && fill <= 0.6;
            if (glyph || aspect >= 0.08) type.Add((c, glyph));
        }
        type.Sort((a, b) => a.C.Left != b.C.Left ? a.C.Left.CompareTo(b.C.Left) : a.C.Label.CompareTo(b.C.Label));

        // Subscripts and superscripts: a piece just after one 1.3 times its height or more, lowered or raised from it —
        // and the pieces running on after it at its own size and level (the "out" of v_out). Counted, a subscript's
        // letters are a row of their own, at two thirds of the type's height.
        static int Ht(Component c) => c.Bottom - c.Top + 1;
        static bool After(Component b, Component a, double h)
        {
            int gap = a.Left - b.Right - 1;
            return gap >= -0.2 * h && gap <= 0.6 * h;
        }
        var script = new bool[type.Count];
        for (int i = 0; i < type.Count; i++)
            foreach (var (b, _) in type)
            {
                var a = type[i].C;
                int ha = Ht(a), hb = Ht(b);
                if (hb < 1.3 * ha || !After(b, a, hb)) continue;
                if (a.Bottom - b.Bottom >= 0.15 * hb || b.Top - a.Top >= 0.15 * hb) { script[i] = true; break; }
            }
        for (bool more = true; more;)
        {
            more = false;
            for (int i = 0; i < type.Count; i++)
            {
                if (script[i]) continue;
                var a = type[i].C;
                for (int j = 0; j < type.Count && !script[i]; j++)
                {
                    if (!script[j]) continue;
                    var b = type[j].C;
                    double h = Math.Max(Ht(a), Ht(b));
                    if (Math.Abs(Ht(a) - Ht(b)) <= 0.3 * h && Math.Abs(a.Bottom - b.Bottom) <= 0.2 * h && After(b, a, h))
                        script[i] = more = true;
                }
            }
        }

        var heights = new List<double>();
        var alone = new List<double>();
        for (int i = 0; i < type.Count; i++)
        {
            var (a, glyph) = type[i];
            if (!glyph || script[i]) continue;
            int ha = a.Bottom - a.Top + 1;
            bool InRow(Component b)
            {
                int hb = b.Bottom - b.Top + 1;
                if (Math.Max(ha, hb) > 1.6 * Math.Min(ha, hb)) return false;
                int overlap = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top) + 1;
                if (overlap < 0.4 * Math.Min(ha, hb)) return false;
                int gap = Math.Max(a.Left, b.Left) - Math.Min(a.Right, b.Right) - 1;
                return gap >= -0.2 * Math.Max(ha, hb) && gap <= 0.6 * Math.Max(ha, hb);
            }
            // In a row, and one of its tall letters: an x-height or a subscript beside a capital is not the cap height.
            bool row = false, tall = true;
            for (int j = 0; j < type.Count; j++)
                if (j != i && type[j].C.Left <= a.Right + ha && InRow(type[j].C))
                {
                    row = true;
                    if (ha < 0.85 * (type[j].C.Bottom - type[j].C.Top + 1)) tall = false;
                }
            if (row && tall) heights.Add(ha);
            else if (!row) alone.Add(ha);
        }
        // Fewer than three in rows — a drawing labelled R, C and v_in, whose only row is a subscript's — and every
        // glyph-shaped piece is counted: a designator's capital beside a subscripted lower-case v, the upper quartile
        // the capitals'.
        bool single = heights.Count < 3;
        if (single) heights.AddRange(alone);
        heights.Sort();
        if (heights.Count < 2) return (0, 0, false);
        return (single ? heights[(int)Math.Round(0.75 * (heights.Count - 1), MidpointRounding.AwayFromZero)] : Median(heights), heights.Count, single);
    }

    /// <summary>The pen as drawn, pixels: the mode, over the skeleton's path pixels, of the ink ACROSS the stroke there
    /// — the darkness summed over the shorter of the horizontal and vertical ink runs and a pixel either side, in units
    /// of the darkest ink. Anti-aliasing keeps that sum, so a 1.1 px hairline that binarises 2 px wide measures 1.1.
    /// (0, 0) with no path pixels.</summary>
    internal static (double Pen, int Samples) InkPen(RasterImage raster, BinaryImage mask)
    {
        var grey = LineArt.Grey(raster);
        if (LineArt.DetectInverted(grey, raster.Width, raster.Height))
            for (int i = 0; i < grey.Length; i++) grey[i] = (byte)(255 - grey[i]);
        var inkHist = new long[256];
        var paperHist = new long[256];
        for (int i = 0; i < grey.Length; i++) (mask.Pixels[i] != 0 ? inkHist : paperHist)[grey[i]]++;
        double ink = Percentile(inkHist, 0.02), paper = Percentile(paperHist, 0.5);
        if (paper - ink < 16) return (0, 0);

        var skel = Skeleton.Thin(mask);
        SkeletonGraph.RemoveStaircaseCorners(skel);
        int W = mask.Width, H = mask.Height;
        double Dark(int x, int y) =>
            (uint)x < (uint)W && (uint)y < (uint)H ? Math.Clamp((paper - grey[y * W + x]) / (paper - ink), 0, 1) : 0;
        var samples = new List<double>();
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                if (!skel[x, y] || SkeletonGraph.Neighbours(skel, x, y) != 2) continue;
                int l = x, r = x, t = y, b = y;
                while (mask.At(l - 1, y)) l--;
                while (mask.At(r + 1, y)) r++;
                while (mask.At(x, t - 1)) t--;
                while (mask.At(x, b + 1)) b++;
                double sum = 0;
                if (r - l <= b - t) for (int u = l - 1; u <= r + 1; u++) sum += Dark(u, y);
                else for (int v = t - 1; v <= b + 1; v++) sum += Dark(x, v);
                samples.Add(sum);
            }
        return samples.Count == 0 ? (0, 0) : (StrokeWidth.Mode(samples), samples.Count);
    }

    private static double Percentile(long[] hist, double q)
    {
        long total = hist.Sum(), acc = 0;
        if (total == 0) return 0;
        for (int v = 0; v < hist.Length; v++)
        {
            acc += hist[v];
            if (acc >= q * total) return v;
        }
        return 255;
    }

    /// <summary>The modal spacing of parallel orthogonal wire candidates, pixels, and how many neighbouring pairs stand
    /// at it; (0, 0) with fewer than three pairs at one spacing.</summary>
    internal static (double Spacing, int Pairs) WireSpacing(IReadOnlyList<WirePiece> wires, double w)
    {
        var gaps = new List<double>();
        foreach (var axis in new[] { WireOrientation.Horizontal, WireOrientation.Vertical })
        {
            var at = wires.Where(p => p.Orientation == axis && p.Length >= 10 * w)
                          .Select(p => axis == WireOrientation.Horizontal ? p.A.y : p.A.x)
                          .OrderBy(v => v).ToList();
            for (int i = 1; i < at.Count; i++)
                if (at[i] - at[i - 1] > 2 * w) gaps.Add(at[i] - at[i - 1]);
        }
        if (gaps.Count == 0) return (0, 0);
        double bin = Math.Max(1, w);
        var mode = gaps.GroupBy(g => Math.Round(g / bin)).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First();
        return mode.Count() < 3 ? (0, 0) : (mode.Average(), mode.Count());
    }

    internal static double Median(IReadOnlyList<double> sorted) =>
        sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : 0.5 * (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]);
}
