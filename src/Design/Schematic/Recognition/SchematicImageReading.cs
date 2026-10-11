// A schematic picture split into what it is made of — wires, words and symbols — with which wire ends reach which
// symbol. brief-img-7-schematic-wires-and-regions.md R-im7-1 … R-im7-8 (overview D9, D11, D16).
//
//   picture ─ normalise: ink colours → binarise → straighten (± 5°) → stroke width w → border, dot grid out
//           ─ profile (PictureProfile, IMF-2): the text height h; a Low picture is read again enlarged ×2 or ×3
//           ─ words (TextRegions): small pieces off the long runs, in lines; their pixels removed
//           ─ the symbol scale s, from the plates, bars and small outlines the default tolerances set aside
//           ─ annotation strokes (StrokeMarks, IMF-3): arrows, dashed lines, zig-zags; an annotation leaves the ink
//           ─ wires (WireGraph): the skeleton's straight runs, symbol strokes set aside, nodes, dots, crossings
//           ─ symbols (SymbolRegions): the residue, its envelope circles, the glyphs touching symbols lifted off it
//             (GlyphLift, IMF-4), grown and merged one part to a region; attachments; hops, supply marks, net labels
//
// Nothing here names a component (IM-8) or reads a word (IM-9). Pure: it posts nothing and writes nothing. Every
// tolerance is a number of pens w at the reference profile, scaled by its class's factor (SchematicImageOptions). The
// coordinates of everything returned are the READ picture's — the source enlarged when it was Low and turned by the
// deskew when there was one; ToSource takes a point back.

using Clipper2Lib;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Layout.Recognition.Silkscreen;
using CircuitRF.Engine;

namespace CircuitRF.Design.Schematic.Recognition;

