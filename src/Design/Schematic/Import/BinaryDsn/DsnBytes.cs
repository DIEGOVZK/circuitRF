// A bounds-checked cursor over one stream of a binary .DSN design — brief-schimp-4 R-si4-3/4.
//
// Every read is checked against the stream's length; running past it, or meeting a byte the layout says cannot be there,
// throws DsnFormatException naming the stream and the offset, and the reader turns that into a refusal. Nothing here
// knows what a record means — only how the format spells numbers, strings and the lists every record shares.

using System.Buffers.Binary;

namespace CircuitRF.Design.Schematic.Import.BinaryDsn;

/// <summary>A stream that does not have the layout its version promises; the message names where.</summary>
internal sealed class DsnFormatException(string message) : Exception(message);

/// <summary>A display property: which property (a string-table index) is shown, where its text's top-left is relative to
/// its owner's position (page units, not turned with the owner), and <paramref name="Rotation"/>, the byte after it.</summary>
internal readonly record struct DsnDisplayProp(int NameIndex, int X, int Y, int Rotation = 0);

internal sealed class DsnBytes(byte[] bytes, string stream, EdifText text)
{
    public byte[] Bytes { get; } = bytes;
    public string Stream { get; } = stream;
    public int Pos { get; set; }
    public int Length => Bytes.Length;
    public bool AtEnd => Pos >= Bytes.Length;

    public DsnFormatException Fail(string what, int? at = null) =>
        new($"the {Stream} stream does not read as expected at byte {at ?? Pos}: {what}");

    private void Need(int n)
    {
        if (n < 0 || Pos + n > Bytes.Length) throw Fail($"it ends {Math.Max(0, Pos + n - Bytes.Length)} byte(s) early");
    }

