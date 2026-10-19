// One page stream (Views/<schematic>/Pages/<page>) of a version-2.0 design — brief-schimp-4 R-si4-3.
//
// Read IN ORDER, every section counted — so a count this reader reads only at zero is a refusal naming it, not a
// misread of whatever follows:
//
//   header       0x0A, an empty pair list, the page NAME and SIZE ("A"), two timestamps, a fixed block holding the
//                page width and height (1/1000 inch) and the grid, a 0x41 byte, a u16 count of u32s
//   title blocks u16 count; each 0x0E … with its displayed fields (not imported, overview D5)
//   13 bytes     0x40, one byte, eleven zeros
//   nets         u16 count of (u32 net id, 3 bytes) — net ids are per PAGE
//   (empty)      u16, zero
//   net names    u16 count of (name, u32 net id) — "0" for the simulation ground
//   wires        u16 count; each 0x14, 2 bytes, u32 id, u32 NET ID, u32 colour, x1 y1 x2 y2 (i32), a byte, a u16
//                alias count (zero), a display-property list
//   parts        u16 count; see Part()
//   (empty)      u16
//   globals      u16 count; ground and power symbols, see Global()
//   tail         six u16 counts, zero (ports, off-page connectors, graphics, …)
//
// Coordinates are 1/100 inch, Y down: one grid square is 10.

namespace CircuitRF.Design.Schematic.Import.BinaryDsn;

internal sealed record DsnWire(uint Id, uint Net, (int X, int Y) A, (int X, int Y) B);

/// <summary>A placed pin: its index in the symbol (1-based), where it is on the page, and the wire it touches (0: none).</summary>
internal sealed record DsnPlacedPin(int Index, (int X, int Y) At, uint Wire);

/// <summary>A placed part. <paramref name="Unit"/> is 1-based: a package of several units places each as its own part
/// with the same reference and its own symbol view (one per unit, its name ending A, B, …).</summary>
internal sealed record DsnPart(
    string Reference, string SymbolName, int ValueIndex, IReadOnlyList<(int Name, int Value)> Properties,
    (int X, int Y) Position, int Rotation, bool Mirror, IReadOnlyList<DsnPlacedPin> Pins, int Unit = 1);

/// <summary>A ground, power or off-page symbol: its symbol's name, the net it names (a string index), where it is, and the
/// wire it touches.</summary>
internal sealed record DsnGlobal(string SymbolName, int NetIndex, (int X, int Y) Position, int Rotation, bool Mirror, uint Wire);

/// <summary>A net alias: a name its wire carries, its text at <paramref name="At"/>.</summary>
internal sealed record DsnAlias(string Name, (int X, int Y) At, uint Wire);

/// <summary>One page. <paramref name="Grid"/> is the grid in the page size's unit, so the size in mils is size × 100 / grid.</summary>
internal sealed record DsnPage(
    string Name, int Width, int Height, int Grid, IReadOnlyList<(string Name, uint Net)> NetNames,
    IReadOnlyList<DsnWire> Wires, IReadOnlyList<DsnPart> Parts, IReadOnlyList<DsnGlobal> Globals)
{
    public IReadOnlyList<DsnGlobal> OffPages { get; init; } = [];
    public IReadOnlyList<DsnAlias> Aliases { get; init; } = [];

    /// <summary>What the page holds that is not read, one sentence each.</summary>
    public IReadOnlyList<string> Skipped { get; init; } = [];
}

internal static class DsnPageV2
{
    /// <summary>The fixed block between the page's timestamps and its 0x41 byte.</summary>
    private const int FixedHeader = 150;