/// <summary>A box of pixels, inclusive, y down.</summary>
public readonly record struct PixelBox(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left + 1;
    public int Height => Bottom - Top + 1;
    public double CentreX => (Left + Right + 1) / 2.0;
    public double CentreY => (Top + Bottom + 1) / 2.0;

    public PixelBox Union(PixelBox o) =>
        new(Math.Min(Left, o.Left), Math.Min(Top, o.Top), Math.Max(Right, o.Right), Math.Max(Bottom, o.Bottom));

    /// <summary>The distance from (x, y) to the box's area (pixel edges); 0 inside.</summary>
    public double Distance(double x, double y)
    {
        double dx = Math.Max(0, Math.Max(Left - x, x - (Right + 1)));
        double dy = Math.Max(0, Math.Max(Top - y, y - (Bottom + 1)));
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>The gap between two boxes; 0 when they touch or overlap.</summary>
    public double Distance(PixelBox o)
    {
        double dx = Math.Max(0, Math.Max(Left - (o.Right + 1), o.Left - (Right + 1)));
        double dy = Math.Max(0, Math.Max(Top - (o.Bottom + 1), o.Top - (Bottom + 1)));
        return Math.Sqrt(dx * dx + dy * dy);
    }
}

/// <summary>Everything read from a schematic picture (R-im7-1). Kept whole on a refusal, so the overlay can still show
/// what was seen.</summary>
public sealed record SchematicImageRead
{
    /// <summary>The stroke width w, pixels — of the line work as read, thickened when the drawing is in hairlines.</summary>
    public double StrokeWidth { get; init; }

    /// <summary>The picture's own stroke width before any thickening, pixels — what its words are read at.</summary>
    public double TextStrokeWidth { get; init; }

    /// <summary>How many pixels each side the line work was thickened by (0: drawn heavily enough as it was).</summary>
    public double Thickened { get; init; }

    /// <summary>The turn applied to straighten the picture, degrees (0: none).</summary>
    public double DeskewDeg { get; init; }

    /// <summary>How many times larger than the file the picture was read (R-imf2-3); 1: at its own size. Every
    /// coordinate here is the read picture's: <see cref="ToSource"/> takes it back to the file's.</summary>
    public int Scale { get; init; } = 1;

    /// <summary>The picture's own scales and the factors its tolerances took (R-imf2-1).</summary>
    public PictureProfile? Profile { get; init; }

    /// <summary>The symbol scale s in the read picture's pixels; 0 when the profile has none.</summary>
    public double SymbolScale => (Profile?.Symbol.Px ?? 0) * Scale;

    /// <summary>The source picture's size.</summary>
    public int SourceWidth { get; init; }
    public int SourceHeight { get; init; }

    /// <summary>The read picture's size — the source's, larger when it was turned, and <see cref="Scale"/> times it
    /// when it was enlarged.</summary>
    public int Width { get; init; }
    public int Height { get; init; }

    /// <summary>The border frame removed, if one was.</summary>
    public PixelBox? Border { get; init; }

    public int GridDotsRemoved { get; init; }

    /// <summary>Hatched strokes closed into the strokes they draw (R-imf5-3).</summary>
    public int HatchesClosed { get; init; }

    /// <summary>The drawing grid the picture shows (an editor's canvas), in source pixels; null when it shows none.</summary>
    public PictureGrid? Grid { get; init; }

    public IReadOnlyList<TextRegion> Words { get; init; } = [];

    /// <summary>Word-shaped pieces kept as symbol details because they touch line work.</summary>
    public IReadOnlyList<KeptText> KeptText { get; init; } = [];

    public WireGraph Wires { get; init; } = WireGraph.Empty;

    public IReadOnlyList<SymbolRegion> Symbols { get; init; } = [];

    /// <summary>Regions no wire reaches — removed, not passed on.</summary>
    public IReadOnlyList<PixelBox> Decorations { get; init; } = [];

    public IReadOnlyList<NetLabel> NetLabels { get; init; } = [];
    public IReadOnlyList<SupplyMark> SupplyMarks { get; init; } = [];

    /// <summary>End nodes reaching nothing: no symbol, no label, no supply mark.</summary>
    public IReadOnlyList<int> Dangling { get; init; } = [];

    /// <summary>The arrows and dashed lines read as annotations, or as wire (R-imf3-3, R-imf3-4).</summary>
    public IReadOnlyList<StrokeAnnotation> Annotations { get; init; } = [];

    /// <summary>What else the wire reading set aside or found, for the report (R-imf3-7).</summary>
    internal StrokeCounts Strokes { get; init; } = new();

    /// <summary>What the region growing closed, merged, refused and split, for the report (R-imf4-6).</summary>
    internal RegionCounts Regions { get; init; } = new();

    public required SchematicImageReport Report { get; init; }

    /// <summary>Why nothing can be made of the picture, or null.</summary>
    public string? Refusal { get; init; }

    public bool Ok => Refusal is null;

    /// <summary>A point of the read picture in the source picture's pixels.</summary>
    public (double X, double Y) ToSource(double x, double y)
    {
        double k = Scale;
        if (DeskewDeg == 0) return (x / k, y / k);
        double phi = DeskewDeg * Math.PI / 180, c = Math.Cos(phi), s = Math.Sin(phi);
        double rx = x - Width / 2.0, ry = y - Height / 2.0;
        return ((rx * c + ry * s + k * SourceWidth / 2.0) / k, (-rx * s + ry * c + k * SourceHeight / 2.0) / k);
    }

    /// <summary>A point of the source picture in the read picture's pixels — the inverse of <see cref="ToSource"/>.</summary>
    public (double X, double Y) FromSource(double x, double y)
    {
        double k = Scale;
        if (DeskewDeg == 0) return (x * k, y * k);
        double phi = DeskewDeg * Math.PI / 180, c = Math.Cos(phi), s = Math.Sin(phi);
        double qx = k * (x - SourceWidth / 2.0), qy = k * (y - SourceHeight / 2.0);
        return (qx * c - qy * s + Width / 2.0, qx * s + qy * c + Height / 2.0);
    }
}

/// <summary>The counts the wire reading keeps for the report (R-imf3-7).</summary>
internal sealed record StrokeCounts
{
    public int Plates { get; init; }
    public int PlatePairs { get; init; }
    public int CurvedPlates { get; init; }
    public int Boxes { get; init; }
    public int ZigzagChains { get; init; }
    public int RoundJunctions { get; init; }
    public int SquareJunctions { get; init; }
    public int DiamondJunctions { get; init; }
    public int MeshFaces { get; init; }
}

public static class SchematicImageReading
{
    /// <summary>Reads <paramref name="source"/>'s picture as a schematic (R-im7-1). Pure.</summary>
    public static SchematicImageRead Read(ImageSource source, SchematicImageOptions? options = null, RunControl? control = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Read(source.Raster, options, control);
    }

    internal static SchematicImageRead Read(RasterImage source, SchematicImageOptions? options = null, RunControl? control = null)
    {
        var o = options ?? new SchematicImageOptions();
        var token = control?.Token ?? CancellationToken.None;
        var stated = o.Profile;

        // ── Normalise (R-im7-2), and the picture's own scales (R-imf2-1, R-imf2-3) ──
        control?.BeginStage("Reading the line work");
        var n = Normalise(source, o, token);
        // A Low picture — a 1–2 px hairline, 7–9 px type — is read enlarged: a glyph's skeleton and a plate's gap need
        // the pixels. A hairline drawing whose text is tall enough is not Low: IM-12's thickening brings it to the
        // reference, as it always has.
        var (w0, _) = PenOf(n, stated, 1);
        double pen0 = stated.Pen ?? (n.InkPen > 0 ? n.InkPen : n.Pen);
        var text0 = stated.TextHeight is null ? PictureProfile.MeasureText(n.Mask, w0) : (0, 0, false);
        double h0 = stated.TextHeight ?? text0.Height;
        bool hairline = Hairline(n.Mask, w0, o, out double cap, out double lineW);
        var (resolution, k) = PictureProfile.Resolve(pen0, h0, hairline, (long)source.Width * source.Height);
        if (k > 1)
        {
            control?.BeginStage("Reading the line work, enlarged");
            n = Normalise(Resample.Enlarge(source, k, o.MaxThreads), o, token);
        }
        var mask = n.Mask;
        var (w, penSamples) = PenOf(n, stated, k);
        token.ThrowIfCancellationRequested();

        // ── Hairlines (IM-12) ──
        // Every tolerance below is in w, measured on drawings whose text stands 5–7 w tall. A drawing in hairlines —
        // circuitRF's own schematic, whose 1 px pen draws 14 px text — has symbols and words three times that size in
        // w: its inductor humps read as wire, its glyphs as symbols. Its words are found on the ink as drawn, at the
        // drawing's own scale (a thickened glyph closes up, and a net label sitting on its wire would join it); then
        // the line work alone is thickened to the weight the tolerances were measured at.
        // The line work's own width decides, not w: on a drawing with more words than wire, w is the text's weight.
        double inkW = w, thick = 0, textW = w;
        if (k > 1) hairline = Hairline(mask, w, o, out cap, out lineW);
        if (hairline)
        {
            textW = cap / o.TextHeightInStrokes;
            thick = Math.Max(0, (textW - lineW) / 2);
        }

        // ── The text height, and the text tolerances it sets (R-imf2-2) ──
        var (hMeasured, glyphs, alone) = stated.TextHeight is not null ? (0, 0, false) : k == 1 ? text0 : PictureProfile.MeasureText(mask, inkW);
        var text = stated.TextHeight is { } th ? ProfileScale.Stated(th * k)
                 : hMeasured > 0 ? new ProfileScale(hMeasured, glyphs, ProfileBasis.Measured)
                 : new ProfileScale(PictureProfile.ReferenceTextLow * textW, 0, ProfileBasis.Derived);
        // A hairline drawing's thickening has already set its text to the reference height in w: its factor is 1.
        double textFactor = thick > 0 ? 1 : PictureProfile.Factor(text.Px / textW, PictureProfile.ReferenceTextLow, PictureProfile.ReferenceTextHigh);
        var profile = new PictureProfile
        {
            Pen = stated.Pen is { } sp ? ProfileScale.Stated(sp)
                : new ProfileScale((n.InkPen > 0 ? n.InkPen : inkW) / k, penSamples, ProfileBasis.Measured),
            Text = text with { Px = text.Px / k }, TextAlone = alone && text.Basis == ProfileBasis.Measured, Resolution = resolution, Resample = k, TextFactor = textFactor,
        };
        var to = profile.Apply(o);

        var read = new SchematicImageRead
        {
            StrokeWidth = w, TextStrokeWidth = inkW, Thickened = thick, DeskewDeg = n.Deskew, Scale = k,
            SourceWidth = source.Width, SourceHeight = source.Height, Width = n.Raster.Width, Height = n.Raster.Height,
            Border = n.Border, GridDotsRemoved = n.Grid, HatchesClosed = n.Hatches,
            Grid = PictureGrids.Detect(source), Profile = profile, Report = new SchematicImageReport(),
        };

        // ── Words (R-im7-3) ──
        control?.BeginStage("Finding words");
        var words = TextRegions.Find(mask, textW, to);
        var wireMask = mask.Clone();
        for (int i = 0; i < wireMask.Pixels.Length; i++) if (words.Removed.Pixels[i] != 0) wireMask.Pixels[i] = 0;
        if (thick > 0)
        {
            // w is then the weight aimed at, not a measurement: a 2.6 px pen binarises to 2, 3 or 4 px by colour and
            // phase, and a hexagon's side read 8.0 w at one zoom and 7.1 at the next — either side of a threshold.
            wireMask = Morphology.Dilate(wireMask, thick, o.MaxThreads);
            w = textW;
            read = read with { StrokeWidth = w };
        }
        read = read with { Words = words.Words, KeptText = words.Kept };
        token.ThrowIfCancellationRequested();

        // ── Wires (R-im7-4, R-im7-5) ──
        control?.BeginStage("Tracing wires");
        var fills = Fills(wireMask, w, o);
        var inkMask = wireMask.Clone();
        for (int i = 0; i < wireMask.Pixels.Length; i++) if (fills.Pixels[i] != 0) wireMask.Pixels[i] = 0;
        var dt = DistanceTransform.Compute(wireMask, o.MaxThreads);
        var blobs = WireGraph.Blobs(wireMask, dt, w, o);
        var skel = SkeletonGraph.Build(wireMask, new SkeletonGraphOptions { StrokeWidth = w, MaxThreads = o.MaxThreads });

        // ── The symbol scale, and the symbol tolerances it sets (R-imf2-1, R-imf2-2) ──
        // Measured on the line work as the defaults read it — the plates, bars and small outlines MaxSymbolStroke and
        // the candidate cycles set aside — before any tolerance has moved.
        var basePieces = WireGraph.Pieces(skel, w, o);
        var baseCandidates = basePieces.Where(p => p.IsWire).ToList();
        var strokeLengths = new List<double>();
        double reachS = 8 * text.Px;
        // A line stopped by a label kept as a symbol detail ends in paper all the same; one ending on a fill (an
        // arrow's shaft at its head) is no plate.
        var keptBoxes = words.Kept.Select(t => t.Box).ToList();
        bool? EndAt(PointD p)
        {
            if (keptBoxes.Any(b => b.Distance(p.x, p.y) <= w)) return true;
            int r = (int)Math.Ceiling(w) + 1;
            for (int y = (int)p.y - r; y <= (int)p.y + r; y++)
                for (int x = (int)p.x - r; x <= (int)p.x + r; x++)
                    if (fills.At(x, y)) return false;
            return null;
        }
        var baseStrokes = WireGraph.SymbolStrokes(baseCandidates, blobs, w, o, strokeLengths, reachS, EndAt);
        strokeLengths.Sort();
        var symbol = stated.SymbolScale is { } ss ? ProfileScale.Stated(ss * k)
                   : strokeLengths.Count >= PictureProfile.MinSymbolSamples
                       ? new ProfileScale(PictureProfile.Median(strokeLengths), strokeLengths.Count, ProfileBasis.Measured)
                       : new ProfileScale(PictureProfile.SymbolInText * text.Px, strokeLengths.Count, ProfileBasis.Derived);
        double symbolFactor = PictureProfile.Factor(symbol.Px / w, PictureProfile.ReferenceSymbolLow, PictureProfile.ReferenceSymbolHigh);
        var (spacing, pairs) = PictureProfile.WireSpacing(baseCandidates.Where(p => !baseStrokes.Contains(p.Sources[0])).ToList(), w);
        profile = profile with
        {
            Symbol = symbol with { Px = symbol.Px / k }, SymbolFactor = symbolFactor,
            Grid = read.Grid is { } pg ? new ProfileScale(pg.Pitch, 0, ProfileBasis.Measured)
                 : spacing > 0 ? new ProfileScale(spacing / k, pairs, ProfileBasis.Measured) : null,
            GridFromCanvas = read.Grid is not null,
        };
        o = profile.Apply(o);
        read = read with { Profile = profile };

        var pieces = symbolFactor == 1 ? basePieces : WireGraph.Pieces(skel, w, o);

        // ── Annotation strokes (R-imf3-3 … R-imf3-5) ──
        // Arrows, dashed lines and zig-zag chains, found on the runs the final tolerances cut. What is an annotation
        // leaves the ink, and the line work is read again without it: an arrow left in would be wire or symbol.
        var marks = StrokeMarks.Find(inkMask, pieces, w, symbol.Px, text.Px, words.Words, keptBoxes, o);
        var annotations = marks.Annotations;
        if (marks.Erase is { } erase)
        {
            var restore = marks.Restore!;
            for (int i = 0; i < erase.Pixels.Length; i++)
            {
                if (erase.Pixels[i] != 0) wireMask.Pixels[i] = fills.Pixels[i] = inkMask.Pixels[i] = 0;
                else if (restore.Pixels[i] != 0) { wireMask.Pixels[i] = 1; fills.Pixels[i] = 0; }
            }
            dt = DistanceTransform.Compute(wireMask, o.MaxThreads);
            blobs = WireGraph.Blobs(wireMask, dt, w, o);
            skel = SkeletonGraph.Build(wireMask, new SkeletonGraphOptions { StrokeWidth = w, MaxThreads = o.MaxThreads });
            pieces = WireGraph.Pieces(skel, w, o);
            // Read once more for what the runs are now — the removed annotations stay as they were found.
            marks = StrokeMarks.Find(inkMask, pieces, w, symbol.Px, text.Px, words.Words, keptBoxes, o);
            annotations = [.. annotations.Where(a => a.Removed), .. marks.Annotations.Where(a => !a.Removed)];
        }
        var candidates = pieces.Where(p => p.IsWire && !marks.Dropped(p)).Concat(marks.DashedWires).ToList();
        // A short stroke wholly inside a label kept as a symbol detail — a box no taller than a line and a half of
        // text; what is kept is sometimes a ground's bars — is no box's side.
        var labels = keptBoxes.Where(b => b.Height <= 1.5 * text.Px).ToList();
        bool Glyph(WirePiece p) => labels.Any(b => b.Distance(p.A.x, p.A.y) <= 1 && b.Distance(p.B.x, p.B.y) <= 1);
        // A line's end is bare when the skeleton stops in paper or on a label kept as a symbol detail (a value touching
        // a plate). A line with a word at BOTH ends is a wire its labels name, not a plate (R-imf3-1).
        var wordBoxes = words.Words.Select(t => t.Box).ToList();
        var plates = new PlateRules
        {
            // Not EndAt's fill rule, which keeps an arrow's shaft out of the scale's samples: a ground's lower bars,
            // merged at a hairline, are a fill under its top bar.
            Symbol = symbol.Px, Bare = p => keptBoxes.Any(b => b.Distance(p.x, p.y) <= w) ? true : null,
            Labelled = (end, mid) => wordBoxes.Any(b => b.Distance(end.x, end.y) <= w && b.Distance(mid.x, mid.y) > w),
            Ink = p => inkMask.At((int)Math.Floor(p.x), (int)Math.Floor(p.y)),
            Sides = [.. pieces.Where(p => p.Side && !Glyph(p))],
        };
        var strokes = WireGraph.SymbolStrokes(candidates, blobs, w, o, plates: plates);
        var wires = candidates.Where(p => !strokes.Contains(p.Sources[0])).ToList();
        read = read with
        {
            Annotations = annotations,
            Strokes = new StrokeCounts
            {
                Plates = plates.Plates, PlatePairs = plates.PlatePairs, CurvedPlates = plates.CurvedPlates, Boxes = plates.Boxes,
                ZigzagChains = marks.ZigzagChains, MeshFaces = plates.MeshFaces,
            },
        };
        token.ThrowIfCancellationRequested();
        if (wires.Count == 0)
            return read with { Report = Report(read), Refusal = SchematicImageReport.NoWiresRefusal };
        // A chart's arcs, a drawing's curves: their skeleton is straight only in short slanting pieces. A machine-drawn
        // schematic wires across and down; most of its line work, by length, slanting is no schematic (IM-12, a Smith
        // chart). By length, because a coil's humps leave a few short slanting pieces among a schematic's long wires.
        var slanting = wires.Where(p => p.Orientation == WireOrientation.Diagonal).ToList();
        if (slanting.Count >= MinSlantingWires && slanting.Sum(p => p.Length) > 0.5 * wires.Sum(p => p.Length))
            return read with { Report = Report(read), Refusal = SchematicImageReport.CurvedLinesRefusal };
        // The user's crossing corrections (R-im10-8), carried into the read picture's pixels.
        var flips = o.Overrides.Where(v => !v.IsJoin)
                               .Select(v => { var (x, y) = read.FromSource(v.X, v.Y); return (x, y, v.Kind == ConnectionOverrideKind.Connect); })
                               .ToList();
        var first = WireGraph.Build(wires, [], blobs, w, o, flips);

        // ── Symbols (R-im7-6, R-im7-7) ──
        control?.BeginStage("Finding symbols");
        // A large fill (R-imf2-4) — wider than three symbol scales — is the region's fill, not its ink: its rim joins
        // the residue, so the region still stands where it is, and its inside does not.
        var (largeFills, inside) = LargeFills(fills, w, 3 * symbol.Px, o);
        BinaryImage ResidueOf(WireGraph g)
        {
            var res = SymbolRegions.Residue(wireMask, g, blobs, w);
            for (int i = 0; i < res.Pixels.Length; i++) if (fills.Pixels[i] != 0 && inside.Pixels[i] == 0) res.Pixels[i] = 1;
            return res;
        }
        var residue = ResidueOf(first);
        // An envelope circle (R-imf4-3) holds its part: the short runs of its own outline and of what is drawn inside it,
        // taken for wire where they run straight, are the circle's.
        var circles = SymbolRegions.Envelopes(residue, inkMask, symbol.Px, w);
        if (circles.Count > 0)
        {
            int before = wires.Count;
            wires = [.. wires.Where(p => !circles.Any(c => c.Holds(p.A, w) && c.Holds(p.B, w)))];
            if (wires.Count < before)
            {
                first = WireGraph.Build(wires, [], blobs, w, o, flips);
                residue = ResidueOf(first);
            }
        }
        // Glyphs come off the symbols they touch before regions are grown (R-imf4-4), and every word is placed against
        // the closed outlines (R-imf4-2).
        var regionCounts = new RegionCounts();
        var lifted = GlyphLift.Lift(residue, first, words.Words, plates.Outlines, text.Px + 2 * thick, symbol.Px, w, o, out regionCounts.Glyphs);
        var allWords = Place([.. words.Words, .. lifted], plates.Outlines, text.Px + 2 * thick);
        read = read with { Words = allWords };
        var scales = new RegionScales(symbol.Px, plates.Outlines, PlatePairs(plates.Lines, symbol.Px, w)) { Circles = circles };
        var raw = SymbolRegions.Group(residue, first, w, o, scales, regionCounts);
        var (hops, hopRegions) = SymbolRegions.Hops(raw, mask.Width, first, w, o);
        // A variable resistor's arrow (R-imf5-3): the stretch of its shaft standing out of the body, read as a short
        // slanting wire with one bare end that points into a region, is the arrow's — off the wires, so the body keeps
        // its two leads and the classifier finds the rest of the arrow across it.
        var tails = ArrowTails(wires, first, raw, symbol.Px, w);
        if (tails.Count > 0) wires = [.. wires.Where(p => !tails.Contains(p))];
        // A part standing on a wire at a junction dot with no lead of its own between them — a bias tee's coil drawn down
        // onto its rail (R-imf5-4): a stub from the dot to the part, so the wire is split there and the part has an end.
        var taps = Taps(wires, first, raw, blobs, mask.Width, o.Attach * w + w / 2 + 1, w);
        if (taps.Count > 0) wires = [.. wires, .. taps];
        var graph = hops.Count > 0 || tails.Count > 0 || taps.Count > 0 ? WireGraph.Build(wires, hops, blobs, w, o, flips) : first;
        var regions = raw.Where((_, j) => !hopRegions.Contains(j)).ToList();
        var attached = SymbolRegions.Attach(regions, mask.Width, graph, allWords, w, o);
        for (int i = 0; i < attached.Regions.Count; i++)
        {
            var r = attached.Regions[i];
            var mine = largeFills.Where(f => r.Box.Distance(f.CentreX, f.CentreY) == 0).ToList();
            if (mine.Count > 0) attached.Regions[i] = r with { Fills = mine };
        }
        token.ThrowIfCancellationRequested();

        // Junction marks by shape (R-imf3-6).
        int round = 0, square = 0, diamond = 0;
        foreach (var node in graph.Nodes.Where(nd => nd.Dot))
        {
            var at = new PointD(node.X, node.Y);
            var blob = blobs.OrderBy(b => WireGraph.Dist(b.Centre, at)).FirstOrDefault();
            if (blob is null) continue;
            switch (StrokeMarks.ShapeAt(wireMask, blob.Centre, blob.Radius))
            {
                case JunctionShape.Square: square++; break;
                case JunctionShape.Diamond: diamond++; break;
                default: round++; break;
            }
        }
        read = read with
        {
            Wires = graph, Symbols = attached.Regions, Decorations = attached.Decorations, NetLabels = attached.Labels, Regions = regionCounts,
            SupplyMarks = attached.Supplies, Dangling = attached.Dangling,
            Strokes = read.Strokes with { RoundJunctions = round, SquareJunctions = square, DiamondJunctions = diamond },
        };
        read = read with { Report = Report(read) };
        return attached.Regions.Count == 0 ? read with { Refusal = SchematicImageReport.NoSymbolsRefusal } : read;
    }

    /// <summary>The slanting wire pieces that are an arrow's tail standing out of a symbol (R-imf5-3): no longer than
    /// 1.5 s, one end bare in paper, the other a wire end alone at a region, and the line through them running on into
    /// that region's ink.</summary>
    private static HashSet<WirePiece> ArrowTails(List<WirePiece> wires, WireGraph graph, List<RawRegion> regions, double s, double w)
    {
        var r = new HashSet<WirePiece>();
        foreach (var p in wires)
        {
            if (p.Orientation != WireOrientation.Diagonal || p.FreeA == p.FreeB || p.Length > 1.5 * s) continue;
            var (bare, inner) = p.FreeA ? (p.A, p.B) : (p.B, p.A);
            if (!graph.Ends.Any(e => Math.Abs(e.X - inner.x) <= w && Math.Abs(e.Y - inner.y) <= w)) continue;
            double len = WireGraph.Dist(bare, inner);
            if (len < 1) continue;
            double ux = (inner.x - bare.x) / len, uy = (inner.y - bare.y) / len;
            // On into a region: a point a third of a symbol scale beyond the inner end lies in a region's box.
            double qx = inner.x + ux * s / 3, qy = inner.y + uy * s / 3;
            if (regions.Any(g => g.Box.Distance(inner.x, inner.y) <= 1.5 * w && g.Box.Distance(qx, qy) == 0)) r.Add(p);
        }
        return r;
    }

    /// <summary>A stub from a junction dot on a wire's interior toward a region no wire end reaches whose ink comes within
    /// <paramref name="reach"/> of the dot's edge (R-imf5-4) — toward its nearest ink outside the dot, and at least two
    /// pens beyond the dot, so the dot and the stub's far end stay two nodes.</summary>
    private static List<WirePiece> Taps(List<WirePiece> wires, WireGraph graph, List<RawRegion> regions, IReadOnlyList<JunctionBlob> blobs,
                                        int width, double reach, double w)
    {
        var r = new List<WirePiece>();
        foreach (var g in regions)
        {
            if (graph.Ends.Any(e => g.Box.Distance(e.X, e.Y) <= reach)) continue;
            foreach (var b in blobs)
            {
                if (g.Box.Distance(b.Centre.x, b.Centre.y) > b.Radius + reach) continue;
                // On a wire: through its interior, or where two of its pieces meet at the dot.
                var on = wires.FirstOrDefault(p => WireGraph.Local(p.A, p.B, b.Centre) is var (along, perp)
                                                   && Math.Abs(perp) <= Math.Max(w, b.Radius) && along >= -b.Radius && along <= p.Length + b.Radius);
                if (on is null) continue;
                // The way to the part: its nearest ink outside the dot (its foot may run into the dot itself).
                double D(int i) => Math.Sqrt(Math.Pow(i % width + 0.5 - b.Centre.x, 2) + Math.Pow(i / width + 0.5 - b.Centre.y, 2));
                var outside = g.Pixels.Where(i => D(i) > b.Radius + 0.5).ToList();
                if (outside.Count == 0) continue;
                int nearest = outside.MinBy(D);
                double px = nearest % width + 0.5, py = nearest / width + 0.5, d = D(nearest);
                if (d > b.Radius + reach) continue;
                double ux = (px - b.Centre.x) / d, uy = (py - b.Centre.y) / d, len = Math.Max(d, b.Radius + 2 * w);
                // Drawn across or down where it is within 30° of either: a lead is, and the part's ink nearest the dot is
                // often a hump beside the lead's line rather than on it.
                if (Math.Abs(ux) < 0.5) (ux, uy) = (0, Math.Sign(uy));
                else if (Math.Abs(uy) < 0.5) (ux, uy) = (Math.Sign(ux), 0);
                var end = new PointD(b.Centre.x + ux * len, b.Centre.y + uy * len);
                r.Add(new WirePiece
                {
                    A = b.Centre, B = end, Width = w, IsWire = true, Angle = Math.Atan2(uy, ux) is var a && a < 0 ? a + Math.PI : a,
                    Orientation = Math.Abs(ux) < 0.2 ? WireOrientation.Vertical : Math.Abs(uy) < 0.2 ? WireOrientation.Horizontal : WireOrientation.Diagonal,
                });
                break;
            }
        }
        return r;
    }

    /// <summary>The words in reading order, numbered again, each placed against the closed outlines (R-imf4-2): inside an
    /// orthogonal box, or within a text height of a triangle (an amplifier's pin numbers beside its leads).</summary>
    private static List<TextRegion> Place(List<TextRegion> words, IReadOnlyList<OutlineBox> outlines, double h)
    {
        TextPlace At(TextRegion t)
        {
            if (t.Place != TextPlace.Free) return t.Place;
            var b = t.Box;
            foreach (var ob in outlines)
            {
                if (ob.Orthogonal && b.Left >= ob.X0 && b.Right <= ob.X1 && b.Top >= ob.Y0 && b.Bottom <= ob.Y1) return TextPlace.InsideBox;
                if (!ob.Orthogonal && b.Left <= ob.X1 + h && b.Right >= ob.X0 - h && b.Top <= ob.Y1 + h && b.Bottom >= ob.Y0 - h
                    && !(b.Left >= ob.X0 && b.Right <= ob.X1 && b.Top >= ob.Y0 && b.Bottom <= ob.Y1))
                    return TextPlace.AtPin;
            }
            return TextPlace.Free;
        }
        return [.. words.OrderBy(t => t.Box.Top).ThenBy(t => t.Box.Left).Select((t, i) => t with { Id = i, Place = At(t) })];
    }

    /// <summary>The centres of the plate pairs among <paramref name="lines"/> (R-imf4-5): two plates side by side, parallel,
    /// overlapping over half the shorter, no more than 1.5 s apart — a capacitor each. A plate is in one pair at most.</summary>
    private static List<PointD> PlatePairs(IReadOnlyList<PlateLine> lines, double s, double w)
    {
        var plates = lines.Where(l => l.Plate).ToList();
        var used = new HashSet<int>();
        var r = new List<PointD>();
        for (int i = 0; i < plates.Count; i++)
            for (int j = i + 1; j < plates.Count && !used.Contains(i); j++)
            {
                if (used.Contains(j)) continue;
                var (p, q) = (plates[i], plates[j]);
                double lp = WireGraph.Dist(p.A, p.B), lq = WireGraph.Dist(q.A, q.B);
                if (lp < 1 || lq < 1) continue;
                double cos = Math.Abs(((p.B.x - p.A.x) * (q.B.x - q.A.x) + (p.B.y - p.A.y) * (q.B.y - q.A.y)) / (lp * lq));
                if (cos < Math.Cos(10 * Math.PI / 180)) continue;
                var (a0, d0) = WireGraph.Local(p.A, p.B, q.A);
                var (a1, d1) = WireGraph.Local(p.A, p.B, q.B);
                double across = Math.Abs((d0 + d1) / 2);
                if (across < w || across > 1.5 * s) continue;
                double overlap = Math.Min(lp, Math.Max(a0, a1)) - Math.Max(0, Math.Min(a0, a1));
                if (overlap < 0.5 * Math.Min(lp, lq)) continue;
                used.Add(i);
                used.Add(j);
                r.Add(new PointD((p.A.x + p.B.x + q.A.x + q.B.x) / 4, (p.A.y + p.B.y + q.A.y + q.B.y) / 4));
            }
        return r;
    }

    /// <summary>A picture binarised, straightened and cleaned.</summary>
    /// <param name="Pen">The run-width mode: the w the binarised line work is read at.</param>
    /// <param name="InkPen">The pen as drawn (PictureProfile.InkPen), 0 when unmeasured.</param>
    private sealed record Normalised(RasterImage Raster, BinaryImage Mask, double Deskew, double Pen, double InkPen, int PenSamples,
                                     PixelBox? Border, int Grid)
    {
        /// <summary>Hatched strokes closed (R-imf5-3).</summary>
        public int Hatches { get; init; }
    }

    private static Normalised Normalise(RasterImage picture, SchematicImageOptions o, CancellationToken token)
    {
        var raster = picture;
        var mask = Binarise(raster, o);
        double deskew = 0;
        if (o.Deskew)
        {
            double lean = Lean(mask, o);
            if (Math.Abs(lean) > o.MinDeskewDeg && Math.Abs(lean) <= o.MaxDeskewDeg)
            {
                deskew = -lean;
                raster = Rotate(picture, deskew * Math.PI / 180);
                mask = Binarise(raster, o);
            }
        }
        token.ThrowIfCancellationRequested();
        double w = RunWidth(mask, out int samples);
        PixelBox? border = o.RemoveBorder ? RemoveBorder(mask, w) : null;
        int grid = o.RemoveGrid ? RemoveGrid(mask, w) : 0;
        if (border is not null || grid > 0) w = RunWidth(mask, out samples);
        w = Math.Max(1.0, w);

        // Specks (IM-12): a piece of ink, or a hole in it, smaller than a quarter of a stroke's square is noise — salt
        // on a scan, a JPEG's ringing — never a full stop (about w²) or a junction dot (several w across). Left in, a
        // speck beside a word is read as a glyph of it, and a hole in a stroke cuts a symbol's outline.
        int speck = Math.Max(2, (int)Math.Ceiling(0.25 * w * w));
        mask = Morphology.Despeckle(mask, speck);
        mask = Morphology.Despeckle(mask.Inverted(), speck).Inverted();
        // A stroke drawn hatched (R-imf5-3): its ticks closed into the stroke before the type and the regions are read.
        mask = Hatching.Close(mask, w, out int hatches);
        var (ink, inkSamples) = PictureProfile.InkPen(raster, mask);
        return new Normalised(raster, mask, deskew, w, ink, ink > 0 ? inkSamples : samples, border, grid) { Hatches = hatches };
    }

    /// <summary>The pen to read at, read pixels: the stated one (source pixels × the scale) or the measured one.</summary>
    private static (double W, int Samples) PenOf(Normalised n, PictureProfileOptions stated, int scale) =>
        stated.Pen is { } pen ? (Math.Max(1.0, pen * scale), 0) : (n.Pen, n.PenSamples);

    /// <summary>Whether the drawing is in hairlines (IM-12): its text, <paramref name="cap"/>, stands taller than
    /// HairlineTextHeight times its line work's width, <paramref name="lineW"/>.</summary>
    private static bool Hairline(BinaryImage mask, double w, SchematicImageOptions o, out double cap, out double lineW)
    {
        cap = lineW = 0;
        if (o.HairlineTextHeight <= 0) return false;
        cap = TextHeight(mask, w);
        if (cap <= 0) return false;
        lineW = LineWidth(mask, w);
        return cap > o.HairlineTextHeight * lineW;
    }

    /// <summary>A picture is refused as curved line work only with at least this many slanting wire pieces, and more
    /// than half its wire length in them: a schematic may have a diagonal wire or two.</summary>
    internal const int MinSlantingWires = 8;

    private static SchematicImageReport Report(SchematicImageRead r) => new()
    {
        StrokeWidth = r.StrokeWidth / r.Scale,
        Profile = r.Profile,
        DeskewDeg = r.DeskewDeg,
        GridDotsRemoved = r.GridDotsRemoved, HatchesClosed = r.HatchesClosed,
        BorderRemoved = r.Border is not null,
        Words = r.Words.Count,
        WordsKeptAsSymbolDetail = r.KeptText.Count,
        WireSegments = r.Wires.Segments.Count,
        DiagonalWires = r.Wires.Segments.Count(s => s.Orientation == WireOrientation.Diagonal),
        JunctionDots = r.Wires.Nodes.Count(n => n.Dot),
        CrossingsConnected = r.Wires.Crossings.Count(c => c.Reading == CrossingReading.Connected),
        CrossingsNotConnected = r.Wires.Crossings.Count(c => c.Reading == CrossingReading.NotConnected),
        Hops = r.Wires.Crossings.Count(c => c.Reading == CrossingReading.Hop),
        SymbolRegions = r.Symbols.Count,
        FillsKept = r.Symbols.Sum(s => s.Fills.Count),
        DecorationsRemoved = r.Decorations.Count,
        NetLabels = r.NetLabels.Count,
        SupplyMarks = r.SupplyMarks.Count,
        DanglingEnds = r.Dangling.Count,
        Plates = r.Strokes.Plates, PlatePairs = r.Strokes.PlatePairs, CurvedPlates = r.Strokes.CurvedPlates, Boxes = r.Strokes.Boxes,
        DimensionArrows = r.Annotations.Count(a => a.Kind == StrokeAnnotationKind.DimensionArrow),
        CurrentArrows = r.Annotations.Count(a => a.Kind == StrokeAnnotationKind.CurrentArrow),
        VoltageArrows = r.Annotations.Count(a => a.Kind == StrokeAnnotationKind.VoltageArrow),
        TerminalArrows = r.Annotations.Count(a => a.Kind == StrokeAnnotationKind.TerminalArrow),
        SupplyArrows = r.Annotations.Count(a => a.Kind == StrokeAnnotationKind.SupplyArrow),
        SymbolArrows = r.Annotations.Count(a => a.Kind == StrokeAnnotationKind.SymbolArrow),
        DashedAnnotations = r.Annotations.Count(a => a.Kind == StrokeAnnotationKind.DashedBox),
        DashedWires = r.Annotations.Count(a => a.Kind == StrokeAnnotationKind.DashedWire),
        ZigzagChains = r.Strokes.ZigzagChains,
        RoundJunctions = r.Strokes.RoundJunctions, SquareJunctions = r.Strokes.SquareJunctions, DiamondJunctions = r.Strokes.DiamondJunctions,
        WireLoops = r.Strokes.MeshFaces,
        ClosedBoxes = r.Regions.Boxes, ClosedTriangles = r.Regions.Triangles, EnvelopeCircles = r.Regions.Circles,
        RegionsMerged = r.Regions.Merged, RefusedGap = r.Regions.RefusedGap, RefusedLead = r.Regions.RefusedLead,
        RefusedSize = r.Regions.RefusedSize, RefusedPins = r.Regions.RefusedPins, RefusedOutline = r.Regions.RefusedOutline,
        GlyphsLifted = r.Regions.Glyphs, RegionsSplit = r.Regions.Split, RegionsSplitInto = r.Regions.SplitInto, Blocks = r.Regions.Blocks,
    };

    /// <summary>The filled areas of the drawing that are not junction dots — a filled rectangle, a diode's triangle,
    /// a supply's arrowhead: what survives an opening by a disc 1.5 w in radius, larger than a dot, far from round or
    /// not symmetric about its centre (a small filled triangle is dot-sized; a round, square or diamond junction mark is
    /// symmetric, R-imf3-6), with its anti-aliased rim. They are symbol, never wire: the skeleton runs straight through
    /// a filled rectangle between two collinear leads, and without this the whole symbol read as one wire with a dot on
    /// it — and a filled diode as a wire through a junction.</summary>
    internal static BinaryImage Fills(BinaryImage mask, double w, SchematicImageOptions o)
    {
        var open = Morphology.Open(mask, 1.5 * w, o.MaxThreads);
        var lab = Components.Label(open);
        double dot = 1.15 * o.MaxDot * w;
        var keep = new HashSet<int>();
        foreach (var c in lab.Components)
        {
            int cw = c.Right - c.Left + 1, ch = c.Bottom - c.Top + 1;
            bool dotLike = cw <= dot && ch <= dot && Math.Max(cw, ch) <= 1.5 * Math.Min(cw, ch) && Symmetric(lab, c, open.Width);
            if (!dotLike) keep.Add(c.Label);
        }
        var core = new BinaryImage(mask.Width, mask.Height);
        if (keep.Count == 0) return core;
        for (int i = 0; i < core.Pixels.Length; i++) if (keep.Contains(lab.Labels[i])) core.Pixels[i] = 1;
        var near = Morphology.Dilate(core, 1.0, o.MaxThreads);
        for (int i = 0; i < near.Pixels.Length; i++) near.Pixels[i] &= mask.Pixels[i];
        return near;
    }

    /// <summary>At least 92 % of the component's pixels have their point reflection through its centroid in it too: a
    /// disc, a square or a diamond does, a triangle does not.</summary>
    private static bool Symmetric(ComponentLabels lab, Component c, int width)
    {
        double cx = 0, cy = 0;
        int n = 0;
        for (int y = c.Top; y <= c.Bottom; y++)
            for (int x = c.Left; x <= c.Right; x++)
                if (lab.Labels[y * width + x] == c.Label) { cx += x; cy += y; n++; }
        if (n < 9) return true;
        cx /= n; cy /= n;
        int hit = 0;
        for (int y = c.Top; y <= c.Bottom; y++)
            for (int x = c.Left; x <= c.Right; x++)
            {
                if (lab.Labels[y * width + x] != c.Label) continue;
                int rx = (int)Math.Round(2 * cx - x), ry = (int)Math.Round(2 * cy - y);
                if (rx >= c.Left && rx <= c.Right && ry >= c.Top && ry <= c.Bottom && lab.Labels[ry * width + rx] == c.Label) hit++;
            }
        return hit >= 0.92 * n;
    }

    /// <summary>The fills of <paramref name="fills"/> wider or taller than <paramref name="size"/> pixels, and their
    /// insides: every pixel more than a pen from the fill's edge.</summary>
    internal static (List<PixelBox> Boxes, BinaryImage Inside) LargeFills(BinaryImage fills, double w, double size, SchematicImageOptions o)
    {
        var inside = new BinaryImage(fills.Width, fills.Height);
        var boxes = new List<PixelBox>();
        if (fills.Count() == 0) return (boxes, inside);
        var lab = Components.Label(fills);
        var large = new HashSet<int>();
        foreach (var c in lab.Components)
            if (Math.Max(c.Right - c.Left + 1, c.Bottom - c.Top + 1) > size)
            {
                large.Add(c.Label);
                boxes.Add(new PixelBox(c.Left, c.Top, c.Right, c.Bottom));
            }
        if (large.Count == 0) return (boxes, inside);
        var dt = DistanceTransform.Compute(fills, o.MaxThreads);
        for (int i = 0; i < inside.Pixels.Length; i++)
            if (large.Contains(lab.Labels[i]) && dt[i] > w) inside.Pixels[i] = 1;
        return (boxes, inside);
    }

    // ── Normalise ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The strokes as foreground. A coloured drawing is first reduced to its ink colours: those at least half
    /// as far from the ground as the darkest is (on a dark ground, the lightest), and — colour is not darkness (IMF-2,
    /// R-imf2-4) — every other colour drawn as thin strokes and standing clear of the ground: a datasheet's blue type, a
    /// green not-fitted part. What drops out is a pale tint (a highlight, a tinted grid, a box's gradient) and a wide
    /// colour that is not dark (a shaded triangle's fill), whose outline is drawn in ink of its own — and a dark
    /// line's lighter anti-aliased rim, which touches it all along.</summary>
    internal static BinaryImage Binarise(RasterImage img, SchematicImageOptions o)
    {
        if (!HasColour(img)) return LineArt.Binarise(img, o.LineArt);
        var set = ColourClusters.Find(img, 8, new ColourClusterOptions { MaxThreads = o.MaxThreads });
        int bg = set.BackgroundIndex();
        double bgL = set.Clusters[bg].Lab.L;
        bool darkGround = bgL < 50;
        double far = darkGround ? set.Clusters.Max(c => c.Lab.L) : set.Clusters.Min(c => c.Lab.L);
        double cut = bgL + 0.5 * (far - bgL);
        var (runs, _) = ImageKind.MedianRuns(set);
        // The pen: the darkest ink's median run; a colour is thin within three of it.
        int darkest = set.Clusters.Where(c => c.Index != bg).OrderBy(c => darkGround ? -c.Lab.L : c.Lab.L).First().Index;
        double thin = ImageKind.ThinRunStrokes * Math.Max(2, runs[darkest]);
        bool Dark(ColourCluster c) => darkGround ? c.Lab.L >= cut : c.Lab.L <= cut;
        bool Clear(ColourCluster c) => Math.Abs(c.Lab.L - bgL) >= PaleContrast * Math.Abs(far - bgL) || c.Lab.DeltaE(set.Clusters[bg].Lab) >= PaleDeltaE;
        var keep = set.Clusters.Where(c => c.Index != bg && Dark(c)).Select(c => c.Index).ToList();
        var fringe = Fringes(set, keep);
        keep.AddRange(set.Clusters.Where(c => c.Index != bg && !Dark(c) && runs[c.Index] <= thin && Clear(c) && !fringe[c.Index])
                                  .Select(c => c.Index));
        if (keep.Count == 0) return LineArt.Binarise(img, o.LineArt);

        var cover = new float[img.Width * img.Height];
        foreach (int k in keep)
        {
            var v = set.Coverage(k).Values;
            for (int i = 0; i < cover.Length; i++) cover[i] += v[i];
        }
        var rgba = new byte[cover.Length * 4];
        for (int i = 0; i < cover.Length; i++)
        {
            byte g = (byte)Math.Clamp((int)Math.Round(255 * (1 - Math.Min(1f, cover[i]))), 0, 255);
            rgba[4 * i] = rgba[4 * i + 1] = rgba[4 * i + 2] = g;
            rgba[4 * i + 3] = 255;
        }
        return LineArt.Binarise(new RasterImage(img.Width, img.Height, rgba), o.LineArt with { Invert = false });
    }

    /// <summary>Which colours are another's anti-aliased edge: more than half their pixels touch a colour of
    /// <paramref name="ink"/>. A blue wire's lighter rim is no line of its own; a green part's line is.</summary>
    private static bool[] Fringes(ColourClusterSet set, List<int> ink)
    {
        int w = set.Width, h = set.Height, k = set.Clusters.Count;
        var isInk = new bool[k];
        foreach (int c in ink) isInk[c] = true;
        var touching = new long[k];
        var count = new long[k];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int c = set.Label(x, y);
                if (c < 0 || c >= k || isInk[c]) continue;
                count[c]++;
                if ((x > 0 && isInk[set.Label(x - 1, y)]) || (x + 1 < w && isInk[set.Label(x + 1, y)])
                    || (y > 0 && isInk[set.Label(x, y - 1)]) || (y + 1 < h && isInk[set.Label(x, y + 1)]))
                    touching[c]++;
            }
        var r = new bool[k];
        for (int c = 0; c < k; c++) r[c] = count[c] > 0 && touching[c] * 2 > count[c];
        return r;
    }

    /// <summary>A thin colour lighter than the darkness cut is ink only when it stands at least this share of the
    /// darkest ink's contrast from the ground — or this far from it in CIELAB: a blue or a green line is far from
    /// white in hue though not in lightness, a grid's tint or a gradient's step is near it in both.</summary>
    internal const double PaleContrast = 0.25, PaleDeltaE = 40;

    /// <summary>More than one pixel in two thousand is clearly coloured.</summary>
    private static bool HasColour(RasterImage img)
    {
        var p = img.Rgba;
        long n = 0, limit = Math.Max(1, (long)img.Width * img.Height / 2000);
        for (int j = 0; j < p.Length; j += 4)
        {
            int mx = Math.Max(p[j], Math.Max(p[j + 1], p[j + 2])), mn = Math.Min(p[j], Math.Min(p[j + 1], p[j + 2]));
            if (mx - mn > 48 && ++n > limit) return true;
        }
        return false;
    }

    /// <summary>The dominant lean of the long straight runs off 0°/90°, degrees, y down (length-weighted median); 0
    /// when there are none. Runs are cut where the skeleton leaves a line by more than 1.5 px: a line leaning 1.2° is
    /// binarised as a staircase of level steps, and cut at every step each piece would read level.</summary>
    internal static double Lean(BinaryImage mask, SchematicImageOptions o)
    {
        double w = Math.Max(1.0, RunWidth(mask));
        var g = SkeletonGraph.Build(mask, new SkeletonGraphOptions { StrokeWidth = w, MaxThreads = o.MaxThreads });
        var leans = new List<(double Dev, double Len)>();
        foreach (var e in g.Edges)
        {
            var pts = e.Pixels.Select(q => new PointD(q.X + 0.5, q.Y + 0.5)).ToList();
            var keep = Polylines.SimplifyOpen(pts, 1.5);
            for (int k = 1; k < keep.Count; k++)
            {
                var run = pts.GetRange(keep[k - 1], keep[k] - keep[k - 1] + 1);
                var fit = Fit.Segment(run);
                if (fit.Length < 8 * w || fit.Residual > 1.0) continue;
                double dev = fit.Angle * 180 / Math.PI;
                dev -= 90 * Math.Round(dev / 90);
                if (Math.Abs(dev) <= o.MaxDeskewDeg + 1) leans.Add((dev, fit.Length));
            }
        }
        if (leans.Count == 0) return 0;
        leans.Sort((x, y) => x.Dev.CompareTo(y.Dev));
        double half = leans.Sum(l => l.Len) / 2, acc = 0;
        foreach (var (dev, len) in leans)
        {
            acc += len;
            if (acc >= half) return dev;
        }
        return leans[^1].Dev;
    }

    /// <summary><paramref name="src"/> turned by <paramref name="phi"/> radians about its centre (y down) onto a
    /// white canvas large enough to hold it, sampled bilinearly.</summary>
    internal static RasterImage Rotate(RasterImage src, double phi)
    {
        double c = Math.Cos(phi), s = Math.Sin(phi);
        int w = (int)Math.Ceiling(Math.Abs(src.Width * c) + Math.Abs(src.Height * s));
        int h = (int)Math.Ceiling(Math.Abs(src.Width * s) + Math.Abs(src.Height * c));
        var p = src.Rgba;
        var r = new byte[w * h * 4];
        double scx = src.Width / 2.0, scy = src.Height / 2.0, dcx = w / 2.0, dcy = h / 2.0;
        for (int v = 0; v < h; v++)
            for (int u = 0; u < w; u++)
            {
                double rx = u + 0.5 - dcx, ry = v + 0.5 - dcy;
                double sx = rx * c + ry * s + scx - 0.5, sy = -rx * s + ry * c + scy - 0.5;
                int x0 = (int)Math.Floor(sx), y0 = (int)Math.Floor(sy);
                double fx = sx - x0, fy = sy - y0;
                int o = (v * w + u) * 4;
                for (int ch = 0; ch < 3; ch++)
                {
                    double Sample(int x, int y) =>
                        (uint)x < (uint)src.Width && (uint)y < (uint)src.Height ? p[(y * src.Width + x) * 4 + ch] : 255;
                    double val = (1 - fy) * ((1 - fx) * Sample(x0, y0) + fx * Sample(x0 + 1, y0))
                               + fy * ((1 - fx) * Sample(x0, y0 + 1) + fx * Sample(x0 + 1, y0 + 1));
                    r[o + ch] = (byte)Math.Clamp((int)Math.Round(val), 0, 255);
                }
                r[o + 3] = 255;
            }
        return new RasterImage(w, h, r);
    }

    /// <summary>The stroke width: the mode, over the skeleton's path pixels, of the shorter of the horizontal and the
    /// vertical ink run through each. IM-1's 2·DT − 1 reads only odd widths on an orthogonal stroke (a 2 px wire is a
    /// distance of 1 from both sides' centres and reads 1); a run reads 2.</summary>
    internal static double RunWidth(BinaryImage mask) => RunWidth(mask, out _);

    /// <summary><see cref="RunWidth(BinaryImage)"/>, with how many skeleton pixels it was measured over.</summary>
    internal static double RunWidth(BinaryImage mask, out int count)
    {
        var skel = Skeleton.Thin(mask);
        SkeletonGraph.RemoveStaircaseCorners(skel);
        int w = mask.Width, h = mask.Height;
        var samples = new List<double>();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (!skel[x, y] || SkeletonGraph.Neighbours(skel, x, y) != 2) continue;
                int l = x, r = x, t = y, b = y;
                while (mask.At(l - 1, y)) l--;
                while (mask.At(r + 1, y)) r++;
                while (mask.At(x, t - 1)) t--;
                while (mask.At(x, b + 1)) b++;
                samples.Add(Math.Min(r - l + 1, b - t + 1));
            }
        count = samples.Count;
        return StrokeWidth.Mode(samples);
    }

    /// <summary>The width of the drawing's line work, pixels: <see cref="RunWidth"/> over the straight runs longer than
    /// 40 w alone; w when there are none.</summary>
    internal static double LineWidth(BinaryImage mask, double w)
    {
        var line = TextRegions.LongRuns(mask, (int)Math.Ceiling(40 * w) + 1);
        double lw = line.Count() == 0 ? 0 : RunWidth(line);
        return lw > 0 ? lw : w;
    }

    /// <summary>The height of the drawing's text, pixels: the median, over the typeset lines of three glyphs or more, of
    /// each line's median glyph height — glyphs being what is left beside the straight runs longer than 40 w. 0 when
    /// fewer than two such lines are found: a drawing with no words says nothing about its weight.</summary>
    internal static double TextHeight(BinaryImage mask, double w)
    {
        var line = TextRegions.LongRuns(mask, (int)Math.Ceiling(40 * w) + 1);
        var rest = mask.Clone();
        for (int i = 0; i < rest.Pixels.Length; i++) if (line.Pixels[i] != 0) rest.Pixels[i] = 0;
        var comps = Components.Label(rest).Components
            .Where(c => c.Bottom - c.Top + 1 >= 3 * w && c.Bottom - c.Top + 1 <= 40 * w && c.Right - c.Left + 1 <= 40 * w)
            .ToList();
        var units = comps.Select(c =>
        {
            var u = new StrokeGlyphs.Unit();
            u.Add(0, [c.Left, c.Top, c.Right + 1, c.Bottom + 1]);
            return u;
        }).ToList();
        var lines = StrokeGlyphs.Groups(units, vertical: false, typeset: true)
            .Where(g => g.Count >= 3)
            .Select(g => g.Select(k => (double)(comps[k].Bottom - comps[k].Top + 1)).OrderBy(h => h).ElementAt(g.Count / 2))
            .OrderBy(h => h)
            .ToList();
        return lines.Count < 2 ? 0 : lines[lines.Count / 2];
    }

    /// <summary>Removes a frame enclosing most of the drawing — the component reaching over 60 % of the picture each
    /// way and inking at least 90 % of each side of its box — with everything drawn on it (a title block, zone
    /// marks). Returns its box.</summary>
    internal static PixelBox? RemoveBorder(BinaryImage mask, double w)
    {
        int W = mask.Width, H = mask.Height;
        var lab = Components.Label(mask);
        int band = (int)Math.Ceiling(w) + 2;
        foreach (var c in lab.Components.OrderByDescending(c => c.Area))
        {
            if (c.Right - c.Left + 1 < 0.6 * W || c.Bottom - c.Top + 1 < 0.6 * H) continue;
            bool Side(bool horizontal, bool far)
            {
                int from = horizontal ? c.Left : c.Top, to = horizontal ? c.Right : c.Bottom, hit = 0;
                for (int a = from; a <= to; a++)
                {
                    bool any = false;
                    for (int k = 0; k < band && !any; k++)
                    {
                        int across = horizontal ? (far ? c.Bottom - k : c.Top + k) : (far ? c.Right - k : c.Left + k);
                        int x = horizontal ? a : across, y = horizontal ? across : a;
                        if (lab.Labels[y * W + x] == c.Label) any = true;
                    }
                    if (any) hit++;
                }
                return hit >= 0.9 * (to - from + 1);
            }
            if (!Side(true, false) || !Side(true, true) || !Side(false, false) || !Side(false, true)) continue;
            for (int i = 0; i < mask.Pixels.Length; i++) if (lab.Labels[i] == c.Label) mask.Pixels[i] = 0;
            return new PixelBox(c.Left, c.Top, c.Right, c.Bottom);
        }
        return null;
    }

    /// <summary>Removes a dot grid: at least 25 dot-sized components, most of them on one lattice. Returns how many
    /// were removed.</summary>
    internal static int RemoveGrid(BinaryImage mask, double w)
    {
        var lab = Components.Label(mask);
        double side = 2 * w + 2;
        var dots = lab.Components.Where(c => c.Right - c.Left + 1 <= side && c.Bottom - c.Top + 1 <= side).ToList();
        if (dots.Count < 25) return 0;

        int Pitch(bool alongX)
        {
            var steps = new Dictionary<int, int>();
            foreach (var a in dots)
            {
                double best = double.MaxValue;
                foreach (var b in dots)
                {
                    double d = alongX ? b.CentroidX - a.CentroidX : b.CentroidY - a.CentroidY;
                    double off = alongX ? Math.Abs(b.CentroidY - a.CentroidY) : Math.Abs(b.CentroidX - a.CentroidX);
                    if (d >= 3 * w && off <= 1.5 && d < best) best = d;
                }
                if (best == double.MaxValue) continue;
                int k = (int)Math.Round(best);
                steps[k] = steps.TryGetValue(k, out int n) ? n + 1 : 1;
            }
            return steps.Count == 0 ? 0 : steps.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).First().Key;
        }
        int px = Pitch(true), py = Pitch(false);
        if (px == 0 || py == 0) return 0;

        double Phase(Func<Component, double> at, int pitch) =>
            dots.GroupBy(c => (int)Math.Round(((at(c) % pitch) + pitch) % pitch))
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key;
        double phx = Phase(c => c.CentroidX, px), phy = Phase(c => c.CentroidY, py);
        bool On(double v, double phase, int pitch)
        {
            double m = ((v - phase) % pitch + pitch) % pitch;
            return Math.Min(m, pitch - m) <= 1.5;
        }
        var lattice = dots.Where(c => On(c.CentroidX, phx, px) && On(c.CentroidY, phy, py)).Select(c => c.Label).ToHashSet();
        if (lattice.Count < 25 || lattice.Count < 0.6 * dots.Count) return 0;
        for (int i = 0; i < mask.Pixels.Length; i++) if (lattice.Contains(lab.Labels[i])) mask.Pixels[i] = 0;
        return lattice.Count;
    }
}