    public byte U8() { Need(1); return Bytes[Pos++]; }
    public ushort U16() { Need(2); var v = BinaryPrimitives.ReadUInt16LittleEndian(Bytes.AsSpan(Pos)); Pos += 2; return v; }
    public short I16() { Need(2); var v = BinaryPrimitives.ReadInt16LittleEndian(Bytes.AsSpan(Pos)); Pos += 2; return v; }
    public uint U32() { Need(4); var v = BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(Pos)); Pos += 4; return v; }
    public int I32() { Need(4); var v = BinaryPrimitives.ReadInt32LittleEndian(Bytes.AsSpan(Pos)); Pos += 4; return v; }
    public void Skip(int n) { Need(n); Pos += n; }

    public byte Peek(int ahead = 0) { Need(ahead + 1); return Bytes[Pos + ahead]; }

    /// <summary>One byte that must be <paramref name="value"/>.</summary>
    public void Expect(byte value, string what)
    {
        int at = Pos;
        if (U8() != value) throw Fail($"expected {what} (0x{value:X2}), found 0x{Bytes[at]:X2}", at);
    }

    /// <summary>A u16 that must be zero — a list whose items this reader does not read.</summary>
    public void ExpectEmpty(string what)
    {
        int at = Pos;
        int n = U16();
        if (n != 0) throw Fail($"{what} holds {n} item(s), and this reader has not seen one to know its layout", at);
    }

    /// <summary>A string: a u16 length, that many bytes, and a NUL — decoded as SI-3 decodes text (R-si4-4).</summary>
    public string Str()
    {
        int at = Pos;
        int n = U16();
        Need(n + 1);
        if (Bytes[Pos + n] != 0) throw Fail("a string is not NUL-terminated", at);
        var raw = Bytes.AsSpan(Pos, n);
        string s = raw.IndexOfAnyExceptInRange((byte)0x20, (byte)0x7E) < 0 && raw.IndexOf((byte)0) < 0
            ? System.Text.Encoding.ASCII.GetString(raw)
            : text.DecodeBytes(raw.ToArray(), out _);
        Pos += n + 1;
        return s;
    }

    /// <summary>A record's (name, value) string-index pairs: a u16 count, then u16 pairs.</summary>
    public List<(int Name, int Value)> Pairs()
    {
        int n = U16();
        if (n > 4096) throw Fail($"a record claims {n} properties");
        var pairs = new List<(int, int)>(n);
        for (int i = 0; i < n; i++) pairs.Add((U16(), U16()));
        return pairs;
    }

    // ── version 3: sized records ───────────────────────────────────────────────────────────────

    /// <summary>The end marker of a version-3 record header.</summary>
    private static ReadOnlySpan<byte> Magic => [0xFF, 0xE4, 0x5C, 0x39];

    /// <summary>
    /// A version-3 record's header at the cursor: one or more SIZED prefixes (type, u32 size, u32 0 — the first size
    /// covers the whole record), then optionally a FINAL prefix (type, u16 n, n × (u32 name, u32 value) property pairs,
    /// or n = FFFF and none) ending in FF E4 5C 39. Leaves the cursor at the body; returns where the record ends and its
    /// pairs. <paramref name="type"/> null accepts any type, read from the stream.
    /// </summary>
    public (int End, byte Type, List<(int Name, int Value)> Pairs) Record(byte? type = null)
    {
        int start = Pos;
        byte t = U8();
        if (type is { } want && t != want) throw Fail($"expected a record of type 0x{want:X2}, found 0x{t:X2}", start);
        uint size = U32();
        if (U32() != 0 || start + 9 + (long)size > Length) throw Fail($"a record of type 0x{t:X2} with no valid size", start);
        int end = (int)(start + 9 + size);
        var pairs = new List<(int, int)>();
        while (Pos < end && Bytes[Pos] == t)
        {
            if (FinalPrefixAt(Pos, end) is { } n && !SizedChainEndsSooner(Pos, end, Pos + 3 + 8L * n))
            {
                Pos += 3;
                for (int i = 0; i < n; i++) pairs.Add(((int)U32(), (int)U32()));
                Pos += 4;
                break;
            }
            if (Pos + 9 <= end && BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(Pos + 5)) == 0
                && Pos + 9 + (long)BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(Pos + 1)) <= end)
            {
                Pos += 9;
                continue;
            }
            break;
        }
        return (end, t, pairs);
    }

    /// <summary>A sized prefix at <paramref name="at"/> (type, u32 size, u32 0, inside the record), or false.</summary>
    private bool SizedPrefixAt(int at, int end) =>
        at + 9 <= end && BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(at + 5)) == 0
        && at + 9 + (long)BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(at + 1)) <= end;

    /// <summary>
    /// Whether the bytes at <paramref name="at"/> also read as a SIZED prefix whose chain reaches a final prefix ending
    /// before <paramref name="finalMagicAt"/> — in which case that nearer one is the header's real end.
    ///
    /// <para>A sized prefix <c>0D 2B 00 00 00 00 00 00 00</c> also reads as a final prefix claiming 0x2B pairs, and on one
    /// field design a pin record's own end marker sat exactly 43 × 8 bytes on: the header swallowed the part's body and
    /// the page read as running 39,889 bytes past its end. The real header ends at the NEAREST marker, so the reading
    /// whose final prefix closes first is the one taken.</para>
    /// </summary>
    private bool SizedChainEndsSooner(int at, int end, long finalMagicAt)
    {
        if (!SizedPrefixAt(at, end)) return false;
        byte t = Bytes[at];
        for (int p = at + 9; p < end && Bytes[p] == t; p += 9)
        {
            if (FinalPrefixAt(p, end) is { } n) return p + 3 + 8L * n < finalMagicAt;
            if (!SizedPrefixAt(p, end)) return false;
        }
        return false;
    }

    /// <summary>The pair count of a final prefix at <paramref name="at"/>, or null when there is none.</summary>
    private int? FinalPrefixAt(int at, int end)
    {
        if (at + 7 > end) return null;
        int n = Bytes[at + 1] | Bytes[at + 2] << 8;
        if (n == 0xFFFF) return Bytes.AsSpan(at + 3, 4).SequenceEqual(Magic) ? 0 : null;
        long magicAt = at + 3 + 8L * n;
        return magicAt + 4 <= end && Bytes.AsSpan((int)magicAt, 4).SequenceEqual(Magic) ? n : null;
    }

    /// <summary>The start of every version-3 record body: a u32 length and that many bytes, read by nothing here (on a
    /// part, a 37-byte block naming how its reference is displayed; zero almost everywhere else).</summary>
    public void SkipBlob()
    {
        int at = Pos;
        uint n = U32();
        if (Pos + (long)n > Length) throw Fail($"a record body opens with a {n}-byte block that runs past the stream", at);
        Pos += (int)n;
    }

    /// <summary>A version-3 list: a u16 count of whole records, each skipped by its size.</summary>
    public int SkipRecords()
    {
        int n = U16();
        for (int i = 0; i < n; i++) Pos = Record().End;
        return n;
    }

    /// <summary>A version-3 display-property list: a u16 count of records, each the counted block, then version 2's
    /// layout after its 27 FF FF — a u32 name index, x y (i16), 6 bytes.</summary>
    public List<DsnDisplayProp> DisplayRecords()
    {
        int n = U16();
        if (n > 4096) throw Fail($"a record claims {n} displayed properties");
        var props = new List<DsnDisplayProp>(n);
        for (int i = 0; i < n; i++)
        {
            var (end, _, _) = Record();
            SkipBlob();
            int name = (int)U32();
            int x = I16(), y = I16();
            int rotation = U8();
            props.Add(new DsnDisplayProp(name, x, y, rotation));
            Pos = end;
        }
        return props;
    }

    /// <summary>A display-property list: a u16 count, then 15-byte records starting 0x27 FF FF.</summary>
    public List<DsnDisplayProp> DisplayProps()
    {
        int n = U16();
        if (n > 4096) throw Fail($"a record claims {n} displayed properties");
        var props = new List<DsnDisplayProp>(n);
        for (int i = 0; i < n; i++)
        {
            int at = Pos;
            if (U8() != 0x27 || U16() != 0xFFFF) throw Fail("a displayed property does not start 27 FF FF", at);
            int name = U16();
            int x = I16(), y = I16();
            int rotation = U8();
            Skip(5);
            props.Add(new DsnDisplayProp(name, x, y, rotation));
        }
        return props;
    }
}
