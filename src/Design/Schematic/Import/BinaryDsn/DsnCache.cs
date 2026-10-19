// The Cache stream: each placed symbol's drawing and pins — brief-schimp-4 R-si4-3.
// Versions 2.0 and 3.2; src/Design/RESOLVED.md has the layout.
//
// A symbol record (version 2; version 3 wraps each record and each graphic in a sized prefix and opens each
// body with a counted block, see DsnBytes.Record and SkipBlob):
//   type (0x18 a part's view, 0x21–0x24 a global/port/connector), a (name, value) pair list, the NAME ("C.Normal",
//   "0"), the LIBRARY it came from, a u32 colour, a u16 count of graphics and the graphics, a u32 (zero), the
//   body's WIDTH and HEIGHT, a u16 count of pins and the pins.
//   A pin: 0x1A, a pair list, its name, start and hotspot (four i32), a u32, a u32 shape, a u32, a display-property list.
//   Coordinates are 1/100 inch (one grid square = 10), Y DOWN, relative to the body's top-left.
//
// Records are FOUND by name: each occurrence of the wanted name is tried as a record and accepted only when the whole
// symbol parses. In version 2 an unknown graphic is a refusal — with no length, nothing after it can be read; in version 3
// it is skipped by its size and counted.

namespace CircuitRF.Design.Schematic.Import.BinaryDsn;

/// <summary>One graphic in source units (1/100 inch, Y down).</summary>
internal abstract record DsnGraphic;
internal sealed record DsnLine(int X1, int Y1, int X2, int Y2) : DsnGraphic;
/// <summary>An arc of the ellipse inscribed in the box, counter-clockwise on screen from the start point to the end point.</summary>
internal sealed record DsnArc(int X1, int Y1, int X2, int Y2, int Sx, int Sy, int Ex, int Ey) : DsnGraphic;
internal sealed record DsnEllipse(int X1, int Y1, int X2, int Y2) : DsnGraphic;
internal sealed record DsnPolyline(IReadOnlyList<(int X, int Y)> Points) : DsnGraphic;

internal sealed record DsnPin(string Name, (int X, int Y) Start, (int X, int Y) Hot, int Shape);

internal sealed record DsnSymbol(string Name, string Library, IReadOnlyList<DsnGraphic> Graphics, int Width, int Height,
                                 IReadOnlyList<DsnPin> Pins, int SkippedGraphics);

internal sealed class DsnCache(byte[] bytes, EdifText text, int major)
{
    private static ReadOnlySpan<byte> Magic => [0xFF, 0xE4, 0x5C, 0x39];

