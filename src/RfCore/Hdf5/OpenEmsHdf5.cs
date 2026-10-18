// ================================================================
//  OpenEmsHdf5.cs — brief-sar-4 R-sar4-2: openEMS 0.37.0's HDF5 files, read through PureHDF (the managed, MIT HDF5
//  library src/RfCore already carries for the .mat export; overview O1). No HDF5 code of circuitRF's own.
//
//  Three files, each against the layout pinned in docs/design/sar.md §6:
//
//    ReadSarRaw    a raw SAR dump (DumpType 29): per cell, the complex E phasor, conductivity, density and volume,
//                  at each FD_Samples frequency — what SAR-5's engine averages. openEMS writes it on the cell-centre
//                  ("dual") mesh, with each cell's widths beside it.
//    ReadField     a frequency-domain field dump (DumpType 10 E, 11 H; FileType 1): a vector per mesh node.
//    ReadSarResult what sar_calc writes: one SAR value per cell, and the mass, power and peak it reports.
//
//  ONE DATASET AT A TIME. Every array is read on its own, through a PureHDF hyperslab selection, so a 1 GB dump is
//  never read whole: a vector field one component at a time, and ReadSarRaw can be asked for a slab of x so a
//  caller streams a large dump through in pieces. Mesh lines and attributes are small and read whole.
//
//  WHAT IS REFUSED. A file whose groups, datasets or attributes differ from the pinned layout — a missing group, a
//  dataset of another type or shape, a legacy d_order, a cylindrical mesh, another openEMS_HDF5_version — throws
//  OpenEmsHdf5Exception naming the object path and what was expected. Never a read on a guess.
//
//  ARRAY ORDER: OpenEmsArrayOrder, the one place. 0.37.0 writes C order over (x, y, z), circuitRF's own, so the
//  arrays here are exactly what NumPy hands back for openEMS's own reader, element for element.
//
//  Mesh lines are in metres as stored: openEMS writes SI coordinates and states the simulation's drawing unit
//  beside them as mesh_scaling (1e-3 for a mm setup), which is carried, never applied.
//
//  E and H are kept as the file's complex64 (ComplexF), not widened: a whole-body dump at 2 mm is ~34 M cells, and
//  System.Numerics.Complex would double what is already the largest array in a SAR run. A consumer widens a value
//  when it computes with it.
// ================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using PureHDF;
using PureHDF.Selections;

namespace RfCore.Hdf5;

/// <summary>One complex64 value as openEMS stores it (a compound of float32 <c>r</c> and <c>i</c>).</summary>
public struct ComplexF(float re, float im)
{
    public float Re = re;
    public float Im = im;

    public readonly System.Numerics.Complex ToComplex() => new(Re, Im);

    /// <summary>|z|², in double precision.</summary>
    public readonly double MagnitudeSquared => (double)Re * Re + (double)Im * Im;
}

/// <summary>A dump's Cartesian mesh lines, in metres, and the drawing unit openEMS states beside them.</summary>
public sealed record OpenEmsMesh(double[] X, double[] Y, double[] Z, double DrawingUnitM)
{
    public int Nx => X.Length;
    public int Ny => Y.Length;
    public int Nz => Z.Length;
    public int Count => Nx * Ny * Nz;
}

public enum OpenEmsFieldQuantity { E, H }

/// <summary>
/// A raw SAR dump at one frequency: per cell, the peak complex E phasor, σ (S/m), ρ (kg/m³) and volume (m³), on the
/// dump's cell-centre mesh. Every array is C order over (x, y, z) (<see cref="OpenEmsArrayOrder.Index"/>). When it
/// is an x slab, <see cref="XStart"/> is its first x index in the file and the mesh holds only its own x lines.
/// </summary>
public sealed record OpenEmsSarRaw(
    OpenEmsMesh Mesh, double[] CellWidthX, double[] CellWidthY, double[] CellWidthZ,
    double FrequencyHz, int XStart,
    ComplexF[] Ex, ComplexF[] Ey, ComplexF[] Ez,
    float[] Conductivity, float[] Density, float[] Volume);

/// <summary>brief-sar-5 — a raw SAR dump's cell-centre mesh, cell widths and frequencies, without its cell data.</summary>
public sealed record OpenEmsSarRawLayout(
    OpenEmsMesh Mesh, double[] CellWidthX, double[] CellWidthY, double[] CellWidthZ, double[] FrequenciesHz);

