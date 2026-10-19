// One page stream of a version-3.2 design — brief-schimp-4 R-si4-3. Layout in src/Design/RESOLVED.md.
//
// The SAME sections in the same order as version 2 (DsnPageV2), every item now a sized record (DsnBytes.Record) whose
// body opens with a counted block (DsnBytes.SkipBlob) — so a section this reader does not read is SKIPPED by its sizes and
// reported, never misread:
//
//   page record  0x0A; body: u32 0, NAME, SIZE ("A3"), two timestamps, the same 150-byte block (width and height now in
//                µm, grid 2540), then the title block(s) as 0x41 records
//   nets         u16 count of 0x34 records
//   (list)       u16 count of records, skipped by size
//   net names    u16 count of (name, u32 net id), plain as in version 2; every net is named, auto-named ones "N…"
//   wires        u16 count of 0x14 records: u32 id, u32 NET, u32 colour, x1 y1 x2 y2 (i32), a byte, u16 count of ALIAS
//                records (u32 0, x y (i32), u32 colour, u32 rotation, u32, NAME), display properties, 8 bytes
//   parts        u16 count of 0x0D records (see Part)
//   (list)       u16 count of records — empty
//   globals      u16 count of 0x25 records, each followed by u32 WIRE and a byte
//   off-page     u16 count of 0x26 records, each followed by u32 WIRE and a byte — the same layout as a global
//   (lists)      u16 counts of records: two empty, then the page's text and graphics, then two empty
//
// Coordinates are 1/100 inch, Y down, as in version 2.

namespace CircuitRF.Design.Schematic.Import.BinaryDsn;

internal static class DsnPageV3
{
    private const int FixedHeader = 150;

    public static DsnPage Read(DsnBytes r)
    {
        var (pageEnd, _, _) = r.Record(0x0A);
        r.SkipBlob();
        string name = r.Str();
        r.Str();                                 // the page size's name
        r.Skip(8);
        int fixedAt = r.Pos;
        r.Skip(16);
        int width = r.I32(), height = r.I32(), grid = r.I32();
        r.Pos = fixedAt + FixedHeader;
        while (r.Peek() == 0x41 && IsRecord(r)) r.Pos = r.Record().End;

        var skipped = new List<string>();
        r.SkipRecords();                         // nets: the wires name theirs
        Unread(r, "list after the nets", skipped);
        int na = r.U16();
        var netNames = new List<(string, uint)>(na);
        for (int i = 0; i < na; i++) netNames.Add((r.Str(), r.U32()));

        int nw = r.U16();
        var wires = new List<DsnWire>(nw);
        var aliases = new List<DsnAlias>();
        for (int i = 0; i < nw; i++) wires.Add(Wire(r, aliases));
        int np = r.U16();
        var parts = new List<DsnPart>(np);
        for (int i = 0; i < np; i++) parts.Add(Part(r));
        Unread(r, "list after the parts", skipped);
        var globals = Globals(r, 0x25);
        var offPages = Globals(r, 0x26);
        Unread(r, "first list after the off-page connectors", skipped);
        Unread(r, "second list after the off-page connectors", skipped);
        int graphics = r.SkipRecords();
        if (graphics > 0) skipped.Add($"{graphics} text or graphic item(s) on page {name} were not imported.");
        while (r.Pos + 2 <= pageEnd) Unread(r, "list at the end of the page", skipped);
        return new DsnPage(name, width, height, grid, netNames, wires, parts, globals)
        {
            OffPages = offPages,
            Aliases = aliases,
            Skipped = skipped,
        };
    }

    private static bool IsRecord(DsnBytes r)
    {
        int at = r.Pos;
        if (at + 9 > r.Length) return false;
        uint size = BitConverter.ToUInt32(r.Bytes, at + 1), zero = BitConverter.ToUInt32(r.Bytes, at + 5);
        return zero == 0 && at + 9 + (long)size <= r.Length;
    }

