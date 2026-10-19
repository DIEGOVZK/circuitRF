// brief-sar-6 — SAR and B1± as results of an openEMS run: each driven port's raw SAR dump through SAR-5's engine, B1 from the
// H dump beside it, each number normalised to the power the port accepted, the volumes written beside the dump
// (SarVolumeFiles), and the scalars published into the run's DataSet as the `sar` group. docs/design/sar.md §8.
//
// NO OPENEMS PROCESS IS STARTED HERE (sar_calc aside, when the setup asks for the cross-check). Everything is read from what a
// run left in its port directories, so SarResultsWiringTests drives it from SAR-1's kept dipole dump, and a run that is
// re-processed reads what openEMS wrote, not a second solve.
//
// WHICH TISSUE A CELL IS. The dump carries ρ per cell and nothing naming its solid. A named region, B1's region and the peak's
// tissue each need one, so a cell's centre is tested against the tissue solids, the last-constructed first (it is the one
// CSXCAD gives the cell: the innermost layer of a phantom); a cell no solid claims — a centre on a staircased face of a shape
// whose test is its bounding box — takes the tissue of the nearest density. The engine never sees any of this: it takes a mask.
//
// ON A CYLINDRICAL GRID (brief-sar-8b R-sar8b-4) the dump's cells are (ρ, α, z): a cell's centre is turned into the world before it
// is tested against a solid, the peak's centre is reported in the world, no cube is averaged (a cube is a Cartesian grid's: the
// run service removed the masses and said so), and B1 is read from an HDF5 H dump whose Hρ and Hα are turned onto the world's
// axes at the cell's own azimuth (sar.md §1.5) before B1± are formed. The volumes and their lines files stay in (ρ, α, z), and
// the record says so (SarRunRecord.Grid), so nothing that reads a Cartesian volume reads them.

using System.Globalization;
using System.Numerics;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine;
using CircuitRF.Engine.Em3d;
using CircuitRF.Engine.Em3d.Sar;
using RfCore.Data;

namespace CircuitRF.Design.Em3d;

/// <summary>A driven port at a SAR frequency: its own voltage and current there, from FdtdPortTransform (circuitRF's DFT).</summary>
public sealed record SarPortDrive(int Port, double FrequencyHz, Complex U, Complex I)
{
    /// <summary>The power the port accepted, W, in openEMS's DFT convention (SarVolumeFiles' header): 2·Re(U·I*).</summary>
    public double AcceptedPowerW => 2 * (U * Complex.Conjugate(I)).Real;
}

/// <summary>What a SAR post-processing produced: a record per (port, frequency), and what the run should say.</summary>
public sealed record SarProcessOutcome(IReadOnlyList<SarRunRecord> Records, IReadOnlyList<string> Notes, IReadOnlyList<string> Warnings,
                                       string? Error);

public static class OpenEmsSarResults
{
    /// <summary>The DataSet group the scalars are published in.</summary>
    public const string Group = "sar";

    /// <summary>The scalar cubes (R-sar6-7). The peak cubes are <c>psSAR&lt;m&gt;g</c> per averaged mass.</summary>
    public const string WholeSarCube = "SARwb", AbsorbedCube = "Pabs", AcceptedCube = "Pacc", RadiatedCube = "Prad",
                        MassCube = "Mass", BudgetCube = "PowerBudget", B1MeanCube = "B1pMean", EfficiencyCube = "SarEff";

    /// <summary>The peak cube of an averaged quantity: <c>psSAR10g</c>.</summary>
    public static string PeakCube(double grams) => "ps" + SarSettings.AveragedName(grams);

    /// <summary>What the run normalises to (R-sar6-3), as a readout names it.</summary>
    public const string Normalisation = "per 1 W accepted";