/// <summary>A frequency-domain E or H dump at one of its frequencies: three components, C order over (x, y, z).</summary>
public sealed record OpenEmsField(
    OpenEmsFieldQuantity Quantity, OpenEmsMesh Mesh, bool DualMesh,
    IReadOnlyList<double> FrequenciesHz, int FrequencyIndex,
    ComplexF[] X, ComplexF[] Y, ComplexF[] Z)
{
    public double FrequencyHz => FrequenciesHz[FrequencyIndex];
}

/// <summary>
/// What sar_calc wrote, at one frequency: SAR per cell (W/kg, C order over (x, y, z)), the mass (kg) and power (W)
/// it summed, its maximum and the cell holding it, and its cube counts.
/// </summary>
public sealed record OpenEmsSarResult(
    OpenEmsMesh Mesh, double FrequencyHz, float[] Sar,
    double MassKg, double PowerW, double MaxSar, int[] MaxSarIndex,
    ulong ValidCubes, ulong UsedCubes, ulong UnusedCubes, ulong AirCubes);

/// <summary>openEMS 0.37.0's HDF5 output and sar_calc's, read through PureHDF. See the file header.</summary>
public static partial class OpenEmsHdf5
{
    /// <summary>The <c>openEMS_HDF5_version</c> 0.37.0 writes — the layout this reader is pinned to.</summary>
    public const double PinnedVersion = 0.3;

    /// <summary>The raw SAR dump's <c>dump_type</c> (openEMS's SAR_RAW_DATA).</summary>
    public const int SarRawDumpType = 29;

    /// <summary>
    /// A raw SAR dump (DumpType 29) at frequency index <paramref name="frequencyIndex"/>. With
    /// <paramref name="xCount"/> ≥ 0, only x indices [<paramref name="xStart"/>, xStart + xCount) are read.
    /// </summary>
    public static OpenEmsSarRaw ReadSarRaw(string path, int frequencyIndex = 0, int xStart = 0, int xCount = -1)
    {
        using var r = new Reader(path);
        var (mesh, widths) = ReadSarRawHeader(r);

        int nx = mesh.Nx, ny = mesh.Ny, nz = mesh.Nz;
        ArgumentOutOfRangeException.ThrowIfNegative(xStart);
        int count = xCount < 0 ? nx - xStart : xCount;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(xStart + count, nx);

        var cellData = new[] { "Conductivity", "Density", "Volume" }.Select(name =>
        {
            string p = "/CellData/" + name;
            var ds = r.Dataset(p, "the cells' " + name.ToLowerInvariant());
            r.RequireFloat(ds, p, 4);
            r.RequireShape(ds, p, [(ulong)nx, (ulong)ny, (ulong)nz]);
            r.RequireOrder(ds, p, OpenEmsArrayOrder.Scalar);
            return ds.Read<float[]>(fileSelection: new HyperslabSelection(3, [(ulong)xStart, 0, 0], [(ulong)count, (ulong)ny, (ulong)nz]));
        }).ToArray();

        var freqs = r.ReadFrequencies();
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(frequencyIndex, freqs.Length);
        var e = r.ReadVector(frequencyIndex, mesh, xStart, count);

        var slab = mesh with { X = mesh.X[xStart..(xStart + count)] };
        return new OpenEmsSarRaw(slab, widths[0][xStart..(xStart + count)], widths[1], widths[2], freqs[frequencyIndex], xStart,
                                 e[0], e[1], e[2], cellData[0], cellData[1], cellData[2]);
    }

    /// <summary>
    /// brief-sar-5 — a raw SAR dump's layout without its cell data: the cell-centre mesh, the cell widths and every
    /// frequency, so a caller can size what it builds before streaming the cells through <see cref="ReadSarRaw"/> in x slabs.
    /// </summary>
    public static OpenEmsSarRawLayout ReadSarRawLayout(string path)
    {
        using var r = new Reader(path);
        var (mesh, widths) = ReadSarRawHeader(r);
        return new OpenEmsSarRawLayout(mesh, widths[0], widths[1], widths[2], r.ReadFrequencies());
    }

