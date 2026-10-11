// What a schematic picture's reading found — brief-img-7-schematic-wires-and-regions.md R-im7-8 (overview D16).
//
// Everything that is not a refusal is a count. The two refusals are the only ways a schematic picture fails here: no
// line work that reads as wires, or wires with nothing drawn between them.

using System.Globalization;

namespace CircuitRF.Design.Schematic.Recognition;

/// <summary>The counts of one reading.</summary>
public sealed record SchematicImageReport
{
    public const string NoWiresRefusal = "No connected line work: this picture has no wires to read.";

    public const string NoSymbolsRefusal =
        "No symbols: this picture has wires but nothing drawn between them to read as a part.";

    public const string CurvedLinesRefusal =
        "No circuit: most of the lines in this picture curve or slant, as a chart's or a drawing's do; a schematic's wires run across and down.";

    /// <summary>The stroke width w the line work was read at, the source picture's pixels.</summary>
    public double StrokeWidth { get; init; }

    /// <summary>The picture's own scales (R-imf2-1); null before they are measured.</summary>
    public PictureProfile? Profile { get; init; }

    /// <summary>How far the picture was turned to straighten it, degrees (0: it was not).</summary>
    public double DeskewDeg { get; init; }

    /// <summary>Dots of a background grid removed.</summary>
    public int GridDotsRemoved { get; init; }
    public int HatchesClosed { get; init; }

    /// <summary>Whether a border frame (and the title block drawn on it) was removed.</summary>
    public bool BorderRemoved { get; init; }

    public int Words { get; init; }

    /// <summary>Text-sized pieces touching line work, kept as symbol details rather than read as words.</summary>
    public int WordsKeptAsSymbolDetail { get; init; }

    public int WireSegments { get; init; }

    /// <summary>Of <see cref="WireSegments"/>, those neither horizontal nor vertical.</summary>
    public int DiagonalWires { get; init; }

    public int JunctionDots { get; init; }

    public int CrossingsConnected { get; init; }
    public int CrossingsNotConnected { get; init; }
    public int Hops { get; init; }

    public int SymbolRegions { get; init; }

    /// <summary>Large filled areas kept as their regions' fills, not read as ink (R-imf2-4).</summary>
    public int FillsKept { get; init; }
    public int DecorationsRemoved { get; init; }
    public int NetLabels { get; init; }
    public int SupplyMarks { get; init; }
    public int DanglingEnds { get; init; }

    // ── What is not wire (R-imf3-7) ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Plates a lead meets (R-imf3-1), at any length — a capacitor's, a ground's top bar.</summary>
    public int Plates { get; init; }

    /// <summary>Plate pairs found side by side across a clear strip, and curved plates.</summary>
    public int PlatePairs { get; init; }
    public int CurvedPlates { get; init; }

    /// <summary>Closed outlines set aside as symbols (R-imf3-2).</summary>
    public int Boxes { get; init; }

    /// <summary>Arrows by class (R-imf3-3).</summary>
    public int DimensionArrows { get; init; }
    public int CurrentArrows { get; init; }
    public int VoltageArrows { get; init; }
    public int TerminalArrows { get; init; }
    public int SupplyArrows { get; init; }
    public int SymbolArrows { get; init; }

    /// <summary>Dashed lines by role (R-imf3-4): removed as annotations, or read as wires.</summary>
    public int DashedAnnotations { get; init; }
    public int DashedWires { get; init; }

    /// <summary>Zig-zag chains whose slanted legs were left out of the curved-lines count (R-imf3-5).</summary>
    public int ZigzagChains { get; init; }

    /// <summary>Junction marks by shape (R-imf3-6).</summary>
    public int RoundJunctions { get; init; }
    public int SquareJunctions { get; init; }
    public int DiamondJunctions { get; init; }

    // ── Regions that stop at parts (R-imf4-6) ───────────────────────────────────────────────────────────────────

    /// <summary>Loops of wire read as wire because a junction dot stands on them, not as a symbol's outline.</summary>
    public int WireLoops { get; init; }

    /// <summary>Regions closed on their outlines — boxes, triangles — and envelope circles (R-imf4-2, R-imf4-3).</summary>
    public int ClosedBoxes { get; init; }
    public int ClosedTriangles { get; init; }
    public int EnvelopeCircles { get; init; }

    /// <summary>Pieces merged into one region, and merges refused by each rule of R-imf4-1 (and R-imf4-2's outline).</summary>
    public int RegionsMerged { get; init; }
    public int RefusedGap { get; init; }
    public int RefusedLead { get; init; }
    public int RefusedSize { get; init; }
    public int RefusedPins { get; init; }
    public int RefusedOutline { get; init; }

    /// <summary>Glyphs lifted off the symbols they touch into the words (R-imf4-4).</summary>
    public int GlyphsLifted { get; init; }

    /// <summary>Runaway regions split (R-imf4-5), and how many regions they became; and those no cut could split, left as
    /// S-parameter blocks.</summary>
    public int RegionsSplit { get; init; }
    public int RegionsSplitInto { get; init; }
    public int Blocks { get; init; }

    public const string DashedWireNote = "A dashed line was read as a wire; it usually stands for repeated sections left out.";

