// brief-sar-6 R-sar6-5 — a SAR run's volumes (SarVolumeFiles) as field steps, so a SAR or B1 plot is drawn on clip planes and
// surfaces by exactly the code that draws E.
//
// TWO STEPS PER PORT AND FREQUENCY. A step has one mesh, and the two kinds of volume want different ones: SAR lives in tissue
// and its air is drawn transparent (R-sar6-5), so the SAR step's mesh holds only the lattice cells that touch a tissue node;
// B1 is wanted in the air inside a coil too, so the B1 step's mesh is the whole dump box. Each is a solution of its own,
// labelled (SAR) or (B1), and FieldSolution.Offers says which arrays it holds, so a plot picks the right one by its quantity.
//
// THE LATTICE. Each volume's values sit on the dump's cell centres, which become the step's NODES (as an openEMS E dump's
// cell centres do), split into six tetrahedra a lattice cell exactly as VtrReader.Mesh splits one. The file is C order over
// (x, y, z); the lattice numbers its nodes x fastest, as VTK does.
//
// AS SOLVED, THEN REFERRED. The volumes are openEMS's own scale (SarVolumeFiles' header). Like an E dump they are loaded times
// the solution's DumpScale — SAR by its |·|², B1 by its |·| — which puts them at the incident wave every openEMS and Palace
// field is drawn at; the plot's drive (FieldDrive.Read) then takes them to 1 W accepted unless it says otherwise. B1 is held in
// µT, the unit its legend states.

using System.Numerics;
using System.Text.RegularExpressions;
using CircuitRF.Design.Em3d;

namespace CircuitRF.Render.Scene3D.Fields;

public sealed partial class FieldRun
{
    private static readonly Regex SarRecord = new(@"^sar(\d+)\.json$", RegexOptions.CultureInvariant);

    /// <summary>The SAR and B1 solutions a SAR run left in <paramref name="portDir"/>, the run that drove <paramref name="port"/>.</summary>
    private static IEnumerable<FieldSolution> SarSolutions(string portDir, int port)
    {
        foreach (string file in System.IO.Directory.EnumerateFiles(portDir, "sar*.json"))
        {
            // brief-sar-8b — a cylindrical grid's volumes are (ρ, α, z) cells, which the view's Cartesian lattice cannot draw.
            if (!SarRecord.IsMatch(Path.GetFileName(file)) || SarVolumeFiles.ReadRecord(file) is not { } r || r.IsCylindrical) continue;
            double ghz = r.FrequencyHz / 1e9;
            var sar = r.Volumes.Where(FieldNames.IsSar).ToList();
            if (sar.Count > 0) yield return new FieldSolution(FieldProblemKind.Driven, port, ghz, file, null, "openEMS") { Offers = sar };
            if (r.Volumes.Contains(FieldNames.B1Plus))
                yield return new FieldSolution(FieldProblemKind.Driven, port, ghz,
                                               Path.Combine(portDir, SarVolumeFiles.VolumeFile(r.Index - 1, FieldNames.B1Plus)), null, "openEMS")
                             { Offers = [.. r.Volumes.Where(FieldNames.IsB1)] };
        }
    }
}

public sealed partial class FieldRun
{
    /// <summary>
    /// brief-sar-9 — the fields analyses recombined from <paramref name="runDir"/>'s per-port fields: the SAR and B1 volumes each
    /// left in <c>&lt;run&gt;.fields/cosim/&lt;analysis&gt;/</c>, as solutions of their own. Each carries its drive already: the
    /// circuit's driven port, its Z0 and its reflection, with its volumes at the incident power every driven step is loaded at
    /// (FieldCosimulation's header), so FieldDrive refers it exactly as it refers a port's own.
    /// </summary>
    private static IEnumerable<FieldSolution> CosimSolutions(string runDir)
    {
        string root = Path.Combine(runDir.TrimEnd(Path.DirectorySeparatorChar) + ".fields", FieldCosimulation.CosimFolder);
        if (!System.IO.Directory.Exists(root)) yield break;
        foreach (string dir in System.IO.Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            string recordPath = Path.Combine(dir, SarVolumeFiles.RecordFile(0));
            if (SarVolumeFiles.ReadRecord(recordPath) is not { } r || r.IsCylindrical || FieldCosimulation.ReadRecord(dir) is not { } c) continue;
            double ghz = r.FrequencyHz / 1e9;
            var drive = new FieldPortDrive(c.DrivePort, c.DriveZ0, c.Reflection, Complex.One, true);
            var sar = r.Volumes.Where(FieldNames.IsSar).ToList();
            if (sar.Count > 0)
                yield return new FieldSolution(FieldProblemKind.Driven, 0, ghz, recordPath, null, "openEMS") { Offers = sar, Drive = drive, Cosim = c.Analysis };
            if (r.Volumes.Contains(FieldNames.B1Plus))
                yield return new FieldSolution(FieldProblemKind.Driven, 0, ghz, Path.Combine(dir, SarVolumeFiles.VolumeFile(0, FieldNames.B1Plus)), null, "openEMS")
                             { Offers = [.. r.Volumes.Where(FieldNames.IsB1)], Drive = drive, Cosim = c.Analysis };
        }
    }
}

