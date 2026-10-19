// brief-schimp-4-binary-dsn-reader.md R-si4-3/5/6 — the binary .DSN reader, against containers built by the tests:
// the refusals, and the symbol cache's graphics.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CircuitRF.Design.Schematic.Import;
using CircuitRF.Design.Schematic.Import.BinaryDsn;
using Xunit;

namespace CircuitRF.Ui.Tests.ImportSchematic;

public sealed class BinaryDsnReaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("crf-schimp4-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Write(string name, IReadOnlyDictionary<string, byte[]> streams)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, CfbTestWriter.Build(streams));
        return path;
    }

    /// <summary>A Library stream stating version <paramref name="major"/>.<paramref name="minor"/>.</summary>
    private static byte[] Library(int major, int minor)
    {
        var b = new byte[64];
        Encoding.ASCII.GetBytes("Windows Design".PadRight(31)).CopyTo(b, 0);
        b[32] = (byte)major;
        b[34] = (byte)minor;
        return b;
    }

    /// <summary>R-si4-3: a version not read is refused by number, with the EDIF route — and classified as the binary
    /// design it is, never as something else.</summary>
    [Fact]
    public void AnUnreadVersion_IsRefusedByNumber_WithTheEdifRoute()
    {
        string path = Write("future.dsn", new Dictionary<string, byte[]>
        {
            ["Library"] = Library(9, 1),
            ["Cache"] = new byte[8],
            ["Views/SCHEMATIC1/Pages/PAGE1"] = new byte[8],
        });
        var read = SchematicImport.Read(path);
        Assert.Equal(SchematicFileKind.BinaryDsn, read.Kind);
        Assert.False(read.Ok);
        Assert.Contains("version (9.1) is not readable", read.Refusal);
        Assert.Contains("EDIF 2.0.0", read.Refusal);
    }

    /// <summary>A compound file that is not a design — a spreadsheet, a symbol library — is said to be one, not misread.</summary>
    [Fact]
    public void ACompoundFileThatIsNotADesign_IsRefusedAsSuch()
    {
        string path = Write("book.dsn", new Dictionary<string, byte[]> { ["Workbook"] = new byte[600] });
        var read = BinaryDsnReader.Read(path);
        Assert.False(read.Ok);
        Assert.Contains("not a schematic design", read.Refusal);
    }

    /// <summary>A version-3 cache holding one symbol named <paramref name="name"/> with <paramref name="graphics"/>, each a
    /// (type, payload) as the cache stores them: the type twice, the payload's size, a zero, the payload.</summary>
    private static byte[] V3Cache(string name, params (byte Type, byte[] Payload)[] graphics)
    {
        var b = new List<byte>();
        void U16(int v) => b.AddRange(BitConverter.GetBytes((ushort)v));
        void U32(int v) => b.AddRange(BitConverter.GetBytes(v));
        void Str(string v) { U16(v.Length); b.AddRange(Encoding.ASCII.GetBytes(v)); b.Add(0); }
        b.AddRange([0xFF, 0xE4, 0x5C, 0x39]);
        U32(0);                                  // the body's counted block, empty
        Str(name);
        Str("parts.olb");
        U32(0);                                  // colour
        U16(graphics.Length);
        foreach (var (type, payload) in graphics)
        {
            b.Add(type);
            b.Add(type);
            U32(payload.Length);
            U32(0);
            b.AddRange(payload);
        }
        U32(0);
        U16(30);
        U16(20);
        U16(0);                                  // no pins
        return [.. b];
    }

    private static byte[] Ints(params int[] values) => [.. values.SelectMany(BitConverter.GetBytes)];

    private static byte[] Points(params (short A, short B)[] stored) =>
        [.. BitConverter.GetBytes((ushort)stored.Length), .. stored.SelectMany(p => BitConverter.GetBytes(p.A).Concat(BitConverter.GetBytes(p.B)))];

    /// <summary>A polyline's and a polygon's points are stored Y first, read as (x, y) a diode's triangle points down out of its
    /// body; a rectangle and a polygon carry their fill (0 solid, 1 none) after style and width.</summary>
    [Fact]
    public void TheCache_ReadsPointsYFirst_AndRectanglesAndPolygonsWithTheirFill()
    {
        var cache = new DsnCache(V3Cache("DIODE.Normal",
            (0x28, Ints(0, 0, 30, 20, 0, 3, 1, 0)),
            (0x2C, [.. Ints(0, 0, 0, 0), .. Points((10, 20), (17, 10), (3, 10), (10, 20))]),
            (0x2D, [.. Ints(0, 0), .. Points((1, 18), (8, 19))])), new EdifText(1252), 3);

        var symbol = cache.Find("diode.normal");

        Assert.NotNull(symbol);
        Assert.Equal(0, symbol!.SkippedGraphics);
        Assert.Equal(new DsnRectangle(0, 0, 30, 20, Filled: false), symbol.Graphics[0]);
        var triangle = Assert.IsType<DsnPolygon>(symbol.Graphics[1]);
        Assert.True(triangle.Filled);
        Assert.Equal([(20, 10), (10, 17), (10, 3), (20, 10)], triangle.Points);
        Assert.Equal([(18, 1), (19, 8)], Assert.IsType<DsnPolyline>(symbol.Graphics[2]).Points);
    }
}