    public static DsnPage Read(DsnBytes r)
    {
        r.Expect(0x0A, "a page");
        if (r.Pairs().Count != 0) throw r.Fail("a page with properties");
        string name = r.Str();
        r.Str();                                 // the page size's name ("A")
        r.Skip(8);                               // created, modified
        int fixedAt = r.Pos;
        r.Skip(16);
        int width = r.I32(), height = r.I32(), grid = r.I32();
        r.Pos = fixedAt + FixedHeader;
        r.Expect(0x41, "the end of the page header");
        int n = r.U16();
        r.Skip(4 * n);

        int titles = r.U16();
        for (int i = 0; i < titles; i++)
        {
            r.Expect(0x0E, "a title block");
            r.Skip(5);
            r.Str();
            r.Skip(20);                          // instance, box, colour and rotation
            r.DisplayProps();
        }
        int tailAt = r.Pos;
        if (r.U8() != 0x40) throw r.Fail("the title blocks' end", tailAt);
        r.U8();
        for (int i = 0; i < 11; i++)
            if (r.U8() != 0) throw r.Fail("content after the title blocks that this reader has not seen", tailAt);

        int nn = r.U16();
        r.Skip(7 * nn);                          // the nets: u32 id, 3 bytes — the wires name theirs
        r.ExpectEmpty("the list after the nets");
        int na = r.U16();
        var netNames = new List<(string, uint)>(na);
        for (int i = 0; i < na; i++) netNames.Add((r.Str(), r.U32()));

        int nw = r.U16();
        var wires = new List<DsnWire>(nw);
        for (int i = 0; i < nw; i++) wires.Add(Wire(r));
        int np = r.U16();
        var parts = new List<DsnPart>(np);
        for (int i = 0; i < np; i++) parts.Add(Part(r));
        r.ExpectEmpty("the list after the parts");
        int ng = r.U16();
        var globals = new List<DsnGlobal>(ng);
        for (int i = 0; i < ng; i++) globals.Add(Global(r));
        while (!r.AtEnd)
        {
            int at = r.Pos;
            if (r.U8() != 0) throw r.Fail("page content after the ground and power symbols that this reader has not seen (ports, "
                                        + "off-page connectors, graphics or text)", at);
        }
        return new DsnPage(name, width, height, grid, netNames, wires, parts, globals);
    }

    private static DsnWire Wire(DsnBytes r)
    {
        r.Expect(0x14, "a wire");
        if (r.Pairs().Count != 0) throw r.Fail("a wire with properties");
        uint id = r.U32(), net = r.U32();
        r.U32();                                 // colour
        var a = (r.I32(), r.I32());
        var b = (r.I32(), r.I32());
        r.U8();
        r.ExpectEmpty("a wire's alias list");
        r.DisplayProps();
        return new DsnWire(id, net, a, b);
    }

    /// <summary>
    /// 0x0D, the instance's (name, value) property pairs, u16 0, u16 library (string index), u32 timestamp, the symbol
    /// NAME ("R.Normal"), u32 instance, 8 bytes, x y (i16), colour, ROTATION (bits 0–1 the quarter turns counter-clockwise
    /// on screen, bit 2 the mirror), 2 bytes, display properties, a byte, the REFERENCE, u32 VALUE (string index), 4 bytes,
    /// u16 pin count and the pins (0x10, 2 bytes, u16 index, x y, u32 WIRE, 4 bytes, display properties), the source
    /// part's name, u16 UNIT (0-based).
    /// </summary>
    private static DsnPart Part(DsnBytes r)
    {
        r.Expect(0x0D, "a part");
        var pairs = r.Pairs();
        r.Skip(8);                               // 0, library, timestamp
        string symbol = r.Str();
        r.Skip(12);
        var pos = ((int)r.I16(), (int)r.I16());
        r.U8();
        int rot = r.U8();
        r.Skip(2);
        r.DisplayProps();
        r.U8();
        string reference = r.Str();
        int value = (int)r.U32();
        r.Skip(4);
        int np = r.U16();
        if (np > 10_000) throw r.Fail($"a part claims {np} pins");
        var pins = new List<DsnPlacedPin>(np);
        for (int i = 0; i < np; i++)
        {
            r.Expect(0x10, "a placed pin");
            r.Skip(2);
            int index = r.U16();
            var at = ((int)r.I16(), (int)r.I16());
            uint wire = r.U32();
            r.Skip(4);
            r.DisplayProps();
            pins.Add(new DsnPlacedPin(index, at, wire));
        }
        r.Str();
        int unit = r.U16();
        return new DsnPart(reference, symbol, value, pairs, pos, rot & 3, (rot & 4) != 0, pins, unit + 1);
    }

    /// <summary>0x25, a pair list, u16 NET NAME (string index), u16 library, u32 timestamp, the SYMBOL's name, u32 id, u32,
    /// the body's width and height, x y (i16), colour, rotation, 2 bytes, display properties, a byte, u32 WIRE, a byte.
    /// (The simulation ground's symbol is called "0", the net it names.)</summary>
    private static DsnGlobal Global(DsnBytes r)
    {
        r.Expect(0x25, "a ground or power symbol");
        r.Pairs();
        int net = r.U16();
        r.Skip(6);
        string symbol = r.Str();
        r.Skip(8);
        r.Skip(4);                               // width, height
        var pos = ((int)r.I16(), (int)r.I16());
        r.U8();
        int rot = r.U8();
        r.Skip(2);
        r.DisplayProps();
        r.U8();
        uint wire = r.U32();
        r.U8();
        return new DsnGlobal(symbol, net, pos, rot & 3, (rot & 4) != 0, wire);
    }
}