    private static (OpenEmsMesh Mesh, double[][] Widths) ReadSarRawHeader(Reader r)
    {
        r.RequireRootHeader(out int dumpType);
        if (dumpType != SarRawDumpType)
            throw r.Unexpected("/@dump_type", $"dump type {dumpType}", $"dump type {SarRawDumpType} (a raw SAR dump)");
        var mesh = r.ReadMesh();
        var widths = new[] { "x", "y", "z" }.Select((a, n) =>
        {
            string p = "/CellWidth/" + a;
            var ds = r.Dataset(p, "the cell widths along " + a);
            r.RequireFloat(ds, p, 8);
            r.RequireShape(ds, p, [(ulong)mesh.LengthOf(n)]);
            return ds.Read<double[]>();
        }).ToArray();
        return (mesh, widths);
    }

    /// <summary>A frequency-domain E (DumpType 10) or H (11) field dump, at frequency index <paramref name="frequencyIndex"/>.</summary>
    public static OpenEmsField ReadField(string path, int frequencyIndex = 0)
    {
        using var r = new Reader(path);
        bool dual = r.RequireRootHeader(out int dumpType);
        // openEMS states the field in dump_type (0 E, 1 H); that it is frequency-domain is the /FieldData/FD group.
        var quantity = dumpType switch
        {
            0 => OpenEmsFieldQuantity.E,
            1 => OpenEmsFieldQuantity.H,
            _ => throw r.Unexpected("/@dump_type", $"dump type {dumpType}", "dump type 0 (an E field) or 1 (an H field)"),
        };
        var mesh = r.ReadMesh();
        var freqs = r.ReadFrequencies();
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(frequencyIndex, freqs.Length);
        var c = r.ReadVector(frequencyIndex, mesh, 0, mesh.Nx);
        return new OpenEmsField(quantity, mesh, dual, freqs, frequencyIndex, c[0], c[1], c[2]);
    }

    /// <summary>A SAR result file as sar_calc writes it, at frequency index <paramref name="frequencyIndex"/>.</summary>
    public static OpenEmsSarResult ReadSarResult(string path, int frequencyIndex = 0)
    {
        using var r = new Reader(path);
        r.RequireVersion();
        double mass = r.Number(r.Root, "/", "mass", "the averaged mass in kg");
        var mesh = r.ReadMesh();
        var freqs = r.ReadFrequencies();
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(frequencyIndex, freqs.Length);

        const string fd = "/FieldData/FD";
        var group = r.Group(fd, "the frequency-domain results");
        ulong Cubes(string name) => (ulong)r.Number(group, fd, name, "the count of " + name.Replace('_', ' '));

        string p = $"{fd}/f{frequencyIndex}";
        var ds = r.Dataset(p, "the SAR at that frequency");
        r.RequireFloat(ds, p, 4);
        r.RequireShape(ds, p, [(ulong)mesh.Nx, (ulong)mesh.Ny, (ulong)mesh.Nz]);
        r.RequireOrder(ds, p, OpenEmsArrayOrder.Scalar);
        var idx = r.Numbers(ds, p, "maxSAR_idx", "the cell index of the maximum");
        if (idx.Length != 3)
            throw r.Unexpected(p + "/@maxSAR_idx", $"{idx.Length} values", "3 values (ix, iy, iz)");

        return new OpenEmsSarResult(mesh, freqs[frequencyIndex], ds.Read<float[]>(), mass,
            r.Number(ds, p, "power", "the absorbed power in W"), r.Number(ds, p, "maxSAR", "the maximum SAR in W/kg"),
            idx.Select(v => (int)v).ToArray(),
            Cubes("valid_cubes"), Cubes("used_cubes"), Cubes("unused_cubes"), Cubes("air_cubes"));
    }

    // ── the pinned layout, checked object by object ─────────────────────────────────────────────────────────

    private sealed class Reader : IDisposable
    {
        private static readonly H5ReadOptions Options = new()
        {
            // openEMS's complex64 is a compound of float32 "r" and "i"; ComplexF's fields are Re and Im.
            FieldNameMapper = f => f.Name switch { nameof(ComplexF.Re) => "r", nameof(ComplexF.Im) => "i", _ => f.Name },
        };

        private readonly string _name;
        public readonly IH5Group Root;
        private readonly IDisposable _file;

        public Reader(string path)
        {
            _name = Path.GetFileName(path);
            var file = H5File.OpenRead(path, Options);
            Root = file;
            _file = file;
        }

        public void Dispose() => _file.Dispose();

        public OpenEmsHdf5Exception Missing(string path, string expected) =>
            new(OpenEmsHdf5Diagnostics.Missing(_name, path, expected), path);