    /// <summary>
    /// Every driven port's SAR at every SAR frequency. <paramref name="drives"/> gives each port's U and I at each frequency;
    /// <paramref name="efficiencies"/> each port's radiated-over-accepted power over the sweep, when the setup asked for a
    /// radiation pattern (interpolated linearly to a SAR frequency between two of its points).
    /// </summary>
    public static SarProcessOutcome Process(Em3dSarPlan plan, Em3dProblem problem, string runDir, IReadOnlyList<SarPortDrive> drives,
                                            IReadOnlyList<(double Hz, int Port, double Efficiency)>? efficiencies = null,
                                            SolverInstallation? openEms = null, int? maxCores = null, RunControl? control = null,
                                            CancellationToken ct = default, FdtdCylinder? cylinder = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(drives);
        var s = plan.Settings;
        var records = new List<SarRunRecord>();
        var notes = new List<string>();
        var warnings = new List<string>();
        var tissue = problem.Solids.Where(x => plan.Densities.ContainsKey(x.Name)).OrderByDescending(x => x.Order).ToList();
        var materials = problem.Materials.ToDictionary(m => m.Name, StringComparer.Ordinal);
        var b1Set = plan.B1Region.ToHashSet(StringComparer.Ordinal);
        var b1Solids = problem.Solids.Where(x => b1Set.Contains(x.Name)).OrderByDescending(x => x.Order).ToList();
        var regionSet = plan.Region.ToHashSet(StringComparer.Ordinal);

        control?.BeginStage("SAR and B1", drives.Count, "dumps");
        foreach (var drive in drives)
        {
            ct.ThrowIfCancellationRequested();
            int j = IndexOf(s.FrequenciesHz, drive.FrequencyHz);
            string dir = Path.Combine(runDir, OpenEmsRun.PortDirectory(drive.Port));
            string raw = Path.Combine(dir, Em3dSar.RawDumpFile(j));
            string at = $"{MaterialDispersion.Hz(drive.FrequencyHz)} (port {drive.Port})";
            if (!File.Exists(raw))
                return new([], notes, warnings, $"openEMS wrote no SAR dump at {at}: '{raw}' is missing.");

            SarGrid grid;
            try { grid = OpenEmsSarGrid.Read(raw); }
            catch (Exception e) when (e is IOException or InvalidDataException or RfCore.Hdf5.OpenEmsHdf5Exception or ArgumentException)
            {
                return new([], notes, warnings, $"The SAR dump at {at} could not be read: {e.Message}");
            }

            // ── which tissue each cell is, when anything needs it ───────────────────────────────────────
            bool named = s.Region is not null;
            int[]? owner = named || s.B1 ? Owners(grid, tissue, plan.Densities, cylinder) : null;
            var region = named
                ? SarRegion.Mask(Mask(owner!, i => regionSet.Contains(tissue[i].Name)), plan.RegionName)
                : SarRegion.All;

            SarResult result;
            try
            {
                result = SarEngine.Compute(grid, new SarOptions
                {
                    MassesGrams = s.MassesGrams, Method = s.Method, Region = region, MaxDegreeOfParallelism = maxCores ?? 0,
                });
            }
            catch (ArgumentException e) { return new([], notes, warnings, $"SAR at {at} could not be computed: {e.Message}"); }
            ct.ThrowIfCancellationRequested();

            var record = new SarRunRecord
            {
                Port = drive.Port, FrequencyHz = drive.FrequencyHz, Index = j + 1, AcceptedPowerW = drive.AcceptedPowerW,
                RadiationEfficiency = Efficiency(efficiencies, drive.Port, drive.FrequencyHz),
                Region = plan.RegionName, AbsorbedPowerW = result.AbsorbedPowerW, TissueMassKg = result.TissueMassKg,
                UncountedPowerW = result.UncountedPowerW, Shape = [grid.Nx, grid.Ny, grid.Nz],
                Grid = cylinder is null ? null : SarRunRecord.CylindricalGrid,
            };
            if (!(record.AcceptedPowerW > 0))
                warnings.Add($"Port {drive.Port} accepted no power at {MaterialDispersion.Hz(drive.FrequencyHz)} ({G(record.AcceptedPowerW)} W), " +
                             "so its SAR cannot be referred to a watt accepted; the values as solved are kept in the run directory.");
            if (result.UncountedPowerW > 1e-3 * Math.Max(result.AbsorbedPowerW, double.Epsilon))
                warnings.Add($"At {at}, {G(result.UncountedPowerW / Math.Max(result.AbsorbedPowerW + result.UncountedPowerW, double.Epsilon) * 100)} % " +
                             "of the dissipated power is in cells with no density, which every SAR tool treats as air: a lossy material " +
                             "with no density reached the dump.");

            // ── the volumes, as solved ──────────────────────────────────────────────────────────────────
            int[] shape = [grid.Nx, grid.Ny, grid.Nz];
            Write(dir, j, SarVolumeFiles.LocalQuantity, result.LocalSar, shape, record);
            if (result.LocalPeak is { } lp) record.Peaks.Add(Peak(SarVolumeFiles.LocalQuantity, lp, grid, owner, tissue, materials, cylinder));
            foreach (var avg in result.Averages)
            {
                string q = SarSettings.AveragedName(avg.MassGrams);
                Write(dir, j, q, avg.Sar, shape, record);
                if (avg.Peak is { } p) record.Peaks.Add(Peak(q, p, grid, owner, tissue, materials, cylinder));
            }
            SarVolumeFiles.WriteFloat64(Path.Combine(dir, SarVolumeFiles.LinesFile(j, 'x')), Centres(grid.X));
            SarVolumeFiles.WriteFloat64(Path.Combine(dir, SarVolumeFiles.LinesFile(j, 'y')), Centres(grid.Y));
            SarVolumeFiles.WriteFloat64(Path.Combine(dir, SarVolumeFiles.LinesFile(j, 'z')), Centres(grid.Z));

            // ── B1 from the H dump over the same cells ─────────────────────────────────────────────────
            if (s.B1)
            {
                record.B0Axis = s.B0AxisName;
                record.B1Region = plan.B1RegionName;
                if (B1(dir, j, grid, s, owner!, tissue, b1Solids, b1Set, cylinder, out float[]? plus, out float[]? minus, out double? mean) is { } b1Why)
                    warnings.Add($"B1 at {at} was not computed: {b1Why}");
                else
                {
                    Write(dir, j, SarVolumeFiles.B1PlusQuantity, plus!, shape, record);
                    Write(dir, j, SarVolumeFiles.B1MinusQuantity, minus!, shape, record);
                    record.B1pMeanT = mean;
                    if (mean is null) warnings.Add($"B1's region '{plan.B1RegionName}' holds no cell of the dump at {at}, so its mean |B1+| is not reported.");
                }
                OpenEmsDeleteSnapshots(dir, Em3dSar.HDump(j));
            }

            // ── sar_calc beside it (R-sar5-6) ──────────────────────────────────────────────────────────
            var check = OpenEmsSarCalc.CrossCheck(s.CrossCheck, openEms, raw, result, s.Method, Path.Combine(dir, $"sar_calc{j + 1}"), ct);
            notes.AddRange(check.Notes);
            warnings.AddRange(check.Warnings);
            if (check.Rows.Count > 0)
                record.CrossCheck = [.. check.Rows.Select(r => new SarCrossCheckRecord
                {
                    MassGrams = r.MassGrams, Peak = r.Peak, SarCalcPeak = r.SarCalcPeak, RelativeDifference = r.RelativeDifference,
                })];

            SarVolumeFiles.WriteRecord(Path.Combine(dir, SarVolumeFiles.RecordFile(j)), record);
            records.Add(record);
            notes.Add(Readout(record));
            control?.TickStage();
        }
        notes.Add($"SAR and B1 are valid at their frequency only: each tissue is evaluated once, and an openEMS run's materials are " +
                  "constant over its band (overview L6).");
        return new(records, notes, warnings, null);
    }

