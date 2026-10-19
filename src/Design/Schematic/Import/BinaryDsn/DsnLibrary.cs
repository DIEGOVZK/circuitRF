// The design's Library stream: the format version, and the string table every page and cache record indexes —
// brief-schimp-4 R-si4-2/3. Layout (src/Design/RESOLVED.md, the compound-file section):
//
//   a 32-byte space-padded ASCII title, then u16 major, u16 minor, u32 created, u32 modified (Unix time), then the
//   fonts, the part-field names and the page defaults, then the STRING TABLE — a u32 count, (version 2) one byte, and
//   that many length-prefixed NUL-terminated strings — then a short tail naming the schematics.
//
// Records refer to strings by index, 0 being "none": 1-BASED into a version-2 table (a title block's displayed fields
// resolve to Title, Doc, RevCode, … only under that reading), and into a version-3 table counting its leading empty
// string as 0 (a part's value index then names its value, "100nF/10V").
//
// The table's start is FOUND rather than walked to, because what precedes it (fonts, field names, page settings) varies
// in length and none of it is needed here: the position whose count is followed by exactly that many well-formed
// strings, the longest such run, is the table.

namespace CircuitRF.Design.Schematic.Import.BinaryDsn;

internal sealed class DsnLibrary
{
    /// <summary>Where the title ends and the version begins.</summary>
    private const int VersionAt = 32;

    private DsnLibrary(int major, int minor, IReadOnlyList<string> strings, int first)
    {
        Major = major;
        Minor = minor;
        Strings = strings;
        _first = first;
    }

    /// <summary>The index of <see cref="Strings"/>[0]: 1 in version 2, where the table starts with its first string; 0 in
    /// version 3, whose table opens with an empty string that index 0 names.</summary>
    private readonly int _first;

    public int Major { get; }
    public int Minor { get; }
    public string Version => $"{Major}.{Minor}";
    public IReadOnlyList<string> Strings { get; }

    /// <summary>The string a record's index names; null for 0 or an index past the table.</summary>
    public string? String(int index) => index >= Math.Max(1, _first) && index - _first < Strings.Count ? Strings[index - _first] : null;

    public static DsnLibrary Read(byte[] bytes, EdifText text)
    {
        if (bytes.Length < VersionAt + 12) throw new DsnFormatException("the Library stream is too short to state a version");
        var r = new DsnBytes(bytes, "Library", text) { Pos = VersionAt };
        int major = r.U16(), minor = r.U16();
        var (strings, skip) = FindStrings(bytes, text);
        // Version 2 puts one byte between the count and the strings; version 3 none, and opens with an empty string.
        return new DsnLibrary(major, minor, strings, skip == 5 ? 1 : 0);
    }

    /// <summary>The version alone — read before deciding whether the rest is readable.</summary>
    public static (int Major, int Minor)? VersionOf(byte[] bytes)
    {
        if (bytes.Length < VersionAt + 4) return null;
        return (bytes[VersionAt] | bytes[VersionAt + 1] << 8, bytes[VersionAt + 2] | bytes[VersionAt + 3] << 8);
    }

    private static (List<string> Strings, int Skip) FindStrings(byte[] b, EdifText text)
    {
        int bestAt = -1, bestCount = -1, bestSkip = 0;
        for (int p = VersionAt + 12; p + 5 <= b.Length; p++)
        {
            int n = b[p] | b[p + 1] << 8 | b[p + 2] << 16 | b[p + 3] << 24;
            if (n < 1 || n > 1_000_000 || n <= bestCount) continue;
            foreach (int skip in (ReadOnlySpan<int>)[4, 5])
                if (Walk(b, p + skip, n) >= 0) { bestAt = p; bestCount = n; bestSkip = skip; break; }
        }
        if (bestAt < 0) throw new DsnFormatException("the Library stream holds no string table");
        var r = new DsnBytes(b, "Library", text) { Pos = bestAt + bestSkip };
        var strings = new List<string>(bestCount);
        for (int i = 0; i < bestCount; i++) strings.Add(r.Str());
        return (strings, bestSkip);
    }

    /// <summary>Where <paramref name="n"/> well-formed strings starting at <paramref name="p"/> end, or −1.</summary>
    private static int Walk(byte[] b, int p, int n)
    {
        for (int i = 0; i < n; i++)
        {
            if (p + 2 > b.Length) return -1;
            int len = b[p] | b[p + 1] << 8;
            if (p + 2 + len >= b.Length || b[p + 2 + len] != 0) return -1;
            if (b.AsSpan(p + 2, len).IndexOf((byte)0) >= 0) return -1;
            p += 3 + len;
        }
        return p;
    }
}