        public OpenEmsHdf5Exception Unexpected(string path, string found, string expected) =>
            new(OpenEmsHdf5Diagnostics.Unexpected(_name, path, found, expected), path);

        /// <summary>The object at an absolute path, walked link by link so a refusal names the first one missing.</summary>
        private IH5Object Get(string path, string expected)
        {
            IH5Group at = Root;
            string walked = "";
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                walked += "/" + parts[i];
                if (!at.LinkExists(parts[i]))
                    throw Missing(walked, i == parts.Length - 1 ? expected : "a group");
                var next = at.Get(parts[i]);
                if (i == parts.Length - 1) return next;
                at = next as IH5Group ?? throw Unexpected(walked, "a dataset", "a group");
            }
            return at;
        }

        public IH5Group Group(string path, string expected) =>
            Get(path, expected) as IH5Group ?? throw Unexpected(path, "a dataset", "a group holding " + expected);

        public IH5Dataset Dataset(string path, string expected) =>
            Get(path, expected) as IH5Dataset ?? throw Unexpected(path, "a group", "a dataset holding " + expected);

        private IH5Attribute Attribute(IH5Object obj, string objPath, string name, string expected)
        {
            string p = AttrPath(objPath, name);
            return obj.AttributeExists(name) ? obj.Attribute(name) : throw Missing(p, $"an attribute holding {expected}");
        }

        private static string AttrPath(string objPath, string name) => (objPath == "/" ? "/" : objPath + "/") + "@" + name;

        /// <summary>A numeric attribute's values, whatever integer or float width the file chose (openEMS writes
        /// frequencies as float64, sar_calc as float32; both are the pinned layout).</summary>
        public double[] Numbers(IH5Object obj, string objPath, string name, string expected)
        {
            var a = Attribute(obj, objPath, name, expected);
            var t = a.Type;
            double[]? v = (t.Class, t.Size) switch
            {
                (H5DataTypeClass.FloatingPoint, 8) => a.Read<double[]>(),
                (H5DataTypeClass.FloatingPoint, 4) => Array.ConvertAll(a.Read<float[]>(), x => (double)x),
                (H5DataTypeClass.FixedPoint, 1) => t.FixedPoint.IsSigned ? Array.ConvertAll(a.Read<sbyte[]>(), x => (double)x) : Array.ConvertAll(a.Read<byte[]>(), x => (double)x),
                (H5DataTypeClass.FixedPoint, 2) => t.FixedPoint.IsSigned ? Array.ConvertAll(a.Read<short[]>(), x => (double)x) : Array.ConvertAll(a.Read<ushort[]>(), x => (double)x),
                (H5DataTypeClass.FixedPoint, 4) => t.FixedPoint.IsSigned ? Array.ConvertAll(a.Read<int[]>(), x => (double)x) : Array.ConvertAll(a.Read<uint[]>(), x => (double)x),
                (H5DataTypeClass.FixedPoint, 8) => t.FixedPoint.IsSigned ? Array.ConvertAll(a.Read<long[]>(), x => (double)x) : Array.ConvertAll(a.Read<ulong[]>(), x => (double)x),
                _ => null,
            };
            return v ?? throw Unexpected(AttrPath(objPath, name), Describe(t), "a number");
        }

        public double Number(IH5Object obj, string objPath, string name, string expected)
        {
            var v = Numbers(obj, objPath, name, expected);
            return v.Length == 1 ? v[0] : throw Unexpected(AttrPath(objPath, name), $"{v.Length} values", "one value");
        }

        public string Text(IH5Object obj, string objPath, string name, string expected)
        {
            var a = Attribute(obj, objPath, name, expected);
            return a.Type.Class is H5DataTypeClass.String or H5DataTypeClass.VariableLength
                ? a.Read<string>()
                : throw Unexpected(AttrPath(objPath, name), Describe(a.Type), "a string");
        }

        public static string Describe(IH5DataType t) => t.Class switch
        {
            H5DataTypeClass.FloatingPoint => $"float{t.Size * 8}",
            H5DataTypeClass.FixedPoint => $"{(t.FixedPoint.IsSigned ? "int" : "uint")}{t.Size * 8}",
            H5DataTypeClass.Compound => "a compound of " + string.Join(", ", t.Compound.Members.Select(m => $"{m.Name}: {Describe(m.Type)}")),
            _ => $"of type class {t.Class}",
        };