    /// <summary>The report in words, one finding per line; a count of nothing is not a line.</summary>
    public IReadOnlyList<string> Lines()
    {
        var r = new List<string>
        {
            "Stroke width " + StrokeWidth.ToString("0.#", CultureInfo.InvariantCulture) + " px.",
        };
        if (Profile is { } profile) r.AddRange(profile.Lines());
        if (DeskewDeg != 0)
            r.Add("Straightened by " + DeskewDeg.ToString("0.##", CultureInfo.InvariantCulture) + "°.");
        if (GridDotsRemoved > 0) r.Add($"Background grid removed ({Count(GridDotsRemoved, "dot")}).");
        if (HatchesClosed > 0) r.Add($"{Count(HatchesClosed, "hatched stroke")} closed into the {(HatchesClosed == 1 ? "line it draws" : "lines they draw")}.");
        if (BorderRemoved) r.Add("Border frame removed.");
        r.Add(Count(Words, "word") + " found.");
        if (WordsKeptAsSymbolDetail > 0)
            r.Add($"{Count(WordsKeptAsSymbolDetail, "text-sized piece")} touching line work kept as symbol detail.");
        r.Add(Count(WireSegments, "wire segment") + (DiagonalWires > 0 ? $", {DiagonalWires} diagonal." : "."));
        if (JunctionDots > 0)
            r.Add(Count(JunctionDots, "junction dot") + (SquareJunctions + DiamondJunctions == 0 ? "." :
                  " (" + string.Join(", ", new[] { (RoundJunctions, "round"), (SquareJunctions, "square"), (DiamondJunctions, "diamond") }
                                               .Where(t => t.Item1 > 0).Select(t => $"{t.Item1} {t.Item2}")) + ")."));
        if (CrossingsConnected + CrossingsNotConnected > 0)
            r.Add($"Crossings: {CrossingsConnected} connected, {CrossingsNotConnected} not connected.");
        if (Hops > 0) r.Add(Count(Hops, "hop") + ", not connected.");
        if (Plates + PlatePairs + CurvedPlates > 0)
            r.Add("Plates and bars, not wire: " + string.Join(", ", new[]
            {
                Plates > 0 ? $"{Plates} met by a lead" : null,
                PlatePairs > 0 ? Count(PlatePairs, "pair") + " side by side" : null,
                CurvedPlates > 0 ? $"{CurvedPlates} curved" : null,
            }.Where(x => x is not null)) + ".");
        if (Boxes > 0) r.Add(Count(Boxes, "closed outline") + " set aside as symbols.");
        var arrows = new[]
        {
            (DimensionArrows, "dimension"), (CurrentArrows, "current"), (VoltageArrows, "voltage"),
            (TerminalArrows, "into a terminal"), (SupplyArrows, "supply"), (SymbolArrows, "on a symbol"),
        }.Where(t => t.Item1 > 0).ToList();
        if (arrows.Count > 0)
            r.Add("Arrows: " + string.Join(", ", arrows.Select(t => $"{t.Item1} {t.Item2}")) + "; annotations, not wire.");
        if (DashedAnnotations > 0) r.Add(Count(DashedAnnotations, "dashed line") + " removed as an annotation (a box or a boundary).");
        if (DashedWires > 0) r.Add(DashedWireNote);
        if (ZigzagChains > 0) r.Add(Count(ZigzagChains, "zig-zag") + " not counted as curved lines.");
        if (WireLoops > 0) r.Add(Count(WireLoops, "loop") + " of wire through a junction dot read as wire, not an outline.");
        r.Add(Count(SymbolRegions, "symbol region") + ".");
        if (ClosedBoxes + ClosedTriangles + EnvelopeCircles > 0)
            r.Add("Regions closed on their outlines: " + string.Join(", ", new[]
            {
                ClosedBoxes > 0 ? Count(ClosedBoxes, "box", "boxes") : null,
                ClosedTriangles > 0 ? Count(ClosedTriangles, "triangle") : null,
                EnvelopeCircles > 0 ? Count(EnvelopeCircles, "circle") + " round a part" : null,
            }.Where(x => x is not null)) + ".");
        if (RegionsMerged > 0) r.Add(Count(RegionsMerged, "piece") + " merged into the region beside " + (RegionsMerged == 1 ? "it." : "them."));
        var refused = new[]
        {
            (RefusedGap, "too far apart"), (RefusedLead, "a lead on the facing side"), (RefusedSize, "too large together"),
            (RefusedPins, "too many wire ends together"), (RefusedOutline, "outside a closed outline"),
        }.Where(t => t.Item1 > 0).ToList();
        if (refused.Count > 0)
            r.Add("Merges refused: " + string.Join(", ", refused.Select(t => $"{t.Item1} {t.Item2}")) + ".");
        if (GlyphsLifted > 0) r.Add(Count(GlyphsLifted, "glyph") + " lifted off the symbols " + (GlyphsLifted == 1 ? "it touched" : "they touched") + " into the words.");
        if (RegionsSplit > 0)
            r.Add(RegionsSplit == 1 ? $"A region was split into {RegionsSplitInto}." : $"{RegionsSplit} regions were split into {RegionsSplitInto}.");
        if (Blocks > 0)
            r.Add(Count(Blocks, "region") + " with too many wire ends and no cut to split " + (Blocks == 1 ? "it, read" : "them, read") + " as an S-parameter block.");
        if (FillsKept > 0) r.Add($"{Count(FillsKept, "filled area")} kept as {(FillsKept == 1 ? "its symbol's fill" : "their symbols' fills")}, not ink.");
        if (DecorationsRemoved > 0) r.Add($"{Count(DecorationsRemoved, "decoration")} with no wire removed.");
        if (NetLabels > 0) r.Add(Count(NetLabels, "net label") + ".");
        if (SupplyMarks > 0) r.Add(Count(SupplyMarks, "supply mark") + ".");
        if (DanglingEnds > 0) r.Add(Count(DanglingEnds, "dangling wire end") + ".");
        return r;
    }

    private static string Count(int n, string noun) => n.ToString(CultureInfo.InvariantCulture) + " " + noun + (n == 1 ? "" : "s");

    private static string Count(int n, string one, string many) => n.ToString(CultureInfo.InvariantCulture) + " " + (n == 1 ? one : many);
}
