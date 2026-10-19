using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using System.Text;
using RfCore.Data;
using RfCore.Export;
using Xunit;

namespace RfCore.Tests.Export;

/// <summary>
/// brief-modelcomposer-3 R-mc3-3: a MAT v5 file and a v7.3 (HDF5) file holding the same arrays read the same
/// arrays — C order over MATLAB's logical shape from both — and a cell array is refused by name.
/// The v5 file is written here byte by byte from the published layout (column-major, a compressed element and
/// a struct among them); the v7.3 file is <see cref="MatWriter"/>'s, through <see cref="DataSetExporter"/>.
/// </summary>
public sealed class MatReaderTests
{
    private static readonly double[] A = [.. Enumerable.Range(0, 12).Select(i => (double)i)];   // 3 × 4, C order
    private static readonly Complex[] Z = [new(1, -2), new(3.5, 4)];

    [Fact]
    public void AV5File_AndAV73File_ReadTheSameArrays()
    {
        string dir = Directory.CreateTempSubdirectory("crf-mat-").FullName;
        string v73 = Path.Combine(dir, "v73.mat");
        var ds = new DataSet();
        ds.Add("A", new DataCube([new Axis("r", [0, 1, 2]), new Axis("c", [0, 1, 2, 3])], A));
        ds.Add("Z", new DataCube([new Axis("i", [0, 1])], Z));
        DataSetExporter.Export(ds, v73, ExportFormat.Mat);

        string v5 = Path.Combine(dir, "v5.mat");
        File.WriteAllBytes(v5, V5File(
            Compressed(Matrix("A", 6, [3, 4], ColumnMajor(A, 3, 4), null)),
            Matrix("Z", 6, [2, 1], [.. Z.Select(z => z.Real)], [.. Z.Select(z => z.Imaginary)]),
            Struct("s", ("B", Matrix("", 6, [3, 4], ColumnMajor(A, 3, 4), null)))));

        var h = MatReader.Read(v73);
        var m = MatReader.Read(v5);

        Assert.Equal([3, 4], h["dataset/A"].Shape);
        Assert.Equal([3, 4], m["A"].Shape);
        Assert.Equal(A, h["dataset/A"].Real);
        Assert.Equal(A, m["A"].Real);
        Assert.Equal(A, m["s.B"].Real);
        Assert.Equal(Z, Enumerable.Range(0, 2).Select(h["dataset/Z"].At));
        Assert.Equal(Z, Enumerable.Range(0, 2).Select(m["Z"].At));
    }

    [Fact]
    public void ACellArray_IsRefusedByName_AndCostsNothingWhenNotAskedFor()
    {
        string path = Path.Combine(Directory.CreateTempSubdirectory("crf-mat-").FullName, "cell.mat");
        File.WriteAllBytes(path, V5File(
            Matrix("A", 6, [3, 4], ColumnMajor(A, 3, 4), null),
            Cell("traces", Matrix("", 6, [1, 1], [1.0], null))));

        var ex = Assert.Throws<MatFormatException>(() => MatReader.Read(path));
        Assert.Equal("traces", ex.Variable);
        Assert.Contains("'traces' is a cell array", ex.Message);

        Assert.Equal(A, MatReader.Read(path, ["A"])["A"].Real);
    }

    // ── A MAT v5 writer, from the published layout ─────────────────────────────

    private static double[] ColumnMajor(double[] c, int rows, int cols)
    {
        var f = new double[c.Length];
        for (int i = 0; i < rows; i++) for (int j = 0; j < cols; j++) f[i + j * rows] = c[i * cols + j];
        return f;
    }

    private static byte[] V5File(params byte[][] elements)
    {
        var head = new byte[128];
        Encoding.ASCII.GetBytes("MATLAB 5.0 MAT-file, written by MatReaderTests".PadRight(116)).CopyTo(head, 0);
        head[124] = 0x00; head[125] = 0x01;            // version 0x0100, little-endian
        head[126] = (byte)'I'; head[127] = (byte)'M';
        return [.. head, .. elements.SelectMany(e => e)];
    }

    private static byte[] Element(int type, byte[] data)
    {
        var tag = new byte[8];
        BitConverter.GetBytes(type).CopyTo(tag, 0);
        BitConverter.GetBytes(data.Length).CopyTo(tag, 4);
        int pad = (8 - data.Length % 8) % 8;
        return [.. tag, .. data, .. new byte[pad]];
    }

    private static byte[] Header(string name, int cls, bool complex, int[] dims) =>
    [
        .. Element(6, [.. BitConverter.GetBytes(cls | (complex ? 0x0800 : 0)), 0, 0, 0, 0]),
        .. Element(5, [.. dims.SelectMany(BitConverter.GetBytes)]),
        .. Element(1, Encoding.ASCII.GetBytes(name)),
    ];

    private static byte[] Matrix(string name, int cls, int[] dims, double[] re, double[]? im) => Element(14,
    [
        .. Header(name, cls, im is not null, dims),
        .. Element(9, [.. re.SelectMany(BitConverter.GetBytes)]),
        .. (im is null ? [] : Element(9, [.. im.SelectMany(BitConverter.GetBytes)])),
    ]);

    private static byte[] Cell(string name, byte[] content) => Element(14, [.. Header(name, 1, false, [1, 1]), .. content]);

    private static byte[] Struct(string name, params (string Field, byte[] Matrix)[] fields)
    {
        const int len = 32;
        var names = new byte[len * fields.Length];
        for (int k = 0; k < fields.Length; k++) Encoding.ASCII.GetBytes(fields[k].Field).CopyTo(names, k * len);
        return Element(14,
        [
            .. Header(name, 2, false, [1, 1]),
            .. Element(5, BitConverter.GetBytes(len)),
            .. Element(1, names),
            .. fields.SelectMany(f => f.Matrix),
        ]);
    }

    private static byte[] Compressed(byte[] element)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(element);
        byte[] data = ms.ToArray();
        var tag = new byte[8];
        BitConverter.GetBytes(15).CopyTo(tag, 0);
        BitConverter.GetBytes(data.Length).CopyTo(tag, 4);
        return [.. tag, .. data];
    }
}
