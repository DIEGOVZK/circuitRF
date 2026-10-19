// A version-3 compound file holding the streams a test names, for tests that need a container and not the container's
// own corner cases (CompoundFileTests has those). Every stream goes in regular sectors: the header's mini-stream cutoff
// is 0, which the format allows and the reader honours.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CircuitRF.Design.Schematic.Import.Cfb;

namespace CircuitRF.Ui.Tests.ImportSchematic;

internal static class CfbTestWriter
{
    private const uint End = 0xFFFFFFFE, Free = 0xFFFFFFFF, FatSect = 0xFFFFFFFD, None = 0xFFFFFFFF;
    private const int Size = 512;

    /// <summary>A container with each stream at its <c>/</c>-joined path; storages are made as the paths need them.</summary>
    public static byte[] Build(IReadOnlyDictionary<string, byte[]> streams)
    {
        // The tree: index 0 the root, then storages and streams in path order.
        var names = new List<(string Name, byte Type, int Parent, byte[]? Data)> { ("Root Entry", 5, -1, null) };
        var index = new Dictionary<string, int>(StringComparer.Ordinal) { [""] = 0 };
        foreach (var (path, data) in streams.OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            var parts = path.Split('/');
            for (int i = 0; i < parts.Length; i++)
            {
                string at = string.Join('/', parts[..(i + 1)]), parent = string.Join('/', parts[..i]);
                if (index.ContainsKey(at)) continue;
                index[at] = names.Count;
                names.Add((parts[i], (byte)(i == parts.Length - 1 ? 2 : 1), index[parent], i == parts.Length - 1 ? data : null));
            }
        }

        var sectors = new List<byte[]> { new byte[Size] };
        var fat = new List<uint> { FatSect };
        uint Alloc(byte[] data)
        {
            if (data.Length == 0) return End;
            uint first = (uint)sectors.Count;
            for (int at = 0; at < data.Length; at += Size)
            {
                var s = new byte[Size];
                Array.Copy(data, at, s, 0, Math.Min(Size, data.Length - at));
                sectors.Add(s);
                fat.Add((uint)sectors.Count);
            }
            fat[^1] = End;
            return first;
        }
        var starts = names.Select(n => n.Data is { } d ? Alloc(d) : 0u).ToList();

        var dir = new byte[((names.Count + 3) / 4) * 4 * 128];
        for (int i = 0; i < names.Count; i++)
        {
            var e = dir.AsSpan(i * 128, 128);
            var n = Encoding.Unicode.GetBytes(names[i].Name + "\0");
            n.CopyTo(e);
            BinaryPrimitives.WriteUInt16LittleEndian(e[64..], (ushort)n.Length);
            e[66] = names[i].Type;
            var siblings = Enumerable.Range(0, names.Count).Where(k => names[k].Parent == names[i].Parent && k > 0).ToList();
            int next = siblings.IndexOf(i) is int p && p >= 0 && p + 1 < siblings.Count ? siblings[p + 1] : -1;
            int child = Enumerable.Range(0, names.Count).FirstOrDefault(k => names[k].Parent == i, -1);
            BinaryPrimitives.WriteUInt32LittleEndian(e[68..], None);
            BinaryPrimitives.WriteUInt32LittleEndian(e[72..], next < 0 ? None : (uint)next);
            BinaryPrimitives.WriteUInt32LittleEndian(e[76..], child < 0 ? None : (uint)child);
            BinaryPrimitives.WriteUInt32LittleEndian(e[116..], starts[i]);
            BinaryPrimitives.WriteUInt64LittleEndian(e[120..], (ulong)(names[i].Data?.Length ?? 0));
        }
        uint dirStart = Alloc(dir);

        if (fat.Count > Size / 4) throw new InvalidOperationException("the test container needs one FAT sector");
        for (int i = 0; i < Size / 4; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(sectors[0].AsSpan(i * 4), i < fat.Count ? fat[i] : Free);

        var header = new byte[Size];
        CompoundFile.Signature.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(24), 0x3E);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(26), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(28), 0xFFFE);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(30), 9);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(32), 6);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(44), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(48), dirStart);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(56), 0);       // every stream in regular sectors
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(60), End);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(68), End);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(76), 0);
        for (int i = 1; i < 109; i++) BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(76 + i * 4), Free);
        return [.. header.Concat(sectors.SelectMany(s => s))];
    }
}