public sealed partial class FieldStep
{
    /// <summary>brief-sar-6 — the node lattice of a step drawn on a rectilinear grid (a SAR or B1 volume), x fastest; null for
    /// any other step.</summary>
    public (int Nx, int Ny, int Nz)? Lattice { get; init; }

    /// <summary>brief-sar-6 — the SAR record a SAR or B1 step was read with; null for any other step.</summary>
    public SarRunRecord? SarRecord { get; init; }

    /// <summary>Whether <paramref name="path"/> names a SAR run's step: its record (the SAR volumes), or its B1+ volume.</summary>
    public static bool IsSarStep(string path)
        => path.EndsWith(".json", StringComparison.Ordinal) && Regex.IsMatch(Path.GetFileName(path), @"^sar\d+\.json$")
           || path.EndsWith("." + FieldNames.B1Plus + ".npy", StringComparison.Ordinal);

    /// <summary>The record beside a SAR or B1 step's file.</summary>
    public static string SarRecordPath(string path)
    {
        string name = Path.GetFileName(path);
        return Path.Combine(Path.GetDirectoryName(path) ?? "", name[..name.IndexOf('.')] + ".json");
    }

    private static FieldStep OpenSar(string path, double toMetres, Complex scale)
    {
        string recordPath = SarRecordPath(path);
        var record = SarVolumeFiles.ReadRecord(recordPath)
                     ?? throw new FieldReadException($"'{recordPath}' is missing or unreadable: the SAR run's record of what it wrote.");
        bool b1 = !path.EndsWith(".json", StringComparison.Ordinal);
        string dir = Path.GetDirectoryName(recordPath) ?? "";
        int j = record.Index - 1;
        double[] x, y, z;
        try
        {
            x = SarVolumeFiles.ReadFloat64(Path.Combine(dir, SarVolumeFiles.LinesFile(j, 'x')));
            y = SarVolumeFiles.ReadFloat64(Path.Combine(dir, SarVolumeFiles.LinesFile(j, 'y')));
            z = SarVolumeFiles.ReadFloat64(Path.Combine(dir, SarVolumeFiles.LinesFile(j, 'z')));
        }
        catch (Exception e) when (e is IOException or InvalidDataException) { throw new FieldReadException(e.Message); }
        int nx = x.Length, ny = y.Length, nz = z.Length;
        var names = record.Volumes.Where(b1 ? FieldNames.IsB1 : FieldNames.IsSar).ToList();
        if (names.Count == 0) throw new FieldReadException($"'{recordPath}' names no {(b1 ? "B1" : "SAR")} volume.");

        double mag = scale.Magnitude;
        var arrays = new List<FieldArray>();
        foreach (string name in names)
        {
            float[] values;
            int[] shape;
            try { (values, shape) = SarVolumeFiles.ReadFloat32(Path.Combine(dir, SarVolumeFiles.VolumeFile(j, name))); }
            catch (Exception e) when (e is IOException or InvalidDataException) { throw new FieldReadException(e.Message); }
            if (shape is not [var a, var b, var c] || a != nx || b != ny || c != nz)
                throw new FieldReadException($"The {name} volume of '{recordPath}' is not {nx} × {ny} × {nz}.");
            // SAR follows the drive as |scale|², B1 as |scale|, and B1 is held in µT
            double k = FieldNames.IsB1(name) ? mag * 1e6 : mag * mag;
            var nodes = new float[values.Length];
            for (int i = 0; i < nx; i++)
                for (int jj = 0; jj < ny; jj++)
                    for (int kk = 0; kk < nz; kk++)
                        nodes[(kk * ny + jj) * nx + i] = (float)(values[(i * ny + jj) * nz + kk] * k);
            arrays.Add(new FieldArray { Info = new FieldArrayInfo(name, 1, false, false), Re = nodes });
        }

        var grid = new VtrField(x, y, z, b1 ? "B1" : "SAR", 1, []);
        var mesh = b1 ? VtrReader.Mesh(grid, toMetres) : TissueMesh(grid, arrays.First(a => a.Name == FieldNames.LocalSar || FieldNames.IsSar(a.Name)).Re, toMetres);
        var step = new FieldStep
        {
            Pvtu = path, Pieces = [], Mesh = mesh, Arrays = [.. arrays.Select(a => a.Info)], Lattice = (nx, ny, nz), SarRecord = record,
        };
        foreach (var a in arrays) step._loaded[a.Name] = a;
        return step;
    }

