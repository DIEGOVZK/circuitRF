// ================================================================
//  OpenEmsHdf5.DiscMaterial.cs — brief-sar-11 R-sar11-2: a CSXCAD DiscMaterial voxel file read back, so a model prepared for
//  openEMS opens in circuitRF directly. The layout is §6.1's last column (DiscMaterialWriter's header): /@Version ≥ 2,
//  /DiscData uint8 (nz, ny, nx) x fastest with DB_Size, epsR, kappa, density and Name, /mesh/x|y|z the voxel boundaries.
//
//  What CSXCAD reads, this reads, and what it does not need, this does not demand: Name is optional (an entry without one is
//  called "entry k"), and density is optional because CSXCAD skips an absent table. /DiscData may be chunked and deflated,
//  as openEMS's own bundled phantoms are; PureHDF decompresses it. The mesh lines are taken in METRES as stored — a DiscFile's
//  Scale converts a setup's drawing unit to them, never the other way.
// ================================================================

using System;
using System.Linq;
using PureHDF;

namespace RfCore.Hdf5;

/// <summary>A DiscMaterial file: its voxel boundaries (metres), one entry per voxel in C order over (x, y, z), and the entries'
/// table, entry 0 first (the background).</summary>
public sealed record DiscMaterialFile(double[] X, double[] Y, double[] Z, byte[] Indices, DiscTissue[] Tissues)
{
    public int Nx => X.Length - 1;
    public int Ny => Y.Length - 1;
    public int Nz => Z.Length - 1;
}

public static partial class OpenEmsHdf5
{
    /// <summary>A CSXCAD DiscMaterial file (version 2 or later), as <see cref="DiscMaterialWriter"/> writes it and as openEMS's
    /// own phantoms are given.</summary>
    public static DiscMaterialFile ReadDiscMaterial(string path)
    {
        using var r = new Reader(path);
        double version = r.Number(r.Root, "/", "Version", "the DiscMaterial format version");
        if (version < DiscMaterialWriter.Version)
            throw r.Unexpected("/@Version", $"version {version}", $"version {DiscMaterialWriter.Version} or later (CSXCAD reads nothing older)");
        var lines = new[] { "x", "y", "z" }.Select(a =>
        {
            string p = "/mesh/" + a;
            var ds = r.Dataset(p, "the voxel boundaries along " + a);
            if (ds.Type.Class != H5DataTypeClass.FloatingPoint)
                throw r.Unexpected(p, Reader.Describe(ds.Type), "float32 or float64");
            if (ds.Space.Dimensions.Length != 1 || ds.Space.Dimensions[0] < 2)
                throw r.Unexpected(p, $"of shape ({string.Join(", ", ds.Space.Dimensions)})", "at least two lines");
            return ds.Type.Size == 8 ? ds.Read<double[]>() : Array.ConvertAll(ds.Read<float[]>(), v => (double)v);
        }).ToArray();
        int nx = lines[0].Length - 1, ny = lines[1].Length - 1, nz = lines[2].Length - 1;

        var data = r.Dataset("/DiscData", "the voxels' entry indices");
        if (data.Type.Class != H5DataTypeClass.FixedPoint || data.Type.Size != 1)
            throw r.Unexpected("/DiscData", Reader.Describe(data.Type), "uint8 (CSXCAD reads one byte per voxel)");
        r.RequireShape(data, "/DiscData", [(ulong)nz, (ulong)ny, (ulong)nx]);
        int size = (int)r.Number(data, "/DiscData", "DB_Size", "the number of entries");
        double[] Table(string name, bool required)
        {
            if (!required && !data.AttributeExists(name)) return new double[size];
            var v = r.Numbers(data, "/DiscData", name, $"one value per entry ({name})");
            return v.Length == size ? v : throw r.Unexpected($"/DiscData/@{name}", $"{v.Length} values", $"{size} (DB_Size)");
        }
        double[] eps = Table("epsR", true), kappa = Table("kappa", true), density = Table("density", false);
        string[] names = data.AttributeExists("Name") ? r.Text(data, "/DiscData", "Name", "the entries' names").Split(',') : [];
        var tissues = Enumerable.Range(0, size).Select(k => new DiscTissue(
            k < names.Length && names[k].Trim().Trim('\0').Length > 0 ? names[k].Trim().Trim('\0') : $"entry {k}",
            (float)eps[k], (float)kappa[k], (float)density[k])).ToArray();

        byte[] indices = OpenEmsArrayOrder.FromCsxcadOrder(data.Read<byte[]>(), nx, ny, nz);
        int top = indices.Length == 0 ? 0 : indices.Max();
        if (top >= size)
            throw r.Unexpected("/DiscData", $"index {top}", $"indices below DB_Size ({size})");
        return new DiscMaterialFile(lines[0], lines[1], lines[2], indices, tissues);
    }
}
