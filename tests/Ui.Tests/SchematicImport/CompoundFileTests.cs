// brief-schimp-4-binary-dsn-reader.md R-si4-1 — the read-only compound-file reader, against containers BUILT HERE by a
// writer of a few dozen lines: a nested storage, a stream big enough for the
// regular FAT and two small enough for the mini stream, in both sector sizes; then the same file truncated, and with a
// FAT chain and a directory tree made to loop — each a refusal, never an exception and never a hang.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CircuitRF.Design.Schematic.Import.Cfb;
using Xunit;

namespace CircuitRF.Ui.Tests.ImportSchematic;

public sealed class CompoundFileTests
{
    private const uint End = 0xFFFFFFFE, Free = 0xFFFFFFFF, FatSect = 0xFFFFFFFD, None = 0xFFFFFFFF;

    private static byte[] Data(int length, int seed) => [.. Enumerable.Range(0, length).Select(i => (byte)(i * 31 + seed))];

    private static readonly byte[] Big = Data(5000, 1), Small = Data(100, 2), Deep = Data(70, 3);

    /// <summary>
    /// A compound file of version <paramref name="major"/>: Root{ Big, Storage1{ Small, Inner{ Deep } } }. Big is in the
    /// regular FAT, Small and Deep in the mini stream. Sector 0 is the FAT, then the directory, the mini FAT, the mini
    /// stream and Big. Returns the bytes and where things landed, for the tests that break them.
    /// </summary>
    private static (byte[] Bytes, int FatOffset, uint BigStart, int DirOffset) Build(int major)
    {
        int shift = major == 3 ? 9 : 12, size = 1 << shift;
        var sectors = new List<byte[]>();
        var fat = new List<uint>();
        uint Alloc(byte[] data, int sectorBytes)
        {
            if (data.Length == 0) return End;
            uint first = (uint)sectors.Count;
            for (int at = 0; at < data.Length; at += sectorBytes)
            {
                var s = new byte[size];
                Array.Copy(data, at, s, 0, Math.Min(sectorBytes, data.Length - at));
                sectors.Add(s);
                fat.Add((uint)sectors.Count);
            }
            fat[^1] = End;
            return first;
        }

        sectors.Add(new byte[size]); fat.Add(FatSect);                       // sector 0: the FAT itself

        // The mini stream: Small then Deep, each padded to 64-byte mini sectors; the mini FAT chains them.
        var mini = new List<byte>();
        var miniFat = new List<uint>();
        uint MiniAlloc(byte[] data)
        {
            uint first = (uint)miniFat.Count;
            for (int at = 0; at < data.Length; at += 64)
            {
                mini.AddRange(data.Skip(at).Take(64));
                while (mini.Count % 64 != 0) mini.Add(0);
                miniFat.Add((uint)miniFat.Count + 1);
            }
            miniFat[^1] = End;
            return first;
        }
        uint smallStart = MiniAlloc(Small), deepStart = MiniAlloc(Deep);

        var entries = new List<byte[]>
        {
            Entry("Root Entry", 5, None, None, 1, 0, 0),           // start/size of the mini stream patched below
            Entry("Big", 2, None, 2, None, 0, (ulong)Big.Length),  // its start patched below
            Entry("Storage1", 1, None, None, 3, 0, 0),
            Entry("Small", 2, None, 4, None, smallStart, (ulong)Small.Length),
            Entry("Inner", 1, None, None, 5, 0, 0),
            Entry("Deep", 2, None, None, None, deepStart, (ulong)Deep.Length),
        };
        while (entries.Count * 128 % size != 0) entries.Add(new byte[128]);
        uint dirStart = (uint)sectors.Count;
        int dirOffset = (int)(dirStart + 1) * size;
        Alloc([.. entries.SelectMany(e => e)], size);                       // placeholder; rewritten once starts are known
        var miniFatBytes = miniFat.SelectMany(BitConverter.GetBytes).ToArray();
        uint miniFatStart = Alloc(miniFatBytes, size);
        uint miniStart = Alloc([.. mini], size);
        uint bigStart = Alloc(Big, size);

        BinaryPrimitives.WriteUInt32LittleEndian(entries[0].AsSpan(116), miniStart);
        BinaryPrimitives.WriteUInt64LittleEndian(entries[0].AsSpan(120), (ulong)mini.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(entries[1].AsSpan(116), bigStart);
        var dir = entries.SelectMany(e => e).ToArray();
        for (int i = 0; i * size < dir.Length; i++) Array.Copy(dir, i * size, sectors[(int)dirStart + i], 0, size);

        Assert.True(fat.Count <= size / 4);                                  // one FAT sector is enough
        for (int i = 0; i < size / 4; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(sectors[0].AsSpan(i * 4), i < fat.Count ? fat[i] : Free);

        var header = new byte[size];
        CompoundFile.Signature.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(24), 0x3E);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(26), (ushort)major);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(28), 0xFFFE);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(30), (ushort)shift);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(32), 6);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40), major == 4 ? (uint)(dir.Length / size) : 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(44), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(48), dirStart);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(56), 4096);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(60), miniFatStart);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(64), (uint)((miniFatBytes.Length + size - 1) / size));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(68), End);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(76), 0);       // DIFAT[0]: the FAT is sector 0
        for (int i = 1; i < 109; i++) BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(76 + i * 4), Free);

        return ([.. header.Concat(sectors.SelectMany(s => s))], size, bigStart, dirOffset);

        static byte[] Entry(string name, byte type, uint left, uint right, uint child, uint start, ulong length)
        {
            var e = new byte[128];
            var n = Encoding.Unicode.GetBytes(name + "\0");
            n.CopyTo(e, 0);
            BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(64), (ushort)n.Length);
            e[66] = type;
            e[67] = 1;
            BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(68), left);
            BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(72), right);
            BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(76), child);
            BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(116), start);
            BinaryPrimitives.WriteUInt64LittleEndian(e.AsSpan(120), length);
            return e;
        }
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void ASelfBuiltContainer_ListsEveryPath_AndReadsEachStreamExactly(int major)
    {
        var read = CompoundFile.Open(Build(major).Bytes);
        Assert.True(read.Ok, read.Refusal);
        var file = read.File!;
        Assert.Equal(major, file.MajorVersion);
        Assert.Equal(["", "Big", "Storage1", "Storage1/Small", "Storage1/Inner", "Storage1/Inner/Deep"], file.Entries.Select(e => e.Path));
        Assert.Equal(Big, file.ReadStream("Big").Bytes);
        Assert.Equal(Small, file.ReadStream("Storage1/Small").Bytes);   // the mini stream
        Assert.Equal(Deep, file.ReadStream("storage1/inner/DEEP").Bytes);  // the format compares names without case
        Assert.False(file.ReadStream("Storage1/Missing").Ok);
    }

    [Fact]
    public void ATruncatedFile_IsRefused_WhereverItIsCut()
    {
        var bytes = Build(3).Bytes;
        foreach (int keep in new[] { 0, 7, 300, 512, 1024, bytes.Length - 600 })
        {
            var read = CompoundFile.Open(bytes[..keep]);
            if (!read.Ok) { Assert.NotNull(read.Refusal); continue; }
            // Opened (its tables survive the cut): the stream past the cut is the refusal.
            var big = read.File!.ReadStream("Big");
            Assert.False(big.Ok);
            Assert.Contains("truncated", big.Refusal);
        }
    }

    [Fact]
    public void ALoopingFatChain_AndALoopingDirectoryTree_AreRefused()
    {
        // Big's second sector points back at its first.
        var (bytes, fatOffset, bigStart, dirOffset) = Build(3);
        var looped = (byte[])bytes.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(looped.AsSpan(fatOffset + (int)(bigStart + 1) * 4), bigStart);
        var read = CompoundFile.Open(looped);
        Assert.True(read.Ok, read.Refusal);
        var big = read.File!.ReadStream("Big");
        Assert.False(big.Ok);
        Assert.Contains("loops", big.Refusal);

        // Storage1's right sibling is Big, which is already its left neighbour's — the tree returns to an entry.
        var tree = (byte[])bytes.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(tree.AsSpan(dirOffset + 2 * 128 + 72), 1);
        var refused = CompoundFile.Open(tree);
        Assert.False(refused.Ok);
        Assert.Contains("loops", refused.Refusal);
    }
}