    /// <summary>A list this reader does not read: skipped by its sizes, and said.</summary>
    private static void Unread(DsnBytes r, string what, List<string> skipped)
    {
        int at = r.Pos;
        int n = r.SkipRecords();
        if (n > 0) skipped.Add($"{n} item(s) in the page's {what} (at byte {at}) were not read; this reader has not seen one.");
    }

    private static DsnWire Wire(DsnBytes r, List<DsnAlias> aliases)
    {
        var (end, _, _) = r.Record(0x14);
        r.SkipBlob();
        uint id = r.U32(), net = r.U32();
        r.U32();                                 // colour
        var a = (r.I32(), r.I32());
        var b = (r.I32(), r.I32());
        r.U8();
        int n = r.U16();
        for (int i = 0; i < n; i++)
        {
            var (aliasEnd, _, _) = r.Record();
            r.SkipBlob();
            var at = (r.I32(), r.I32());
            r.Skip(12);                          // colour, rotation, font
            aliases.Add(new DsnAlias(r.Str(), at, id));
            r.Pos = aliasEnd;
        }
        r.Pos = end;
        return new DsnWire(id, net, a, b);
    }

    /// <summary>
    /// 0x0D with the instance's (name, value) property pairs in its header; body: the counted block (37 bytes on most
    /// parts, holding "1 9 Reference 0 0  0  "; empty on some), two u32s, the symbol NAME, u32 instance, 8 bytes, x y (i16), colour, ROTATION (as version 2), 2 bytes,
    /// display-property records, a byte, the REFERENCE, u32 VALUE (string index), 8 bytes, u16, u16 pin count, the pin
    /// records (the counted block, u16 index, x y (i16), u32 WIRE, …), the source part's name, u16 UNIT (0-based).
    /// </summary>
    private static DsnPart Part(DsnBytes r)
    {
        var (end, _, pairs) = r.Record(0x0D);
        r.SkipBlob();
        r.Skip(8);
        string symbol = r.Str();
        r.U32();
        r.Skip(8);
        var pos = ((int)r.I16(), (int)r.I16());
        r.U8();
        int rot = r.U8();
        r.Skip(2);
        r.SkipRecords();                         // displayed properties
        r.U8();
        string reference = r.Str();
        int value = (int)r.U32();
        r.Skip(8);
        r.U16();
        int np = r.U16();
        if (np > 10_000) throw r.Fail($"a part claims {np} pins");
        var pins = new List<DsnPlacedPin>(np);
        for (int i = 0; i < np; i++)
        {
            var (pinEnd, _, _) = r.Record(0x10);
            r.SkipBlob();
            int index = r.U16();
            var at = ((int)r.I16(), (int)r.I16());
            uint wire = r.U32();
            pins.Add(new DsnPlacedPin(index, at, wire));
            r.Pos = pinEnd;
        }
        r.Str();                                 // the source part's name
        int unit = r.U16();
        r.Pos = end;
        return new DsnPart(reference, symbol, value, pairs, pos, rot & 3, (rot & 4) != 0, pins, unit + 1);
    }

    /// <summary>A u16 count of records of <paramref name="type"/> (0x25 globals, 0x26 off-page connectors): body u32 0,
    /// u32 NET NAME (string index), u32 library, the SYMBOL's name, u32 id, 8 bytes, x y (i16), colour, rotation; each
    /// record followed by u32 WIRE and a byte.</summary>
    private static List<DsnGlobal> Globals(DsnBytes r, byte type)
    {
        int n = r.U16();
        var list = new List<DsnGlobal>(n);
        for (int i = 0; i < n; i++)
        {
            var (end, _, _) = r.Record(type);
            r.SkipBlob();
            int net = (int)r.U32();
            r.U32();
            string symbol = r.Str();
            r.U32();
            r.Skip(8);
            var pos = ((int)r.I16(), (int)r.I16());
            r.U8();
            int rot = r.U8();
            r.Pos = end;
            uint wire = r.U32();
            r.U8();
            list.Add(new DsnGlobal(symbol, net, pos, rot & 3, (rot & 4) != 0, wire));
        }
        return list;
    }
}