        public void RequireVersion()
        {
            double v = Number(Root, "/", "openEMS_HDF5_version", "the file format version");
            if (v != PinnedVersion)
                throw Unexpected("/@openEMS_HDF5_version", $"version {v.ToString(CultureInfo.InvariantCulture)}",
                                 $"version {PinnedVersion.ToString(CultureInfo.InvariantCulture)}");
        }

        /// <summary>The root attributes every openEMS dump carries; returns dual_mesh (cell centres when true).</summary>
        public bool RequireRootHeader(out int dumpType)
        {
            RequireVersion();
            dumpType = (int)Number(Root, "/", "dump_type", "the dump type");
            return Number(Root, "/", "dual_mesh", "whether the mesh is the cell-centre mesh") != 0;
        }

        public void RequireFloat(IH5Dataset ds, string path, int size)
        {
            if (ds.Type.Class != H5DataTypeClass.FloatingPoint || ds.Type.Size != size)
                throw Unexpected(path, Describe(ds.Type), $"float{size * 8}");
        }

        public void RequireShape(IH5Dataset ds, string path, ulong[] dims)
        {
            if (!ds.Space.Dimensions.SequenceEqual(dims))
                throw Unexpected(path, $"of shape ({string.Join(", ", ds.Space.Dimensions)})", $"shape ({string.Join(", ", dims)})");
        }

        public void RequireOrder(IH5Dataset ds, string path, string expected)
        {
            string order = Text(ds, path, "d_order", "the array order");
            if (order != expected)
                throw Unexpected(path + "/@d_order", $"'{order}'", $"'{expected}' (a legacy, reversed dump is not read)");
        }

        public OpenEmsMesh ReadMesh()
        {
            var g = Group("/Mesh", "the mesh lines");
            double type = Number(g, "/Mesh", "mesh_type", "the coordinate system");
            if (type != 0)
                throw Unexpected("/Mesh/@mesh_type", $"mesh type {type} (not Cartesian)", "mesh type 0 (Cartesian x, y, z)");
            double unit = Number(g, "/Mesh", "mesh_scaling", "the drawing unit");
            var lines = new[] { "x", "y", "z" }.Select(a =>
            {
                string p = "/Mesh/" + a;
                var ds = Dataset(p, "the mesh lines along " + a);
                RequireFloat(ds, p, 8);
                if (ds.Space.Dimensions.Length != 1)
                    throw Unexpected(p, $"of rank {ds.Space.Dimensions.Length}", "rank 1");
                return ds.Read<double[]>();
            }).ToArray();
            return new OpenEmsMesh(lines[0], lines[1], lines[2], unit);
        }

        public double[] ReadFrequencies() =>
            Numbers(Group("/FieldData/FD", "the frequency-domain data (a time-domain dump is not read)"),
                    "/FieldData/FD", "frequency", "the dump's frequencies in Hz");

        /// <summary>A vector dataset's three components over x indices [xStart, xStart + count), one component at a time.</summary>
        public ComplexF[][] ReadVector(int frequencyIndex, OpenEmsMesh mesh, int xStart, int count)
        {
            string p = $"/FieldData/FD/f{frequencyIndex}";
            var ds = Dataset(p, "the field at that frequency (a compound of r and i)");
            var t = ds.Type;
            if (t.Class != H5DataTypeClass.Compound || t.Size != 8 || t.Compound.Members.Length != 2
                || t.Compound.Members[0] is not { Name: "r", Offset: 0, Type: { Class: H5DataTypeClass.FloatingPoint, Size: 4 } }
                || t.Compound.Members[1] is not { Name: "i", Offset: 4, Type: { Class: H5DataTypeClass.FloatingPoint, Size: 4 } })
                throw Unexpected(p, Describe(t), "a compound of r: float32, i: float32");
            RequireShape(ds, p, [3, (ulong)mesh.Nx, (ulong)mesh.Ny, (ulong)mesh.Nz]);
            RequireOrder(ds, p, OpenEmsArrayOrder.Vector);
            return Enumerable.Range(0, 3).Select(c => ds.Read<ComplexF[]>(fileSelection: new HyperslabSelection(4,
                [(ulong)c, (ulong)xStart, 0, 0], [1, (ulong)count, (ulong)mesh.Ny, (ulong)mesh.Nz]))).ToArray();
        }
    }

    private static int LengthOf(this OpenEmsMesh m, int axis) => axis switch { 0 => m.Nx, 1 => m.Ny, _ => m.Nz };
}