    /// <summary>R-sar6-7 — the scalars as cubes over the SAR frequencies and the driven ports, normalised to 1 W accepted (Pacc
    /// as solved). A value the run did not produce is NaN; a cube no record carries is left out.</summary>
    public static void AddTo(DataSet data, IReadOnlyList<SarRunRecord> records, SarSettings settings)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (records.Count == 0) return;
        var freqs = settings.FrequenciesHz.ToArray();
        var ports = records.Select(r => r.Port).Distinct().Order().ToArray();
        var f = new Axis("f", freqs, "Hz");
        var p = new Axis("Port", [.. ports.Select(x => (double)x)], "");
        void Cube(string name, string unit, Func<SarRunRecord, double?> value)
        {
            var v = new double[freqs.Length * ports.Length];
            bool any = false;
            for (int i = 0; i < freqs.Length; i++)
                for (int k = 0; k < ports.Length; k++)
                {
                    var r = records.FirstOrDefault(x => x.Port == ports[k] && Math.Abs(x.FrequencyHz - freqs[i]) <= 1e-9 * freqs[i]);
                    double? x = r is null ? null : value(r);
                    any |= x is not null;
                    v[i * ports.Length + k] = x ?? double.NaN;
                }
            if (any) data.AddToGroup(Group, name, new DataCube([f, p], v) { Unit = unit });
        }
        foreach (double m in settings.MassesGrams)
            Cube(PeakCube(m), "W/kg", r => r.PeakPerWatt(SarSettings.AveragedName(m)));
        Cube(WholeSarCube, "W/kg", r => r.WholeSarPerWatt);
        Cube(AbsorbedCube, "W", r => r.AbsorbedFraction);
        Cube(AcceptedCube, "W", r => r.AcceptedPowerW);
        Cube(RadiatedCube, "W", r => r.RadiationEfficiency);
        Cube(BudgetCube, "", r => r.PowerBudget);
        Cube(MassCube, "kg", r => r.TissueMassKg);
        Cube(B1MeanCube, "T", r => r.B1pMeanPerSqrtWatt);
        Cube(EfficiencyCube, "", r => r.SarEfficiency);
    }

    /// <summary>The run's one line per port and SAR frequency: what the readout table shows.</summary>
    public static string Readout(SarRunRecord r)
    {
        var parts = new List<string>();
        foreach (var peak in r.Peaks.Where(x => x.Quantity != SarVolumeFiles.LocalQuantity))
            if (r.PeakPerWatt(peak.Quantity) is { } v)
                parts.Add($"ps{peak.Quantity} {G(v)} W/kg at {Mm(peak.CentreM)}" + (peak.Object is { } o ? $" in '{o}'" : ""));
        if (r.PeakPerWatt(SarVolumeFiles.LocalQuantity) is { } local) parts.Add($"peak local SAR {G(local)} W/kg");
        if (r.WholeSarPerWatt is { } wb) parts.Add($"whole-region SAR {G(wb)} W/kg over {G(r.TissueMassKg)} kg ({r.Region})");
        if (r.AbsorbedFraction is { } a) parts.Add($"absorbed {Pct(a)} of accepted");
        if (r.RadiationEfficiency is { } eta) parts.Add($"radiated {Pct(eta)}, budget {Pct(r.PowerBudget ?? 0)}");
        if (r.B1pMeanPerSqrtWatt is { } b) parts.Add($"mean |B1+| {G(b * 1e6)} µT/√W over '{r.B1Region}'");
        if (r.SarEfficiency is { } e) parts.Add($"SAR efficiency {G(e * 1e6)} µT/√(W/kg)");
        foreach (var c in r.CrossCheck ?? [])
            if (c.SarCalcPeak is { } theirs && r.AcceptedPowerW > 0)
                parts.Add($"sar_calc {G(c.MassGrams)} g peak {G(theirs / r.AcceptedPowerW)} W/kg");
        return $"SAR at {MaterialDispersion.Hz(r.FrequencyHz)}, port {r.Port} driven, {Normalisation} " +
               $"({G(r.AcceptedPowerW)} W as solved): {string.Join("; ", parts)}.";
    }

    // ── the pieces ──────────────────────────────────────────────────────────────────────────────────────

    private static int IndexOf(IReadOnlyList<double> freqs, double hz)
    {
        for (int i = 0; i < freqs.Count; i++) if (Math.Abs(freqs[i] - hz) <= 1e-9 * Math.Max(freqs[i], hz)) return i;
        throw new ArgumentException($"{hz} Hz is not one of the setup's SAR frequencies.");
    }

    private static void Write(string dir, int j, string quantity, float[] values, int[] shape, SarRunRecord record)
    {
        SarVolumeFiles.WriteFloat32(Path.Combine(dir, SarVolumeFiles.VolumeFile(j, quantity)), values, shape);
        record.Volumes.Add(quantity);
    }

    private static double[] Centres(SarAxis a)
    {
        var c = new double[a.Count];
        for (int i = 0; i < c.Length; i++) c[i] = a.Centre(i);
        return c;
    }

    /// <summary>Each cell's tissue: an index into <paramref name="tissue"/>, or −1 for background (ρ = 0). brief-sar-8b — on a
    /// cylindrical grid each centre is turned into the world first.</summary>
    internal static int[] Owners(SarGrid grid, IReadOnlyList<Em3dSolid> tissue, IReadOnlyDictionary<string, double> densities,
                                 FdtdCylinder? cylinder = null)
    {
        var owner = new int[grid.Count];
        double[] rho = [.. tissue.Select(t => densities[t.Name])];
        Parallel.For(0, grid.Nx, i =>
        {
            double x = grid.X.Centre(i);
            for (int jj = 0; jj < grid.Ny; jj++)
            {
                double y = grid.Y.Centre(jj);
                for (int k = 0; k < grid.Nz; k++)
                {
                    int c = grid.Index(i, jj, k);
                    if (!(grid.Density[c] > 0)) { owner[c] = -1; continue; }
                    var q = WorldCentre(cylinder, x, y, grid.Z.Centre(k));
                    int found = -1;
                    for (int t = 0; t < tissue.Count && found < 0; t++)
                        if (Holds(tissue[t], q)) found = t;
                    if (found < 0)
                    {
                        double best = double.PositiveInfinity;
                        for (int t = 0; t < tissue.Count; t++)
                            if (Math.Abs(rho[t] - grid.Density[c]) is var d && d < best) { best = d; found = t; }
                    }
                    owner[c] = found;
                }
            }
        });
        return owner;
    }

    private static bool[] Mask(int[] owner, Func<int, bool> keep)
    {
        var m = new bool[owner.Length];
        for (int c = 0; c < m.Length; c++) m[c] = owner[c] >= 0 && keep(owner[c]);
        return m;
    }

    /// <summary>brief-sar-8b — a dump cell's centre in the world: as it is on a Cartesian grid, turned from (ρ, α, z) on a cylindrical one.</summary>
    internal static Point3 WorldCentre(FdtdCylinder? cylinder, double x, double y, double z)
        => cylinder is null ? new Point3(x, y, z) : cylinder.World(x, y, z);

    private static SarPeakRecord Peak(string quantity, SarPeak p, SarGrid grid, int[]? owner, IReadOnlyList<Em3dSolid> tissue,
                                      IReadOnlyDictionary<string, Em3dMaterial> materials, FdtdCylinder? cylinder = null)
    {
        var at = WorldCentre(cylinder, p.XM, p.YM, p.ZM);
        int t = owner is not null ? owner[grid.Index(p.I, p.J, p.K)] : OwnerAt(at, tissue);
        var solid = t >= 0 && t < tissue.Count ? tissue[t] : null;
        return new SarPeakRecord
        {
            Quantity = quantity, Value = p.Value, Cell = [p.I, p.J, p.K], CentreM = [at.X, at.Y, at.Z],
            CubeLoM = p.Cube?.LoM, CubeHiM = p.Cube?.HiM, CubeMassKg = p.Cube?.MassKg,
            Kind = p.Cube is null ? null : p.Kind.ToString(), Object = solid?.Name,
            Material = solid is not null && materials.ContainsKey(solid.Material) ? solid.Material : null,
        };
    }

    /// <summary>Whether <paramref name="s"/> holds <paramref name="q"/> — through its voxel's centre when its phantom is voxelized
    /// (brief-sar-8), so a cell is the tissue CSXCAD gave it.</summary>
    private static bool Holds(Em3dSolid s, Point3 q) => s.Holds(q, (p, at) => C3dThermal.Inside(p, at, 0));

    private static int OwnerAt(Point3 q, IReadOnlyList<Em3dSolid> tissue)
    {
        for (int t = 0; t < tissue.Count; t++) if (Holds(tissue[t], q)) return t;
        return -1;
    }

    /// <summary>
    /// R-sar6-4 — |B1+| and |B1−| on the SAR dump's cells from the H dump beside it (openEMS's VTK, cell centres, the same box),
    /// and the volume-weighted mean |B1+| over B1's region. Null on success; otherwise why not.
    /// </summary>
    private static string? B1(string dir, int j, SarGrid grid, SarSettings s, int[] owner, IReadOnlyList<Em3dSolid> tissue,
                              IReadOnlyList<Em3dSolid> b1Solids, HashSet<string> b1Set, FdtdCylinder? cylinder,
                              out float[]? plus, out float[]? minus, out double? mean)
    {
        plus = minus = null;
        mean = null;
        if (cylinder is not null) return CylindricalB1(dir, j, grid, s, owner, tissue, b1Solids, b1Set, cylinder, out plus, out minus, out mean);
        string? abs = Directory.Exists(dir) ? Directory.EnumerateFiles(dir, $"{Em3dSar.HDump(j)}_f=*_abs.vtr").FirstOrDefault() : null;
        if (abs is null) return $"openEMS wrote no H dump ({Em3dSar.HDump(j)}) in '{dir}'.";
        string argFile = abs[..^"_abs.vtr".Length] + "_arg.vtr";
        if (!File.Exists(argFile)) return $"'{argFile}' is missing.";
        VtrData m, a;
        try { (m, a) = (VtrFile.Read(abs), VtrFile.Read(argFile)); }
        catch (Exception e) when (e is IOException or InvalidDataException) { return e.Message; }
        if (m.Components != 3 || a.Values.Length != m.Values.Length) return $"'{abs}' is not a three-component field with its phase file.";
        if (Map(grid.X, m.X) is not { } mx || Map(grid.Y, m.Y) is not { } my || Map(grid.Z, m.Z) is not { } mz)
            return "the H dump's cells are not the SAR dump's.";
        int hnx = m.X.Length, hny = m.Y.Length;
        var p = new float[grid.Count];
        var n = new float[grid.Count];
        double sum = 0, vol = 0;
        var gate = new object();
        Parallel.For(0, grid.Nx, i =>
        {
            double ls = 0, lv = 0;
            for (int jj = 0; jj < grid.Ny; jj++)
                for (int k = 0; k < grid.Nz; k++)
                {
                    int node = (mz[k] * hny + my[jj]) * hnx + mx[i];
                    Complex H(int comp) => Complex.FromPolarCoordinates(m.Values[3 * node + comp], a.Values[3 * node + comp]);
                    var (bp, bm) = SarB1.FromH(H(0), H(1), H(2), s.B0Axis, s.B0Sign);
                    int c = grid.Index(i, jj, k);
                    p[c] = (float)bp.Magnitude;
                    n[c] = (float)bm.Magnitude;
                    bool inRegion = owner[c] >= 0 ? b1Set.Contains(tissue[owner[c]].Name)
                                  : b1Solids.Any(b => Holds(b, new Point3(grid.X.Centre(i), grid.Y.Centre(jj), grid.Z.Centre(k))));
                    if (!inRegion) continue;
                    double v = grid.Volume[c];
                    ls += bp.Magnitude * v;
                    lv += v;
                }
            lock (gate) { sum += ls; vol += lv; }
        });
        plus = p;
        minus = n;
        mean = vol > 0 ? sum / vol : null;
        return null;
    }

    /// <summary>brief-sar-8b — the H dump beside a cylindrical grid's raw dump, as openEMS's HDF5 dump writes it there.</summary>
    public static string CylindricalHFile(int j) => Em3dSar.HDump(j) + ".h5";

    /// <summary>
    /// brief-sar-8b R-sar8b-4 — |B1+| and |B1−| on a cylindrical grid: the HDF5 H dump's (Hρ, Hα, Hz) on the raw dump's cells, each
    /// turned onto the world's axes at the cell's own azimuth (FdtdCylinder.WorldComponents), then B1± as on a Cartesian grid.
    /// </summary>
    private static string? CylindricalB1(string dir, int j, SarGrid grid, SarSettings s, int[] owner, IReadOnlyList<Em3dSolid> tissue,
                                         IReadOnlyList<Em3dSolid> b1Solids, HashSet<string> b1Set, FdtdCylinder cylinder,
                                         out float[]? plus, out float[]? minus, out double? mean)
    {
        plus = minus = null;
        mean = null;
        string file = Path.Combine(dir, CylindricalHFile(j));
        if (!File.Exists(file)) return $"openEMS wrote no H dump ('{file}').";
        RfCore.Hdf5.OpenEmsField h;
        try { h = RfCore.Hdf5.OpenEmsHdf5.ReadField(file); }
        catch (Exception e) when (e is IOException or InvalidDataException or RfCore.Hdf5.OpenEmsHdf5Exception or ArgumentException) { return e.Message; }
        if (!h.Mesh.Cylindrical) return $"'{file}' is not on a cylindrical grid.";
        if (Map(grid.X, h.Mesh.X) is not { } mx || Map(grid.Y, h.Mesh.Y) is not { } my || Map(grid.Z, h.Mesh.Z) is not { } mz)
            return "the H dump's cells are not the SAR dump's.";
        int hny = h.Mesh.Ny, hnz = h.Mesh.Nz;
        var p = new float[grid.Count];
        var n = new float[grid.Count];
        double sum = 0, vol = 0;
        var gate = new object();
        Parallel.For(0, grid.Nx, i =>
        {
            double ls = 0, lv = 0;
            for (int jj = 0; jj < grid.Ny; jj++)
            {
                double alpha = grid.Y.Centre(jj);
                for (int k = 0; k < grid.Nz; k++)
                {
                    int node = (mx[i] * hny + my[jj]) * hnz + mz[k];
                    var (hx, hy, hz) = cylinder.WorldComponents(h.X[node].ToComplex(), h.Y[node].ToComplex(), h.Z[node].ToComplex(), alpha);
                    var (bp, bm) = SarB1.FromH(hx, hy, hz, s.B0Axis, s.B0Sign);
                    int c = grid.Index(i, jj, k);
                    p[c] = (float)bp.Magnitude;
                    n[c] = (float)bm.Magnitude;
                    bool inRegion = owner[c] >= 0 ? b1Set.Contains(tissue[owner[c]].Name)
                                  : b1Solids.Any(b => Holds(b, cylinder.World(grid.X.Centre(i), alpha, grid.Z.Centre(k))));
                    if (!inRegion) continue;
                    double v = grid.Volume[c];
                    ls += bp.Magnitude * v;
                    lv += v;
                }
            }
            lock (gate) { sum += ls; vol += lv; }
        });
        plus = p;
        minus = n;
        mean = vol > 0 ? sum / vol : null;
        return null;
    }

    /// <summary>Each SAR cell centre's index among the H dump's centres; null when one has none within a quarter cell.</summary>
    internal static int[]? Map(SarAxis axis, double[] h)
    {
        var map = new int[axis.Count];
        int at = 0;
        for (int i = 0; i < axis.Count; i++)
        {
            double c = axis.Centre(i);
            while (at + 1 < h.Length && Math.Abs(h[at + 1] - c) <= Math.Abs(h[at] - c)) at++;
            if (Math.Abs(h[at] - c) > 0.25 * axis.Widths[i]) return null;
            map[i] = at;
        }
        return map;
    }

    /// <summary>openEMS's 21 fixed-phase snapshots of a VTK frequency-domain dump, removed: the magnitude and phase files carry
    /// the field exactly (CsxcadWriter's dump comment).</summary>
    private static void OpenEmsDeleteSnapshots(string dir, string dump)
    {
        foreach (string path in Directory.EnumerateFiles(dir, $"{dump}_*_p=*.vtr"))
            try { File.Delete(path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Port <paramref name="port"/>'s radiation efficiency at <paramref name="hz"/>: the sweep's point there, or linear
    /// between the two around it; null without a pattern or outside its points.</summary>
    private static double? Efficiency(IReadOnlyList<(double Hz, int Port, double Efficiency)>? table, int port, double hz)
    {
        if (table is null) return null;
        var pts = table.Where(t => t.Port == port && double.IsFinite(t.Efficiency)).OrderBy(t => t.Hz).ToList();
        for (int i = 0; i < pts.Count; i++)
        {
            if (Math.Abs(pts[i].Hz - hz) <= 1e-9 * hz) return pts[i].Efficiency;
            if (i + 1 < pts.Count && pts[i].Hz < hz && hz < pts[i + 1].Hz)
                return pts[i].Efficiency + (pts[i + 1].Efficiency - pts[i].Efficiency) * (hz - pts[i].Hz) / (pts[i + 1].Hz - pts[i].Hz);
        }
        return null;
    }

    private static string Mm(double[] m)
        => "(" + string.Join(", ", m.Select(v => (v * 1e3).ToString("0.#", CultureInfo.InvariantCulture))) + ") mm";

    private static string Pct(double f) => (f * 100).ToString("0.0", CultureInfo.InvariantCulture) + " %";

    private static string G(double v) => v.ToString("G4", CultureInfo.InvariantCulture);
}
