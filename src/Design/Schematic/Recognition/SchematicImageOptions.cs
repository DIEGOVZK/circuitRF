// How a schematic picture is read — brief-img-7-schematic-wires-and-regions.md R-im7-2 … R-im7-7 (overview D11, D16),
// brief-imf-2-picture-scale-profile.md R-imf2-2, R-imf2-5.
//
// Every tolerance is a number of pens w AT THE REFERENCE PROFILE (PictureProfile: text 5–7.5 w tall, a symbol scale of
// 9–26 w — the pictures every default was measured on), and each belongs to one of three classes, named on it:
//   pen    — wire geometry: residuals, dots, how near an end reaches. Always in w.
//   symbol — sized by the drawing's symbols: in s, PictureProfile.SymbolFactor times the number below.
//   text   — sized by the drawing's type: in h, PictureProfile.TextFactor times the number below.
// A picture inside the reference band reads with exactly these numbers. The mapping is design note §18.2's table.
// Every field has the dialog's default.

using CircuitRF.Design.Imaging;

namespace CircuitRF.Design.Schematic.Recognition;

/// <summary>When a four-way crossing connects (D11). A T always connects; a hop never does.</summary>
public enum CrossingRule
{
    /// <summary>A four-way crossing never connects.</summary>
    Never,

    /// <summary>A four-way crossing connects only where a junction dot sits on it — the default.</summary>
    WithDot,

    /// <summary>Every four-way crossing connects.</summary>
    Always,
}

public sealed record SchematicImageOptions
{
    /// <summary>The binarisation (R-im1-3).</summary>
    public LineArtOptions LineArt { get; init; } = new();

    /// <summary>The picture's scales: measured (Auto), or any of them stated in the source picture's pixels (R-imf2-5).
    /// A stated value replaces only itself.</summary>
    public PictureProfileOptions Profile { get; init; } = PictureProfileOptions.Auto;

    /// <summary>When a four-way crossing connects (R-im7-5).</summary>
    public CrossingRule Crossings { get; init; } = CrossingRule.WithDot;

    /// <summary>A straight run within this many degrees of 0°/90° is an orthogonal wire.</summary>
    public double SnapAngleDeg { get; init; } = 3.0;

    /// <summary>Straighten a picture whose long runs lean (R-im7-2).</summary>
    public bool Deskew { get; init; } = true;

    /// <summary>A lean at or below this many degrees is left alone — it is within the pixel staircase.</summary>
    public double MinDeskewDeg { get; init; } = 0.3;

    /// <summary>A lean above this many degrees is not a skew but a drawing at an angle, and is left alone.</summary>
    public double MaxDeskewDeg { get; init; } = 5.0;

    /// <summary>Remove a dot grid (engineering paper, a tool's snap grid) before reading.</summary>
    public bool RemoveGrid { get; init; } = true;

    /// <summary>Remove a frame enclosing most of the drawing, and the title block drawn on it.</summary>
    public bool RemoveBorder { get; init; } = true;

    // ── Text (R-im7-3) ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Text: a glyph is at least this many w tall.</summary>
    public double MinTextHeight { get; init; } = 2.0;

    /// <summary>Text: a glyph is at most this many w tall; a straight run longer than this is line work, never text.</summary>
    public double MaxTextHeight { get; init; } = 12.0;

    /// <summary>Text: a word touching line work along more than this many w is kept as a symbol detail.</summary>
    public double TextContact { get; init; } = 2.0;

    /// <summary>Profile: a drawing whose text stands taller than this many w is drawn in hairlines (circuitRF's own schematic,
    /// a vector export at a high zoom): its line work is thickened before wires and symbols are read (IM-12). 0 never
    /// thickens.</summary>
    public double HairlineTextHeight { get; init; } = 8.0;

    /// <summary>Profile: a hairline drawing is thickened until its text stands this many w tall — the weight every other
    /// tolerance here was measured at.</summary>
    public double TextHeightInStrokes { get; init; } = 5.0;

    // ── Wires (R-im7-4) ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Pen: an orthogonal straight run shorter than this many w is not a wire.</summary>
    public double MinWireLength { get; init; } = 3.0;

    /// <summary>Symbol: a diagonal straight run is a wire only above this many w — below it, a zig-zag's leg.</summary>
    public double MinDiagonalLength { get; init; } = 8.0;

    /// <summary>Pen: a run is straight when its pixels lie within this many w of its line (RMS).</summary>
    public double MaxResidual { get; init; } = 0.25;

    /// <summary>Symbol: a straight line no longer than this many w whose BOTH ends stop in nothing is a stroke of a symbol — a
    /// capacitor plate, a ground bar, a supply bar — not a wire.</summary>
    public double MaxSymbolStroke { get; init; } = 16.0;

    /// <summary>Pen: a junction dot's diameter is at least this many w …</summary>
    public double MinDot { get; init; } = 2.0;

    /// <summary>… and at most this many.</summary>
    public double MaxDot { get; init; } = 6.0;

    /// <summary>Pen: a junction dot is centred within this many w of the node.</summary>
    public double DotCentre { get; init; } = 1.0;

    // ── Symbols (R-im7-6, R-im7-7) ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Symbol: residue pieces are grown by this many w before they are joined.</summary>
    public double Grow { get; init; } = 1.0;

    /// <summary>Pen: a wire end stopping within this many w of a symbol region is attached to it.</summary>
    public double Attach { get; init; } = 1.5;

    /// <summary>Text: a word within this many w of a dangling end, in line with its wire, is the wire's net label.</summary>
    public double LabelGap { get; init; } = 2.0;

    /// <summary>Symbol: two wire ends facing each other across a wire, no further apart than this many w, are a hop.</summary>
    public double MaxHopSpan { get; init; } = 10.0;

    // ── Corrections (R-im10-8) ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>The user's corrections, in the source picture's pixels: a crossing set connected or not here; a dangling
    /// end joined to a pin in the circuit (IM-10's emit).</summary>
    public IReadOnlyList<ConnectionOverride> Overrides { get; init; } = [];

    public int MaxThreads { get; init; }
}
