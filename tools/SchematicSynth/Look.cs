using SkiaSharp;

namespace CircuitRF.SchematicSynth;

/// <summary>
/// One picture's draw from its style pack: a palette, pens, a face and size, a label stack and placement, a value
/// spelling, a dot and pin-marker style and — per class, on first use — one glyph. Every part of one class is drawn the
/// same way in one picture, as an editor draws them.
/// </summary>
public sealed class Look
{
    private readonly StylePack _pack;
    private readonly Random _r;
    private readonly Dictionary<string, Glyph?> _glyphs = new(StringComparer.Ordinal);

    public StylePack Pack => _pack;
    public required Palette Palette { get; init; }
    public SKColor Paper { get; init; }
    public SKColor GridColour { get; init; }
    public SKColor Wire { get; init; }
    public SKColor Symbol { get; init; }
    public SKColor Text { get; init; }
    public SKColor Type { get; init; }
    public SKColor Fill { get; init; }
    public SKColor Accent { get; init; }
    public string Grid { get; init; } = "none";
    public double GridPitch { get; init; } = 100;
    public double WireShare { get; init; }
    public double SymbolShare { get; init; }
    public double MinPx { get; init; } = 1;
    public SKStrokeCap Cap { get; init; }
    public bool Antialias { get; init; } = true;
    public double SymbolScale { get; init; } = 1;
    public required SKTypeface Face { get; init; }
    public required SKTypeface UiFace { get; init; }
    public double TextSize { get; init; } = 60;
    public required LabelStack Stack { get; init; }
    public string Placement { get; init; } = "side";
    public bool Rotated { get; init; }
    public string ValueForm { get; init; } = "named";
    public bool Spaced { get; init; }
    public string Ohm { get; init; } = "Ohm";
    public string Micro { get; init; } = "u";
    public bool Subscripts { get; init; }
    public bool Tags { get; init; }
    public string Dots { get; init; } = "circle";
    public double DotSize { get; init; } = 10;
    public string PinMarker { get; init; } = "none";
    public bool Hops { get; init; }

    private Look(StylePack pack, Random r)
    {
        _pack = pack;
        _r = r;
    }

    /// <summary>The glyph this picture draws <paramref name="cls"/> with: the pack's choice, else the library's
    /// <c>&lt;class&gt;.plain</c> — a pack need not draw every class a circuit can hold.</summary>
    public Glyph Glyph(string cls)
    {
        if (!_glyphs.TryGetValue(cls, out var g))
        {
            g = _pack.GlyphFor(cls, _r) ?? GlyphLibrary.Embedded.Glyphs.GetValueOrDefault(cls + ".plain")
                ?? throw new InvalidOperationException($"No glyph for class {cls}: the glyph library needs {cls}.plain");
            _glyphs[cls] = g;
        }
        return g!;
    }

    /// <summary>Whether this pack draws <paramref name="cls"/> itself (not by the library's fallback).</summary>
    public bool Draws(string cls) => _pack.Glyphs.ContainsKey(cls);

    private static SKColor Jitter(SKColor c, Random r, int spread)
    {
        byte J(byte v) => (byte)Math.Clamp(v + r.Next(-spread, spread + 1), 0, 255);
        return new SKColor(J(c.Red), J(c.Green), J(c.Blue));
    }

    public static Look Draw(StylePack pack, FontBook fonts, Random r)
    {
        var pal = pack.Palettes[r.Next(pack.Palettes.Count)];
        SKColor C(string s) => SKColor.Parse(s);
        string style = pack.Fonts.Pick(r);
        if (pack.Bold > 0 && r.NextDouble() < pack.Bold && !style.Contains("bold")) style += "-bold";
        return new Look(pack, r)
        {
            Palette = pal,
            Paper = Jitter(C(pal.Paper), r, 3),
            GridColour = C(pal.Grid),
            Wire = C(pal.Wire),
            Symbol = C(pal.Symbol),
            Text = C(pal.Text[r.Next(pal.Text.Count)]),
            Type = C(pal.Type),
            Fill = C(pal.Fill),
            Accent = C(pal.Accent),
            Grid = pack.Grid.Pick(r),
            GridPitch = 100 * pack.GridPitch.Pick(r),
            WireShare = pack.WirePen.Pick(r),
            SymbolShare = pack.SymbolPen.Pick(r),
            MinPx = pack.MinPx.Pick(r),
            Cap = pack.Caps.Pick(r) switch { "round" => SKStrokeCap.Round, "square" => SKStrokeCap.Square, _ => SKStrokeCap.Butt },
            Antialias = r.NextDouble() < pack.Antialias,
            SymbolScale = pack.SymbolScale.Pick(r),
            Face = fonts.Get(style, r),
            UiFace = fonts.Get("ui", r),
            TextSize = 100 * pack.TextSize.Pick(r),
            Stack = pack.Stacks.Pick(r),
            Placement = pack.Placement.Pick(r),
            Rotated = r.NextDouble() < pack.Rotated,
            ValueForm = pack.ValueForms.Pick(r),
            Spaced = r.NextDouble() < pack.Spaced,
            Ohm = pack.Ohm.Pick(r),
            Micro = pack.Micro.Pick(r),
            Subscripts = r.NextDouble() < pack.Subscripts,
            Tags = r.NextDouble() < pack.Tags,
            Dots = pack.Dots.Pick(r),
            DotSize = 100 * pack.DotSize.Pick(r),
            PinMarker = pack.PinMarkers.Pick(r),
            Hops = r.NextDouble() < pack.Hops,
        };
    }
}
