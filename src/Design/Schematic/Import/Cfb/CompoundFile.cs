// A read-only Compound File Binary (CFBF, "OLE2") reader — brief-schimp-4 R-si4-1, overview D7.
//
// Written from the published MS-CFB specification, and from nothing else: the container the binary .DSN design lives
// in. A NuGet CFBF library was considered and not chosen (overview D7: no new dependency). Read-only — there is no
// write support and none is planned.
//
// ── THE FORMAT, AS FAR AS THIS FILE NEEDS IT ──────────────────────────────────────────────────
//
// A 512-byte header, then sectors of 2^SectorShift bytes: 512 in version 3, 4096 in version 4 (whose header sector is
// padded to 4096). Sector n starts at (n + 1) << SectorShift.
//   FAT       — one 32-bit "next sector" per sector; chains end at ENDOFCHAIN. Its own sectors are listed by the DIFAT:
//               109 entries in the header, then a chain of DIFAT sectors whose last slot points at the next one.
//   Directory — a FAT chain of 128-byte entries: a UTF-16 name, a type (storage, stream, root), left/right sibling and
//               child ids — each storage's children form a red-black tree, read here simply as a binary tree — a start
//               sector and a size.
//   Mini      — a stream smaller than the cutoff (4096) lives in the MINI stream (the root entry's own FAT chain), in
//               64-byte mini sectors chained by the mini FAT.
//
// ── HOSTILE INPUT ─────────────────────────────────────────────────────────────────────────────
//
// Every file read here was written by another program, and a damaged one must be a refusal, never an exception and
// never a loop: every chain is bounded by the number of sectors that could exist, a sector visited twice is a cycle,
// every offset is checked against the file's length, and a directory entry reached twice is a cycle in the tree. The
// only thing that leaves this file is a CompoundFileRead / CompoundStreamRead carrying a refusal sentence.

using System.Buffers.Binary;
using System.Text;

namespace CircuitRF.Design.Schematic.Import.Cfb;

/// <summary>What a directory entry is.</summary>
public enum CompoundEntryKind { Storage, Stream, Root }

/// <summary>One storage or stream, with its <paramref name="Path"/> from the root — names joined by <c>/</c>, the root
/// itself <c>""</c>.</summary>
public sealed record CompoundFileEntry(string Path, string Name, CompoundEntryKind Kind, long Size, int Id);

/// <summary>A container opened, or why it could not be.</summary>
public sealed record CompoundFileRead(CompoundFile? File, string? Refusal)
{
    public bool Ok => File is not null;
}

/// <summary>A stream's bytes, or why they could not be read.</summary>
public sealed record CompoundStreamRead(byte[]? Bytes, string? Refusal)
{
    public bool Ok => Bytes is not null;
}

