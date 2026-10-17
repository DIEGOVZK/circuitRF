using CircuitRF.Design.Symbol;

namespace CircuitRF.Design.Schematic;

// ──────────────────────────────────────────────────────────────────────────────
//  Canvas objects (§3.1) — bitmaps, text, shape primitives.
//  All are framework-free POCOs. No Avalonia types.
// ──────────────────────────────────────────────────────────────────────────────

public enum CanvasObjectKind { Bitmap, Text, Rect, Circle, Line }

/// <summary>
/// Base class for all canvas objects (§3.1 shared contract):
/// selectable, drag-move, resize, rotate, transparency, Z-order, lock.
/// </summary>
public abstract class EditableCanvasObject
{
    public string Id        { get; } = Guid.NewGuid().ToString("N")[..12];
    public abstract CanvasObjectKind Kind { get; }

    // Placement: centre position, size, rotation angle (degrees), transparency [0,1]
    public double X           { get; set; }
    public double Y           { get; set; }
    public double Width       { get; set; } = 300.0;
    public double Height      { get; set; } = 200.0;
    public double RotationDeg { get; set; }
    public double Transparency { get; set; }   // 0 = fully opaque, 1 = fully transparent
    public bool   IsLocked    { get; set; }
    public int    ZOrder      { get; set; }    // higher = drawn later (on top)

    public virtual (double MinX, double MinY, double MaxX, double MaxY) GetBoundingBox()
        => (X - Width / 2, Y - Height / 2, X + Width / 2, Y + Height / 2);

    public abstract EditableCanvasObject Clone();
}

/// <summary>
/// Bitmap canvas object (§3.1.1).
/// Persists only the file path; pixels are reloaded on open.
/// Resized by the layout bitmap's 8 scale handles (SchematicScaleHandles).
/// </summary>
public sealed class EditableBitmap : EditableCanvasObject
{
    public override CanvasObjectKind Kind => CanvasObjectKind.Bitmap;

    /// <summary>File path (relative preferred, absolute allowed) — never pixels.</summary>
    public string ImagePath { get; set; } = "";

    public override EditableCanvasObject Clone() => new EditableBitmap
    {
        X = X, Y = Y, Width = Width, Height = Height,
        RotationDeg = RotationDeg, Transparency = Transparency,
        IsLocked = IsLocked, ZOrder = ZOrder,
        ImagePath = ImagePath,
    };
}

/// <summary>
/// Free text on the schematic — the symbol editor's <see cref="TextPrimitive"/>, placed in world
/// coordinates. The fields are the primitive's, one for one, so the two editors offer the same text:
/// <see cref="ToPrimitive"/> is what the renderer draws and what the bounding box is measured from,
/// which is the code the symbol editor already draws and hit-tests its own text with.
///
/// <para><b>X/Y is the ANCHOR</b> — the (<see cref="Align"/>, <see cref="VAlign"/>) corner of the text
/// box, exactly as <see cref="TextPrimitive.AnchorX"/> is — not a centre. The rotation is the base
/// class's <see cref="EditableCanvasObject.RotationDeg"/> read in quarter turns, so the schematic's
/// rigid group rotate turns a text with everything else and nothing new is persisted for it.
/// <see cref="EditableCanvasObject.Width"/>/<see cref="EditableCanvasObject.Height"/> mean nothing
/// here: a text's size is its content and font size, as it is in the symbol editor.</para>
/// </summary>
public sealed class EditableText : EditableCanvasObject
{
    public override CanvasObjectKind Kind => CanvasObjectKind.Text;

    /// <summary>The schematic's own lettering height (<see cref="SchematicComponent.LabelWorldHeight"/>),
    /// so a note placed beside a part reads at the size the part's own labels do.</summary>
    public const double DefaultFontSize = SchematicComponent.LabelWorldHeight;

    public string           Text      { get; set; } = "Text";
    public double           FontSize  { get; set; } = DefaultFontSize;
    public SymbolFontStyle  FontStyle { get; set; }
    public SymbolTextAlign  Align     { get; set; } = SymbolTextAlign.Left;
    public SymbolTextVAlign VAlign    { get; set; } = SymbolTextVAlign.Top;
    public SymbolColorRole  ColorRole { get; set; } = SymbolColorRole.SymbolLine;

