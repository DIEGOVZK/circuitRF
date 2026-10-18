// ================================================================
//  DiscMaterialWriter.cs — brief-sar-4 R-sar4-3: a voxel material file in the DiscMaterial version-2 layout CSXCAD
//  reads (CSPropDiscMaterial::ReadHDF5), written with PureHDF's writer — the API MatWriter already uses.
//
//    /@Version        float64 2.0       (CSXCAD refuses anything below 2)
//    /DiscData        uint8, dimensions (nz, ny, nx): x fastest, as CSXCAD indexes it (OpenEmsArrayOrder)
//      @DB_Size       int32, the number of tissue entries, background included
//      @epsR, @kappa, @density   float32[DB_Size]
//      @Name          one variable-length string, the entries' names joined by commas
//    /mesh/x|y|z      float32, the voxel BOUNDARIES in metres (n + 1 lines for n voxels)
//
//  Entry 0 is background: CSXCAD leaves a voxel holding 0 to the DiscMaterial's own background property unless the
//  setup's DiscFile says UseDBBackground="1". The XML's DiscFile Scale converts the setup's drawing unit to these
//  metres (Scale="1000" for a mm setup).
//
//  UINT8 ONLY. CSXCAD reads /DiscData with H5T_NATIVE_UINT8 into a uint8 buffer, so at most 256 entries can be
//  addressed. A uint16 file would be converted on read and every index above 255 lost; that is a refusal here,
//  never a file CSXCAD silently misreads.
//
//  PLAIN LAYOUT. The data is written contiguous and unfiltered, the form openEMS's own dumps use. No size forces
//  deflate: a contiguous HDF5 dataset has no size limit, and a whole body at 1 mm is ~34 MB of indices.
//
//  The gate is CSXCAD, not a generic HDF5 tool: DiscMaterialWriterTests has the installed openEMS load a file this
//  wrote and reads back, from openEMS's own raw SAR dump, the conductivity and density it assigned each voxel.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using PureHDF;

namespace RfCore.Hdf5;

/// <summary>One DiscMaterial entry: its name, relative permittivity, conductivity (S/m) and density (kg/m³).</summary>
public sealed record DiscTissue(string Name, float EpsR, float Kappa, float Density);

public static class DiscMaterialWriter
{
    /// <summary>The DiscMaterial file version CSXCAD reads.</summary>
    public const double Version = 2.0;

    /// <summary>The most entries a uint8 index addresses — CSXCAD's limit.</summary>
    public const int MaxEntries = 256;

    /// <summary>
    /// Writes <paramref name="indices"/> — one tissue index per voxel, C order over (x, y, z) — on voxel boundaries
    /// <paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/> (metres; one more line than voxels per axis).
    /// <paramref name="tissues"/>[0] is the background entry.
    /// </summary>
    public static void Write(string path, double[] x, double[] y, double[] z, byte[] indices, IReadOnlyList<DiscTissue> tissues)
        => Build(x, y, z, indices, tissues).Write(path, new H5WriteOptions { PreferCompactDatasetLayout = false });

    /// <summary>brief-sar-8 — <see cref="Write"/>'s file as bytes, for a writer that hands its files to a run rather than to disk.</summary>
    public static byte[] ToBytes(double[] x, double[] y, double[] z, byte[] indices, IReadOnlyList<DiscTissue> tissues)
    {
        using var stream = new System.IO.MemoryStream();
        Build(x, y, z, indices, tissues).Write(stream, new H5WriteOptions { PreferCompactDatasetLayout = false });
        return stream.ToArray();
    }

    private static H5File Build(double[] x, double[] y, double[] z, byte[] indices, IReadOnlyList<DiscTissue> tissues)
    {
        int nx = x.Length - 1, ny = y.Length - 1, nz = z.Length - 1;
        foreach (var (lines, axis) in new[] { (x, "x"), (y, "y"), (z, "z") })
        {
            if (lines.Length < 2)
                throw Refused("/mesh/" + axis, "needs at least two lines (one voxel).");
            for (int i = 1; i < lines.Length; i++)
                if (!(lines[i] > lines[i - 1]))
                    throw Refused("/mesh/" + axis, $"must increase strictly; line {i} does not.");
        }
        if (indices.Length != nx * ny * nz)
            throw Refused("/DiscData", $"holds {indices.Length} indices for {nx} × {ny} × {nz} = {nx * ny * nz} voxels.");
        if (tissues.Count is 0 or > MaxEntries)
            throw Refused("/DiscData/@DB_Size", $"would be {tissues.Count}; CSXCAD reads a uint8 index, so 1 to {MaxEntries} entries.");
        if (tissues.FirstOrDefault(t => t.Name.Contains(',')) is { } comma)
            throw Refused("/DiscData/@Name", $"joins the names with commas, and '{comma.Name}' contains one.");
        int top = indices.Length == 0 ? 0 : indices.Max();
        if (top >= tissues.Count)
            throw Refused("/DiscData", $"holds index {top}, and there are {tissues.Count} entries.");

        var disc = new H5Dataset(OpenEmsArrayOrder.ToCsxcadOrder(indices, nx, ny, nz), null, null, null, null, default, null)
        {
            Attributes = new()
            {
                ["DB_Size"] = tissues.Count,
                ["epsR"] = tissues.Select(t => t.EpsR).ToArray(),
                ["kappa"] = tissues.Select(t => t.Kappa).ToArray(),
                ["density"] = tissues.Select(t => t.Density).ToArray(),
                ["Name"] = string.Join(",", tissues.Select(t => t.Name)),
            },
        };
        var file = new H5File
        {
            ["DiscData"] = disc,
            ["mesh"] = new H5Group
            {
                ["x"] = Array.ConvertAll(x, v => (float)v),
                ["y"] = Array.ConvertAll(y, v => (float)v),
                ["z"] = Array.ConvertAll(z, v => (float)v),
            },
        };
        file.Attributes = new() { ["Version"] = Version };
        return file;
    }

    private static OpenEmsHdf5Exception Refused(string objectPath, string why) =>
        new(OpenEmsHdf5Diagnostics.DiscMaterialRefused(objectPath, why), objectPath);
}
