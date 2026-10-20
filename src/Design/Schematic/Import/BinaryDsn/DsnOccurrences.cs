// The references a version-3.2 design DISPLAYS — read from a schematic's Hierarchy stream. Layout in src/Design/RESOLVED.md.
//
// A placed part's page record carries a reference, but it is not always the one the design shows. The editor keeps its
// annotation per OCCURRENCE in Views/<schematic>/Hierarchy/Hierarchy, keyed by the part's instance id (the u32 after the
// symbol name in the page record, DsnPart.InstanceId). On one field design 302 of 308 parts showed a different reference
// there than on their page — parts copied between pages keep their old page reference, and only the occurrence is
// re-annotated — so three pages' worth of parts came out as duplicates of each other and were wired as one part's units.
//
// Each part's occurrence record ends its header with the version-3 end marker and a counted block (empty, or the 37-byte
// display block a page part opens with), then u32 (a record id), u32 INSTANCE id, a nested record's 9-byte sized prefix, 3 bytes, and the REFERENCE string — 16
// bytes after the instance id. An empty string is "no annotation of its own": the page's reference stands.
//
// Read by an anchored scan rather than a full walk of the stream, whose other records this reader does not need: each
// anchor (end marker, a counted block of plausible size) is checked, the string must be well formed, and an instance id met more than once
// (a hierarchical design placing one schematic twice has an occurrence per placement) is not used at all.

using System.Buffers.Binary;

namespace CircuitRF.Design.Schematic.Import.BinaryDsn;

internal static class DsnOccurrences
{
    private static ReadOnlySpan<byte> Anchor => [0xFF, 0xE4, 0x5C, 0x39];

    /// <summary>The longest counted block taken as one — a part's display block is 37 bytes.</summary>
    private const int MaxBlock = 256;

    /// <summary>Bytes from the instance id to its reference string.</summary>
    private const int ReferenceAfterId = 16;

    /// <summary>Instance id → the reference the design displays, for each id that has one and appears once.</summary>
    public static Dictionary<uint, string> Read(byte[] bytes, EdifText text)
    {
        var found = new Dictionary<uint, string?>();
        var span = bytes.AsSpan();
        for (int at = span.IndexOf(Anchor); at >= 0;)
        {
            int blockAt = at + Anchor.Length;
            int block = blockAt + 4 <= bytes.Length ? (int)BinaryPrimitives.ReadUInt32LittleEndian(span[blockAt..]) : -1;
            int idAt = blockAt + 4 + block + 4;
            if (block is >= 0 and <= MaxBlock && idAt + 4 <= bytes.Length)
            {
                uint id = BinaryPrimitives.ReadUInt32LittleEndian(span[idAt..]);
                string? reference = StringAt(bytes, idAt + ReferenceAfterId, text);
                if (id != 0 && reference is not null)
                    found[id] = found.ContainsKey(id) ? null : reference;   // seen twice: not used
            }
            int next = span[(at + 1)..].IndexOf(Anchor);
            at = next < 0 ? -1 : at + 1 + next;
        }
        return found.Where(kv => kv.Value is { Length: > 0 }).ToDictionary(kv => kv.Key, kv => kv.Value!);
    }

    /// <summary>A well-formed reference string at <paramref name="at"/> — u16 length, printable bytes, NUL — or null.</summary>
    private static string? StringAt(byte[] b, int at, EdifText text)
    {
        if (at + 3 > b.Length) return null;
        int n = b[at] | b[at + 1] << 8;
        if (n > 64 || at + 2 + n >= b.Length || b[at + 2 + n] != 0) return null;
        for (int i = 0; i < n; i++)
            if (b[at + 2 + i] < 0x20) return null;
        return n == 0 ? "" : text.DecodeBytes(b.AsSpan(at + 2, n).ToArray(), out _);
    }
}