    /// <summary>The quarter turn <see cref="EditableCanvasObject.RotationDeg"/> names — R90 is the
    /// turn the schematic's R key applies, (x, y) → (−y, x), the same sense as the primitive's.</summary>
    public SymbolRotation Rotation
    {
        get => ((((int)Math.Round(RotationDeg / 90.0)) % 4 + 4) % 4) switch
        {
            1 => SymbolRotation.R90,
            2 => SymbolRotation.R180,
            3 => SymbolRotation.R270,
            _ => SymbolRotation.R0,
        };
        set => RotationDeg = value switch
        {
            SymbolRotation.R90  =>  90.0,
            SymbolRotation.R180 => 180.0,
            SymbolRotation.R270 => 270.0,
            _                   =>   0.0,
        };
    }

    /// <summary>This text as the symbol editor's primitive, anchored at (X, Y) in world units.</summary>
    public TextPrimitive ToPrimitive() => new()
    {
        Content   = Text,
        AnchorX   = X,
        AnchorY   = Y,
        FontSize  = FontSize,
        FontStyle = FontStyle,
        Align     = Align,
        VAlign    = VAlign,
        Rotation  = Rotation,
        ColorRole = ColorRole,
    };

    /// <summary>A symbol's text primitive as schematic text, anchored where the primitive is. Every
    /// field crosses but <see cref="TextPrimitive.ForceReadable"/>, which follows a rotated instance and
    /// so means nothing outside a symbol.</summary>
    public static EditableText FromPrimitive(TextPrimitive t) => new()
    {
        Text      = t.Content,
        X         = t.AnchorX,
        Y         = t.AnchorY,
        FontSize  = t.FontSize,
        FontStyle = t.FontStyle,
        Align     = t.Align,
        VAlign    = t.VAlign,
        Rotation  = t.Rotation,
        ColorRole = t.ColorRole,
    };

    public override (double MinX, double MinY, double MaxX, double MaxY) GetBoundingBox()
        => SymbolGeometry.BboxOf(ToPrimitive());

    public override EditableCanvasObject Clone() => new EditableText
    {
        X = X, Y = Y, Width = Width, Height = Height,
        RotationDeg = RotationDeg, Transparency = Transparency,
        IsLocked = IsLocked, ZOrder = ZOrder,
        Text = Text, FontSize = FontSize, FontStyle = FontStyle,
        Align = Align, VAlign = VAlign, ColorRole = ColorRole,
    };
}

public enum PrimitiveShape { Rect, Circle, Line }
public enum ArrowheadStyle { None, Start, End, Both }

/// <summary>
/// Shape primitive (§3.1.3) — rectangle, circle, or line.
/// Line has two draggable endpoint control points and optional arrowheads.
/// </summary>
public sealed class EditablePrimitive : EditableCanvasObject
{
    public override CanvasObjectKind Kind => CanvasObjectKind.Line; // refined below

    public PrimitiveShape Shape      { get; set; }
    public float          LineWidth  { get; set; } = 2f;
    public uint           ColorArgb  { get; set; } = 0xFF202020;

    // Line-only
    public ArrowheadStyle Arrowheads  { get; set; }
    public float          ArrowSize   { get; set; } = 12f;
    // Line endpoint control points (world coords; used instead of X/Y/Width/Height for lines)
    public double P1X { get; set; }
    public double P1Y { get; set; }
    public double P2X { get; set; }
    public double P2Y { get; set; } = 200.0;

    public override EditableCanvasObject Clone() => new EditablePrimitive
    {
        X = X, Y = Y, Width = Width, Height = Height,
        RotationDeg = RotationDeg, Transparency = Transparency,
        IsLocked = IsLocked, ZOrder = ZOrder,
        Shape = Shape, LineWidth = LineWidth, ColorArgb = ColorArgb,
        Arrowheads = Arrowheads, ArrowSize = ArrowSize,
        P1X = P1X, P1Y = P1Y, P2X = P2X, P2Y = P2Y,
    };
}