public sealed class CompoundFile
{
    /// <summary>The first eight bytes of every compound file.</summary>
    public static ReadOnlySpan<byte> Signature => [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    /// <summary>The largest file opened — far beyond any schematic design, small enough to hold in memory.</summary>
    public const long MaxFileBytes = 1L << 30;

    private const uint MaxRegSect = 0xFFFFFFFA, EndOfChain = 0xFFFFFFFE, FreeSect = 0xFFFFFFFF, NoStream = 0xFFFFFFFF;
    private const int HeaderDifatSlots = 109, EntryBytes = 128;

    private readonly byte[] _bytes;
    private readonly int _sectorSize, _miniSectorSize;
    private readonly uint _miniCutoff;
    private readonly uint[] _fat;
    private readonly uint[] _miniFat;
    private readonly RawEntry[] _raw;
    private byte[]? _miniStream;

    private CompoundFile(byte[] bytes, int majorVersion, int sectorSize, int miniSectorSize, uint miniCutoff, uint[] fat,
                         uint[] miniFat, RawEntry[] raw, IReadOnlyList<CompoundFileEntry> entries)
    {
        _bytes = bytes;
        MajorVersion = majorVersion;
        _sectorSize = sectorSize;
        _miniSectorSize = miniSectorSize;
        _miniCutoff = miniCutoff;
        _fat = fat;
        _miniFat = miniFat;
        _raw = raw;
        Entries = entries;
    }

    /// <summary>3 (512-byte sectors) or 4 (4096-byte sectors).</summary>
    public int MajorVersion { get; }

    /// <summary>Every storage and stream reachable from the root, the root first, then depth-first in tree order.</summary>
    public IReadOnlyList<CompoundFileEntry> Entries { get; }

    /// <summary>The streams only.</summary>
    public IEnumerable<CompoundFileEntry> Streams => Entries.Where(e => e.Kind == CompoundEntryKind.Stream);

    public static bool HasSignature(ReadOnlySpan<byte> head) => head.Length >= 8 && head[..8].SequenceEqual(Signature);

    public static CompoundFileRead Open(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return new CompoundFileRead(null, $"No such file: {path}");
            if (info.Length > MaxFileBytes)
                return new CompoundFileRead(null, $"{info.Name} is {info.Length:N0} bytes, larger than a compound file read here can be.");
            return Open(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new CompoundFileRead(null, $"The file could not be read: {ex.Message}");
        }
    }

    public static CompoundFileRead Open(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        try { return new CompoundFileRead(Parse(bytes), null); }
        catch (Exception ex) when (ex is CfbException or ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            return new CompoundFileRead(null, ex is CfbException ? ex.Message : $"The compound file is damaged: {ex.Message}");
        }
    }

    /// <summary>The stream at <paramref name="path"/> (names joined by <c>/</c>, case-insensitive as the format's own
    /// name comparison is), or why it cannot be read.</summary>
    public CompoundStreamRead ReadStream(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var entry = Entries.FirstOrDefault(e => e.Kind == CompoundEntryKind.Stream && e.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return new CompoundStreamRead(null, $"The compound file holds no stream '{path}'.");
        return ReadStream(entry);
    }

    public CompoundStreamRead ReadStream(CompoundFileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        try
        {
            var raw = _raw[entry.Id];
            if (raw.Size > int.MaxValue) throw new CfbException($"The stream '{entry.Path}' is larger than can be read.");
            int size = (int)raw.Size;
            if (size == 0) return new CompoundStreamRead([], null);
            return new CompoundStreamRead(raw.Size < _miniCutoff ? ReadMini(raw.Start, size, entry.Path) : ReadChain(raw.Start, size, entry.Path), null);
        }
        catch (Exception ex) when (ex is CfbException or ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            return new CompoundStreamRead(null, ex is CfbException ? ex.Message : $"The stream '{entry.Path}' is damaged: {ex.Message}");
        }
    }

    // ── the header and the tables ────────────────────────────────────────────────────────────────

    private static CompoundFile Parse(byte[] b)
    {
        if (b.Length < 512 || !HasSignature(b)) throw new CfbException("It is not a compound file (no compound-file signature).");
        int major = U16(b, 26);
        if (U16(b, 28) != 0xFFFE) throw new CfbException("The compound file's byte-order mark is wrong.");
        int shift = U16(b, 30), miniShift = U16(b, 32);
        if (!(major == 3 && shift == 9) && !(major == 4 && shift == 12))
            throw new CfbException($"The compound file is version {major} with {(shift < 31 ? 1L << shift : 0)}-byte sectors; only version 3 (512) and 4 (4096) are read.");
        if (miniShift != 6) throw new CfbException("The compound file's mini sector is not 64 bytes.");
        int sectorSize = 1 << shift;
        if (b.Length < sectorSize) throw new CfbException("The compound file is truncated: it ends inside its header.");

        // How many sectors COULD exist bounds every chain and every table.
        long sectorCount = (b.Length - sectorSize + sectorSize - 1) / sectorSize;
        int perSector = sectorSize / 4;

        // The FAT's own sectors: the header's DIFAT, then the DIFAT chain.
        uint fatSectors = U32(b, 44);
        if (fatSectors > sectorCount) throw new CfbException("The compound file is truncated: its header lists more FAT sectors than it holds.");
        var fatLocations = new List<uint>((int)fatSectors);
        for (int i = 0; i < HeaderDifatSlots && fatLocations.Count < fatSectors; i++)
        {
            uint s = U32(b, 76 + i * 4);
            if (s == FreeSect) break;
            fatLocations.Add(s);
        }
        uint difat = U32(b, 68);
        var seenDifat = new HashSet<uint>();
        while (fatLocations.Count < fatSectors && difat != EndOfChain && difat != FreeSect)
        {
            if (!seenDifat.Add(difat) || seenDifat.Count > sectorCount) throw new CfbException("The compound file's DIFAT chain loops.");
            int at = SectorOffset(b, difat, shift, sectorSize, "DIFAT");
            for (int i = 0; i < perSector - 1 && fatLocations.Count < fatSectors; i++)
            {
                uint s = U32(b, at + i * 4);
                if (s == FreeSect) break;
                fatLocations.Add(s);
            }
            difat = U32(b, at + (perSector - 1) * 4);
        }
        if (fatLocations.Count < fatSectors) throw new CfbException("The compound file's DIFAT lists fewer FAT sectors than its header says.");

        var fat = new uint[fatLocations.Count * perSector];
        for (int k = 0; k < fatLocations.Count; k++)
        {
            int at = SectorOffset(b, fatLocations[k], shift, sectorSize, "FAT");
            for (int i = 0; i < perSector; i++) fat[k * perSector + i] = U32(b, at + i * 4);
        }

        var reader = new ChainReader(b, fat, shift, sectorSize);
        byte[] dir = reader.ReadAll(U32(b, 48), "the directory");
        int entryCount = dir.Length / EntryBytes;
        if (entryCount == 0) throw new CfbException("The compound file has no directory.");
        var raw = new RawEntry[entryCount];
        for (int i = 0; i < entryCount; i++) raw[i] = RawEntry.Read(dir, i * EntryBytes, major);
        if (raw[0].Type != 5) throw new CfbException("The compound file's first directory entry is not its root.");

        uint miniFatStart = U32(b, 60);
        uint[] miniFat = [];
        if (U32(b, 64) > 0 && miniFatStart != EndOfChain)
        {
            byte[] mf = reader.ReadAll(miniFatStart, "the mini FAT");
            miniFat = new uint[mf.Length / 4];
            for (int i = 0; i < miniFat.Length; i++) miniFat[i] = BinaryPrimitives.ReadUInt32LittleEndian(mf.AsSpan(i * 4));
        }

        var entries = Walk(raw);
        return new CompoundFile(b, major, sectorSize, 64, U32(b, 56), fat, miniFat, raw, entries);
    }

    /// <summary>The tree flattened: each storage's children in tree order, a path per entry. A child or sibling id out of
    /// range, or an entry reached twice, is a damaged directory.</summary>
    private static List<CompoundFileEntry> Walk(RawEntry[] raw)
    {
        var entries = new List<CompoundFileEntry> { new("", raw[0].Name, CompoundEntryKind.Root, raw[0].Size, 0) };
        var seen = new HashSet<int> { 0 };
        var storages = new Stack<(int Id, string Path)>();
        storages.Push((0, ""));
        while (storages.Count > 0)
        {
            var (storage, parent) = storages.Pop();
            var children = new List<int>();
            // In-order over the sibling tree, iteratively — a deep or hostile tree costs no stack.
            var pending = new Stack<int>();
            uint node = raw[storage].Child;
            while (node != NoStream || pending.Count > 0)
            {
                while (node != NoStream)
                {
                    int id = Id(node, raw.Length);
                    if (!seen.Add(id)) throw new CfbException("The compound file's directory tree loops.");
                    pending.Push(id);
                    node = raw[id].Left;
                }
                int next = pending.Pop();
                children.Add(next);
                node = raw[next].Right;
            }
            var added = new List<(int Id, string Path)>();
            foreach (int id in children)
            {
                var r = raw[id];
                string path = parent.Length == 0 ? r.Name : parent + "/" + r.Name;
                switch (r.Type)
                {
                    case 1: entries.Add(new(path, r.Name, CompoundEntryKind.Storage, 0, id)); added.Add((id, path)); break;
                    case 2: entries.Add(new(path, r.Name, CompoundEntryKind.Stream, r.Size, id)); break;
                    default: throw new CfbException($"The compound file's directory entry {id} is neither a storage nor a stream.");
                }
            }
            // Pushed in reverse so storages are walked in tree order.
            for (int i = added.Count - 1; i >= 0; i--) storages.Push(added[i]);
        }
        return entries;

        static int Id(uint id, int count) =>
            id < count ? (int)id : throw new CfbException($"The compound file's directory names an entry ({id}) it does not hold.");
    }

    // ── streams ─────────────────────────────────────────────────────────────────────────────────

    private byte[] ReadChain(uint start, int size, string what) =>
        new ChainReader(_bytes, _fat, MajorVersion == 3 ? 9 : 12, _sectorSize).Read(start, size, $"the stream '{what}'");

    private byte[] ReadMini(uint start, int size, string what)
    {
        _miniStream ??= _raw[0].Size == 0 ? [] : ReadChain(_raw[0].Start, checked((int)_raw[0].Size), "the mini stream");
        var result = new byte[size];
        int done = 0;
        uint sector = start;
        var seen = new HashSet<uint>();
        while (done < size)
        {
            if (sector >= _miniFat.Length) throw new CfbException($"The stream '{what}' is truncated: its mini chain ends early.");
            if (!seen.Add(sector)) throw new CfbException($"The stream '{what}' loops: its mini chain returns to a sector it has read.");
            long at = (long)sector * _miniSectorSize;
            int take = Math.Min(_miniSectorSize, size - done);
            if (at + take > _miniStream.Length) throw new CfbException($"The stream '{what}' is truncated: it runs past the mini stream.");
            Array.Copy(_miniStream, at, result, done, take);
            done += take;
            sector = _miniFat[sector];
        }
        return result;
    }

    /// <summary>Follows FAT chains, bounded by the table's length, refusing a cycle or a sector past the file's end.</summary>
    private sealed class ChainReader(byte[] bytes, uint[] fat, int shift, int sectorSize)
    {
        /// <summary>Exactly <paramref name="size"/> bytes from the chain at <paramref name="start"/>.</summary>
        public byte[] Read(uint start, int size, string what)
        {
            var result = new byte[size];
            int done = 0;
            uint sector = start;
            var seen = new HashSet<uint>();
            while (done < size)
            {
                Check(sector, seen, what);
                int take = Math.Min(sectorSize, size - done);
                long at = ((long)sector + 1) << shift;
                if (at + take > bytes.Length) throw new CfbException($"The compound file is truncated: {what} runs past its end.");
                Array.Copy(bytes, at, result, done, take);
                done += take;
                sector = fat[sector];
            }
            return result;
        }

        /// <summary>The whole chain at <paramref name="start"/>, to its end — the directory and the mini FAT, whose size
        /// is their chain.</summary>
        public byte[] ReadAll(uint start, string what)
        {
            var sectors = new List<uint>();
            var seen = new HashSet<uint>();
            for (uint sector = start; sector != EndOfChain; sector = fat[sector])
            {
                Check(sector, seen, what);
                sectors.Add(sector);
            }
            var result = new byte[sectors.Count * sectorSize];
            for (int i = 0; i < sectors.Count; i++)
            {
                long at = ((long)sectors[i] + 1) << shift;
                if (at + sectorSize > bytes.Length) throw new CfbException($"The compound file is truncated: {what} runs past its end.");
                Array.Copy(bytes, at, result, i * sectorSize, sectorSize);
            }
            return result;
        }

        private void Check(uint sector, HashSet<uint> seen, string what)
        {
            if (sector == EndOfChain) throw new CfbException($"The compound file is truncated: {what} ends before its size.");
            if (sector > MaxRegSect || sector >= fat.Length)
                throw new CfbException($"The compound file is damaged: {what} names a sector ({sector:X}) it does not hold.");
            if (!seen.Add(sector)) throw new CfbException($"The compound file is damaged: {what} loops back to a sector it has read.");
        }
    }

    // ── bytes ───────────────────────────────────────────────────────────────────────────────────

    private static int SectorOffset(byte[] b, uint sector, int shift, int sectorSize, string what)
    {
        if (sector > MaxRegSect) throw new CfbException($"The compound file's {what} names no sector.");
        long at = ((long)sector + 1) << shift;
        if (at + sectorSize > b.Length) throw new CfbException($"The compound file is truncated: its {what} sector {sector} is past its end.");
        return (int)at;
    }

    private static int U16(byte[] b, int at) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at));
    private static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at));

    private readonly record struct RawEntry(string Name, byte Type, uint Left, uint Right, uint Child, uint Start, long Size)
    {
        public static RawEntry Read(byte[] d, int at, int major)
        {
            int nameBytes = Math.Clamp(U16(d, at + 64), 0, 64);
            string name = Encoding.Unicode.GetString(d, at, Math.Max(0, nameBytes - 2) & ~1);
            // Version 3 states only the low 32 bits of a size; the high half may hold anything.
            ulong size = BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(at + 120));
            if (major == 3) size &= 0xFFFFFFFF;
            return new RawEntry(name, d[at + 66], U32(d, at + 68), U32(d, at + 72), U32(d, at + 76), U32(d, at + 116),
                                (long)Math.Min(size, long.MaxValue));
        }
    }

    private sealed class CfbException(string message) : Exception(message);
}