    /// <summary>The lattice's cells that touch a tissue node (one whose SAR is above zero), six tetrahedra each, as
    /// <see cref="VtrReader.Mesh"/> splits them; every node is kept, so the arrays index the lattice.</summary>
    private static FieldMesh TissueMesh(VtrField f, float[] sar, double toMetres)
    {
        var all = VtrReader.Mesh(f, toMetres);
        int nx = f.X.Length, ny = f.Y.Length, nz = f.Z.Length;
        var conn = new List<int>();
        for (int k = 0; k + 1 < nz; k++)
            for (int j = 0; j + 1 < ny; j++)
                for (int i = 0; i + 1 < nx; i++)
                {
                    bool tissue = false;
                    for (int b = 0; b < 8 && !tissue; b++)
                        tissue = sar[((k + ((b >> 2) & 1)) * ny + j + ((b >> 1) & 1)) * nx + i + (b & 1)] > 0;
                    if (!tissue) continue;
                    int cell = (k * (ny - 1) + j) * (nx - 1) + i;
                    for (int t = 0; t < 24; t++) conn.Add(all.Cells[24 * cell + t]);
                }
        return new FieldMesh
        {
            Shape = FieldCellShape.Tetrahedron, Order = 1, NodesPerCell = 4, Points = all.Points, Cells = [.. conn],
            Attribute = new int[conn.Count / 4], ToMetres = toMetres,
        };
    }

    /// <summary>
    /// brief-sar-6 R-sar6-5 — the maximum-intensity projection of array <paramref name="name"/> along axis
    /// <paramref name="axis"/> (0 x, 1 y, 2 z): each line of nodes along the axis carries its largest value at every node, so a
    /// clip plane across that axis, anywhere through the volume, draws the projection. Null for a step with no lattice or no
    /// such array.
    /// </summary>
    public FieldStep? WithMip(string name, int axis)
    {
        if (Lattice is not { } l || Load(name) is not { } a || a.Components != 1) return null;
        var (nx, ny, nz) = l;
        var v = new float[a.Re.Length];
        int n0 = axis == 0 ? ny : nx, n1 = axis == 2 ? ny : nz, along = axis == 0 ? nx : axis == 1 ? ny : nz;
        int Node(int p, int q, int t) => axis switch
        {
            0 => (q * ny + p) * nx + t,
            1 => (q * ny + t) * nx + p,
            _ => (t * ny + q) * nx + p,
        };
        for (int p = 0; p < n0; p++)
            for (int q = 0; q < n1; q++)
            {
                float m = float.NegativeInfinity;
                for (int t = 0; t < along; t++) m = Math.Max(m, a.Re[Node(p, q, t)]);
                for (int t = 0; t < along; t++) v[Node(p, q, t)] = m;
            }
        var step = new FieldStep
        {
            Pvtu = $"{Pvtu}#mip{"xyz"[axis]}", Pieces = [], Mesh = Mesh, Arrays = [a.Info], Lattice = Lattice, SarRecord = SarRecord,
        };
        step._loaded[name] = new FieldArray { Info = a.Info, Re = v };
        return step;
    }

    /// <summary>The middle of the step's nodes along <paramref name="axis"/>, metres: where a projection's plane is drawn.</summary>
    public double Middle(int axis)
    {
        float lo = float.PositiveInfinity, hi = float.NegativeInfinity;
        for (int i = axis; i < Mesh.Points.Length; i += 3) { lo = Math.Min(lo, Mesh.Points[i]); hi = Math.Max(hi, Mesh.Points[i]); }
        return 0.5 * (lo + hi) * Mesh.ToMetres;
    }
}

/// <summary>brief-sar-6 R-sar6-5 — the peak of a SAR plot: where it is, the cube it came from (an averaged quantity), and its
/// value as the plot shows it.</summary>
public sealed record FieldSarPeak(string Quantity, double Value, Design.Em3d.SarPeakRecord Record)
{
    /// <summary>The marker's label: <c>psSAR10g 2.43 W/kg</c>, <c>peak SAR 41.2 W/kg</c>.</summary>
    public string Label => (Quantity == FieldNames.LocalSar ? "peak SAR " : "ps" + Quantity + " ")
                           + Value.ToString("G3", System.Globalization.CultureInfo.InvariantCulture) + " W/kg";

    /// <summary>The peak of <paramref name="quantity"/> in <paramref name="step"/>'s record, at the plot's drive: the value as
    /// solved times the step's load scale (|DumpScale|²) times <paramref name="driveFactor"/>. Null for a step that is not a SAR
    /// step, or a quantity with no peak.</summary>
    public static FieldSarPeak? Of(FieldStep step, string quantity, Complex dumpScale, double driveFactor)
    {
        if (!FieldNames.IsSar(quantity) || step.SarRecord?.Peaks.FirstOrDefault(p => p.Quantity == quantity) is not { } p) return null;
        double m = dumpScale.Magnitude;
        return new FieldSarPeak(quantity, p.Value * m * m * driveFactor, p);
    }
}