    private readonly Dictionary<string, DsnSymbol?> _found = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The symbol named <paramref name="name"/> (case-insensitive, as the editor's own lookup is), or null.</summary>
    public DsnSymbol? Find(string name)
    {
        if (_found.TryGetValue(name, out var known)) return known;
        DsnSymbol? symbol = null;
        var needle = System.Text.Encoding.Latin1.GetBytes(name);
        for (int at = IndexOfName(needle, 0); symbol is null && at >= 0; at = IndexOfName(needle, at + 3))
            symbol = major >= 3 ? TryAtV3(at) : TryAtV2(at);
        _found[name] = symbol;
        return symbol;
    }

    /// <summary>The position of the u16 length of the next occurrence of the name — a length, the name, its NUL — whose
    /// name starts at or after <paramref name="from"/>.</summary>
    private int IndexOfName(byte[] needle, int from)
    {
        for (int i = Math.Max(from, 2); i + needle.Length < bytes.Length; i++)
        {
            if ((bytes[i - 2] | bytes[i - 1] << 8) != needle.Length || bytes[i + needle.Length] != 0) continue;
            if (System.Text.Ascii.EqualsIgnoreCase(bytes.AsSpan(i, needle.Length), needle)) return i - 2;
        }
        return -1;
    }

    /// <summary>Version 2: a symbol whose name's length prefix is at <paramref name="nameAt"/>, if one parses there. The
    /// record starts with its type and a pair list ending where the name begins: n pairs of 4 bytes.</summary>
    private DsnSymbol? TryAtV2(int nameAt)
    {
        for (int n = 0; n <= 64; n++)
        {
            int start = nameAt - 3 - 4 * n;
            if (start < 0) break;
            byte type = bytes[start];
            if (type is not (0x18 or >= 0x21 and <= 0x24) || (bytes[start + 1] | bytes[start + 2] << 8) != n) continue;
            try { return Body(new DsnBytes(bytes, "Cache", text) { Pos = nameAt }); }
            catch (DsnFormatException) { }
        }
        return null;
    }

    /// <summary>Version 3: a body is the header's end marker, a counted block (u32 n, n bytes), then the name.</summary>
    private DsnSymbol? TryAtV3(int nameAt)
    {
        for (int n = 0; n <= 256 && nameAt - 8 - n >= 0; n++)
        {
            if (!bytes.AsSpan(nameAt - 8 - n, 4).SequenceEqual(Magic)) continue;
            if (BitConverter.ToUInt32(bytes, nameAt - 4 - n) != n) continue;
            try { return Body(new DsnBytes(bytes, "Cache", text) { Pos = nameAt }); }
            catch (DsnFormatException) { return null; }
        }
        return null;
    }

    private DsnSymbol Body(DsnBytes r)
    {
        string name = r.Str();
        string library = r.Str();
        if (library.Length < 3) throw r.Fail("no library path");
        r.U32();                                 // colour
        int ng = r.U16();
        if (ng > 10_000) throw r.Fail($"a symbol claims {ng} graphics");
        var graphics = new List<DsnGraphic>(ng);
        int skipped = 0;
        for (int i = 0; i < ng; i++)
            if (Graphic(r) is { } g) graphics.Add(g);
            else skipped++;
        r.U32();
        int w = r.U16(), h = r.U16();
        int np = r.U16();
        if (np > 10_000) throw r.Fail($"a symbol claims {np} pins");
        var pins = new List<DsnPin>(np);
        for (int i = 0; i < np; i++)
        {
            int end = -1;
            if (major >= 3) { end = r.Record(0x1A).End; r.SkipBlob(); }
            else { r.Expect(0x1A, "a pin"); r.Pairs(); }
            string pinName = r.Str();
            var start = (r.I32(), r.I32());
            var hot = (r.I32(), r.I32());
            r.U32();
            int shape = (int)r.U32();
            if (major >= 3) r.Pos = end;
            else { r.U32(); r.DisplayProps(); }
            pins.Add(new DsnPin(pinName, start, hot, shape));
        }
        return new DsnSymbol(name, library, graphics, w, h, pins, skipped);
    }

    /// <summary>One graphic; null for a version-3 kind this reader does not draw (skipped by its size).</summary>
    private DsnGraphic? Graphic(DsnBytes r)
    {
        int at = r.Pos;
        byte type = r.U8();
        int end = -1;
        if (major >= 3)
        {
            // The kind, then a sized prefix of the same kind; the payload starts with version 2's fields.
            uint size = (r.Peek() == type) ? BitConverterSize(r) : throw r.Fail("a graphic with no sized prefix", at);
            end = r.Pos + (int)size;
            if (end > r.Length) throw r.Fail("a graphic that runs past its stream", at);
        }
        DsnGraphic? g = type switch
        {
            0x29 => Line(r),
            0x2A => new DsnArc(r.I32(), r.I32(), r.I32(), r.I32(), r.I32(), r.I32(), r.I32(), r.I32()),
            0x2B => new DsnEllipse(r.I32(), r.I32(), r.I32(), r.I32()),
            0x2D => Polyline(r),
            _ when major >= 3 => null,
            _ => throw r.Fail($"a symbol graphic of type 0x{type:X2}, which this reader has not seen", at),
        };
        if (major >= 3) r.Pos = end;
        else if (g is DsnArc) r.Skip(8);         // style, width
        else if (g is DsnEllipse) r.Skip(16);    // style, width, fill, hatch
        return g;

        static DsnLine Line(DsnBytes r)
        {
            var l = new DsnLine(r.I32(), r.I32(), r.I32(), r.I32());
            r.Skip(8);                           // style, width
            return l;
        }

        static DsnPolyline Polyline(DsnBytes r)
        {
            r.Skip(8);                           // style, width
            int n = r.U16();
            if (n > 10_000) throw r.Fail($"a polyline claims {n} points");
            var pts = new List<(int, int)>(n);
            for (int i = 0; i < n; i++) pts.Add((r.I16(), r.I16()));
            return new DsnPolyline(pts);
        }

        // type, u32 size, u32 0 — returns the size, the cursor on the payload.
        static uint BitConverterSize(DsnBytes r)
        {
            int at = r.Pos;
            r.U8();
            uint size = r.U32();
            if (r.U32() != 0) throw r.Fail("a graphic's sized prefix", at);
            return size;
        }
    }
}
