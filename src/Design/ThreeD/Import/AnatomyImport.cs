// brief-sar-11 — importing anatomy into a .c3d: the ONE pair of functions the 3D editor's Import ▸ Voxel Model… / Surface Meshes…
// and `circuitrf import voxels|surfaces` both call. A verb and a dialog that each built the object would drift apart (CLAUDE.md:
// an operation that lives only in a view model is not a capability); so the plan, the label table's defaults, the copy into
// the view's folder and the size report are all here, and both front ends only gather choices and say what happened.
//
//   PlanVoxels    a NIfTI / raw / DiscMaterial file, or a tetrahedral .node/.ele pair voxelised at a chosen size → the label
//                 table with its default names and materials, and the size report (R-sar11-5)
//   ApplyVoxels   the plan → the file copied (a voxelised mesh written as .nii.gz) beside the .c3d and one VoxelBody added
//   PlanSurfaces  closed surfaces, one per tissue → each checked, ordered outermost first (R-sar11-4)
//   ApplySurfaces the plan → each file copied and one Mesh object per surface, in a group, innermost last
//
// THE LABEL TABLE'S DEFAULTS. A name: the CSV's, then the file's own (a DiscMaterial file names its entries), then "label k".
// A material: the CSV's material column, then a technology material whose name equals the tissue's name ignoring case — never
// anything fuzzier, the rule a STEP part's material follows (a near match would give a tissue another's conductivity in
// silence). Label 0 is air unless the CSV maps it.
//
// UNMAPPED LABELS ARE A REFUSAL here as in the run (R-sar11-2), naming each label and its voxel count — unless the caller says
// it will map them itself (the dialog, whose table is that mapping), and then the plan carries the list.
//
// SIZE, SAID BEFORE THE RUN (R-sar11-5): the file's voxel count and size, what the crop and the tissue keep, the label
// histogram, and the FDTD cells inside the body with openEMS's memory for them. Past LargeBodyCells it is a WARNING, never a
// refusal: a whole body at 1 mm is a legitimate, slow run.

using System.Globalization;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD.Step;
using CircuitRF.Engine.Em3d;
using RfCore.Hdf5;

namespace CircuitRF.Design.ThreeD.Import;

/// <summary>What a voxel import is told where the file does not say: a raw layout, a label list, a tetrahedral mesh's voxel size
/// and unit, the object's name, a crop, the grid step inside the body (DBU), and per-label materials ("air" for air).</summary>
public sealed record VoxelImportOptions
{
    public string? LabelsCsv { get; init; }
    public RawVolumeLayout? Raw { get; init; }
    public double TetVoxelM { get; init; }
    public double TetUnitM { get; init; } = 1e-3;
    public string? Name { get; init; }
    public C3dVoxelRange? Crop { get; init; }
    public long GridStep { get; init; }
    public IReadOnlyDictionary<int, string>? Materials { get; init; }
    public bool AllowUnmapped { get; init; }
}

/// <summary>One row of the import's label table: a label, how many voxels carry it, its tissue name and material (or air).</summary>
public sealed class VoxelImportRow
{
    public required int Label { get; init; }
    public required long Voxels { get; init; }
    public required string Name { get; set; }
    public string? Material { get; set; }
    public bool Air { get; set; }

    /// <summary>Why the material is what it is: "CSV", "name", "chosen", or empty.</summary>
    public string Match { get; set; } = "";
}

/// <summary>R-sar11-5 — the size of a voxel import, before the run.</summary>
public sealed record VoxelImportReport(
    long Voxels, string Dimensions, double? DxM, double? DyM, double? DzM,
    (int I0, int J0, int K0, int I1, int J1, int K1)? Kept, long KeptVoxels,
    long FdtdCells, long MemoryBytes, string? Warning)
{
    /// <summary>The report as sentences, for Messages, stderr and the dialog.</summary>
    public IEnumerable<string> Lines(IReadOnlyList<VoxelImportRow> rows)
    {
        string Mm(double? m) => m is { } v ? (v * 1e3).ToString("0.###", CultureInfo.InvariantCulture) : "non-uniform";
        yield return string.Create(CultureInfo.InvariantCulture, $"{Voxels:N0} voxels ({Dimensions}), voxel {Mm(DxM)} × {Mm(DyM)} × {Mm(DzM)} mm.");
        if (Kept is { } k)
            yield return string.Create(CultureInfo.InvariantCulture,
                $"Kept: voxels [{k.I0}, {k.I1}) × [{k.J0}, {k.J1}) × [{k.K0}, {k.K1}), {KeptVoxels:N0} voxels around the tissue.");
        foreach (var r in rows)
            yield return string.Create(CultureInfo.InvariantCulture,
                $"  label {r.Label,5}  {r.Voxels,14:N0}  {r.Name}  →  {(r.Air ? "air" : r.Material ?? "(no material)")}");
        yield return string.Create(CultureInfo.InvariantCulture,
            $"About {FdtdCells:N0} FDTD cells inside the body, about {CircuitRF.Design.Em3d.MachineMemory.Format(MemoryBytes)} for openEMS (the air around it adds more).");
        if (Warning is { } w) yield return w;
    }
}

/// <summary>A voxel import read and ready to apply.</summary>
public sealed class VoxelImportPlan
{
    public required string SourcePath { get; init; }
    public required string Format { get; init; }
    public required LabelVolume Volume { get; init; }
    public required List<VoxelImportRow> Rows { get; init; }
    public required VoxelImportReport Report { get; init; }
    public required string Name { get; set; }
    public required VoxelImportOptions Options { get; init; }

    /// <summary>The bytes the body's file will hold, and its name: the source's own, or a voxelised mesh as .nii.gz.</summary>
    public required byte[] Bytes { get; init; }
    public required string FileName { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>Labels voxels carry that no row maps (when the caller allowed them), with their counts.</summary>
    public IReadOnlyList<(int Label, long Voxels)> Unmapped
        => [.. Rows.Where(r => !r.Air && r.Material is not { Length: > 0 }).Select(r => (r.Label, r.Voxels))];
}

/// <summary>One surface of a surface import, checked.</summary>
public sealed class SurfaceImportRow
{
    public required string SourcePath { get; init; }
    public required string Name { get; set; }
    public string? Material { get; set; }
    public string Match { get; set; } = "";
    public required SurfaceMeshCheck Check { get; init; }
    public required int Depth { get; init; }
    public required byte[] Bytes { get; init; }
    public required string Hash { get; init; }
    public bool Flip => Check.SignedVolume < 0;
    public string? Fault => Check.Fault();
}

/// <summary>A surface import read and ready to apply: rows outermost first.</summary>
public sealed class SurfaceImportPlan
{
    public required List<SurfaceImportRow> Rows { get; init; }
    public required string Unit { get; init; }
    public required string Group { get; set; }
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>What an import added: its objects, the files it wrote (created or reused) and what it said.</summary>
public sealed record AnatomyImportResult(IReadOnlyList<C3dObject> Objects, IReadOnlyList<(string Path, byte[] Bytes, bool Created)> Files,
                                         IReadOnlyList<string> Notes);

public static class AnatomyImport
{
    /// <summary>Past this many FDTD cells inside the body, the report warns.</summary>
    public const long LargeBodyCells = 50_000_000;

    /// <summary>Whether <paramref name="path"/> is a file a voxel import reads.</summary>
    public static bool IsVoxelSource(string path)
        => NiftiFile.IsNifti(path) || RawVolume.IsRaw(path) || TetMesh.IsTetMesh(path) ||
           Path.GetExtension(path).ToLowerInvariant() is ".h5" or ".hdf5";

    // ── voxels ─────────────────────────────────────────────────────────────────────────────────

    public static VoxelImportPlan PlanVoxels(string sourcePath, VoxelImportOptions o, Technology? tech, C3dDocument target, int dbuPerMicron)
    {
        string full = Path.GetFullPath(sourcePath);
        var notes = new List<string>();
        LabelVolume volume;
        byte[] bytes;
        string fileName = Path.GetFileName(full), format;
        if (TetMesh.IsTetMesh(full))
        {
            if (!(o.TetVoxelM > 0))
                throw new LabelVolumeException(AnatomyDiagnostics.Option("A tetrahedral mesh is voxelised before import: give the voxel size (the dialog's Voxel, or --voxel)."));
            var mesh = TetMesh.Read(full);
            volume = mesh.Voxelise(o.TetVoxelM, o.TetUnitM);
            bytes = NiftiFile.ToBytes(volume, gzip: true);
            fileName = Path.GetFileNameWithoutExtension(full) + ".nii.gz";
            format = "tetrahedral";
            notes.Add(string.Create(CultureInfo.InvariantCulture,
                $"'{Path.GetFileName(full)}' ({mesh.Tets.Length:N0} tetrahedra) was voxelised at {o.TetVoxelM * 1e3:0.###} mm, each voxel the region of the tetrahedron holding its centre, and kept as '{fileName}'."));
        }
        else
        {
            format = C3dVoxelBodies.FormatOf(full);
            bytes = File.ReadAllBytes(full);
            switch (format)
            {
                case "nifti":
                    var read = NiftiFile.Read(bytes is [0x1f, 0x8b, ..] ? Gunzip(bytes) : bytes, Path.GetFileName(full));
                    volume = read.Volume;
                    notes.AddRange(read.Notes);
                    break;
                case "discmaterial":
                    var disc = OpenEmsHdf5.ReadDiscMaterial(full);
                    volume = new LabelVolume(disc.X, disc.Y, disc.Z, Array.ConvertAll(disc.Indices, i => (ushort)i),
                                             disc.Tissues.Select((t, k) => (k, t.Name)).ToDictionary(t => t.k, t => t.Name));
                    if (disc.Tissues.Any(t => t.Kappa > 0 || t.EpsR != 1))
                        notes.Add($"'{Path.GetFileName(full)}' states each entry's εr, σ and density; circuitRF takes its tissues' values from the materials the label table names, not from the file.");
                    break;
                default:
                    if (o.Raw is null)
                        throw new LabelVolumeException(AnatomyDiagnostics.Option($"'{Path.GetFileName(full)}' is a raw volume: give its dimensions, type and voxel size (the dialog's Raw fields, or --dims, --type, --voxel)."));
                    volume = RawVolume.Read(full, o.Raw);
                    break;
            }
        }

        // the label table: CSV, then the file's names; materials from the CSV, the caller, then an exact name
        var csv = o.LabelsCsv is { } c ? LabelTableCsv.Read(c).ToDictionary(r => r.Label) : [];
        var materials = tech?.ResolvedMaterials ?? [];
        var crop = o.Crop is null ? (0, 0, 0, volume.Nx, volume.Ny, volume.Nz)
                                  : C3dVoxelBodies.CropOf(new C3dVoxelBody { Crop = o.Crop }, volume);
        var histogram = volume.Histogram(crop);
        var rows = new List<VoxelImportRow>();
        foreach (var (label, count) in histogram)
        {
            csv.TryGetValue(label, out var given);
            string name = given?.Name ?? (volume.Names.TryGetValue(label, out var n) && n.Length > 0 ? n : $"label {label}");
            name = name.Replace('/', '_').Replace(',', ' ').Trim();
            var row = new VoxelImportRow { Label = label, Voxels = count, Name = name };
            string? chosen = o.Materials?.GetValueOrDefault(label);
            if (chosen is { } ch)
            {
                if (ch.Equals("air", StringComparison.OrdinalIgnoreCase)) row.Air = true;
                else { row.Material = ch; row.Match = "chosen"; }
            }
            else if (given?.Material is { } m)
            {
                if (m.Equals("air", StringComparison.OrdinalIgnoreCase)) row.Air = true;
                else { row.Material = m; row.Match = "CSV"; }
            }
            else if (label == 0) row.Air = true;
            else if (materials.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) is { } byName)
            {
                row.Material = byName.Name;
                row.Match = "name";
            }
            rows.Add(row);
        }
        // tissue names unique: a second "label k" style collision gets its label appended
        foreach (var g in rows.Where(r => !r.Air).GroupBy(r => r.Name, StringComparer.Ordinal).Where(g => g.Count() > 1))
            foreach (var r in g) r.Name = $"{r.Name} {r.Label}";

        var used = StepImport.UsedNames(target);
        string stem = fileName.EndsWith(".nii.gz", StringComparison.OrdinalIgnoreCase) ? fileName[..^7] : Path.GetFileNameWithoutExtension(fileName);
        string objectName = o.Name ?? StepImport.ProposeName(stem, "body", used);

        var plan = new VoxelImportPlan
        {
            SourcePath = full, Format = format, Volume = volume, Rows = rows, Name = objectName, Options = o, Bytes = bytes,
            FileName = fileName, Notes = notes, Report = Report(volume, rows, crop, o.GridStep, dbuPerMicron),
        };
        if (!o.AllowUnmapped && plan.Unmapped is { Count: > 0 } unmapped)
            throw new LabelVolumeException(AnatomyDiagnostics.Unmapped(C3dVoxelBodies.UnmappedRefusal(objectName, unmapped) +
                                           " A label list (CSV: label,name,material) or --material label=name maps them."));
        return plan;
    }

    /// <summary>R-sar11-5 — the report for <paramref name="rows"/> as they stand (the dialog recomputes it as rows change).</summary>
    public static VoxelImportReport Report(LabelVolume v, IReadOnlyList<VoxelImportRow> rows,
                                           (int I0, int J0, int K0, int I1, int J1, int K1) crop, long gridStep, int dbuPerMicron)
    {
        var air = new HashSet<int>(rows.Where(r => r.Air).Select(r => r.Label));
        var kept = v.Occupied(l => air.Contains(l), crop);
        long keptVoxels = kept is { } k ? (long)(k.I1 - k.I0) * (k.J1 - k.J0) * (k.K1 - k.K0) : 0;
        long cells = keptVoxels;
        double step = C3dLowering.Metres(gridStep, dbuPerMicron);
        if (step > 0 && kept is { } kk)
        {
            long Across(double[] lines, int a, int b) => Math.Max(1, (long)Math.Ceiling((lines[b] - lines[a]) / step - 1e-9));
            cells = Across(v.X, kk.I0, kk.I1) * Across(v.Y, kk.J0, kk.J1) * Across(v.Z, kk.K0, kk.K1);
        }
        long memory = Em3dSizeEstimate.OpenEmsMemoryBytes(cells);
        string? warning = cells > LargeBodyCells
            ? string.Create(CultureInfo.InvariantCulture,
                $"Large: about {cells / 1e6:0} M FDTD cells inside the body alone (~{memory / 1e9:0.#} GB). It runs, slowly; a Crop, or a coarser GridStep, reduces it.")
            : null;
        var (dx, dy, dz) = v.Spacing;
        return new VoxelImportReport(v.Count, v.Dimensions, dx, dy, dz, kept, keptVoxels, cells, memory, warning);
    }

    public static AnatomyImportResult ApplyVoxels(VoxelImportPlan plan, C3dDocument doc, string c3dPath)
    {
        if (NameValidator.Validate(plan.Name) is { } bad) throw new LabelVolumeException(AnatomyDiagnostics.Name($"'{plan.Name}' cannot be a name: {bad}"));
        if (StepImport.UsedNames(doc).Contains(plan.Name)) throw new LabelVolumeException(AnatomyDiagnostics.Name($"'{plan.Name}' is already a name in this document."));
        if (plan.Unmapped is { Count: > 0 } unmapped && !plan.Options.AllowUnmapped)
            throw new LabelVolumeException(AnatomyDiagnostics.Unmapped(C3dVoxelBodies.UnmappedRefusal(plan.Name, unmapped)));
        string dir = Path.GetDirectoryName(Path.GetFullPath(c3dPath))!;
        string hash = StepImport.HashOf(plan.Bytes);
        var (copy, created) = StepImport.CopyInto(dir, plan.FileName, plan.Bytes, hash, write: true);
        var o = plan.Options;
        var body = new C3dVoxelBody
        {
            Name = plan.Name, File = Path.GetFileName(copy), Hash = hash, Crop = o.Crop, GridStep = o.GridStep,
            SourcePath = StepImport.SourceSpelling(plan.SourcePath, c3dPath),
            Raw = plan.Format == "raw" && o.Raw is { } r ? RawOf(r, doc.DbuPerMicron) : null,
            Labels = [.. plan.Rows.Where(row => row.Air ? row.Label != 0 : true)
                                  .Select(row => new C3dVoxelLabel { Label = row.Label, Name = row.Name, Material = row.Air ? null : row.Material, Air = row.Air })],
        };
        doc.Objects.Add(body);
        return new AnatomyImportResult([body], [(copy, plan.Bytes, created)], plan.Notes);
    }

    private static C3dVoxelRaw RawOf(RawVolumeLayout r, int dbuPerMicron)
    {
        long Dbu(double m) => (long)Math.Round(m * 1e6 * dbuPerMicron);
        return new C3dVoxelRaw
        {
            Nx = r.Nx, Ny = r.Ny, Nz = r.Nz, Type = r.Type.ToString().ToLowerInvariant(), VoxelX = Dbu(r.VoxelXM), VoxelY = Dbu(r.VoxelYM),
            VoxelZ = Dbu(r.VoxelZM), BigEndian = r.BigEndian, HeaderBytes = r.HeaderBytes,
        };
    }

    private static byte[] Gunzip(byte[] b)
    {
        using var input = new MemoryStream(b);
        using var z = new System.IO.Compression.GZipStream(input, System.IO.Compression.CompressionMode.Decompress);
        using var ms = new MemoryStream();
        z.CopyTo(ms);
        return ms.ToArray();
    }

    // ── surfaces ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <paramref name="files"/> (STL, OBJ, PLY), each one tissue, in <paramref name="unit"/>: checked, nested outermost first,
    /// named after the file, and given a material from <paramref name="materials"/> (keyed by file name or stem), else the
    /// technology material named as the file's stem.
    /// </summary>
    public static SurfaceImportPlan PlanSurfaces(IReadOnlyList<string> files, string unit, IReadOnlyDictionary<string, string>? materials,
                                                 Technology? tech, C3dDocument target)
    {
        if (C3dMeshes.UnitMetres(unit) is null) throw new LabelVolumeException(AnatomyDiagnostics.Option($"'{unit}' is not a length unit (m, cm, mm, um, in, mil)."));
        if (files.Count == 0) throw new LabelVolumeException(AnatomyDiagnostics.Option("No surface files to import."));
        var meshes = new List<SurfaceMesh>();
        var bytes = new List<byte[]>();
        foreach (string f in files)
        {
            if (!SurfaceMeshFile.IsSurfaceMesh(f)) throw new LabelVolumeException(AnatomyDiagnostics.Mesh($"'{Path.GetFileName(f)}' is not an STL, OBJ or PLY file."));
            meshes.Add(SurfaceMeshFile.Read(f));
            bytes.Add(File.ReadAllBytes(f));
        }
        var (order, depth, crossing) = SurfaceMeshFile.Nest(meshes);
        var used = StepImport.UsedNames(target);
        var known = tech?.ResolvedMaterials ?? [];
        var rows = new List<SurfaceImportRow>();
        foreach (int i in order)
        {
            string stem = Path.GetFileNameWithoutExtension(files[i]);
            var row = new SurfaceImportRow
            {
                SourcePath = Path.GetFullPath(files[i]), Name = StepImport.ProposeName(stem, $"tissue_{i + 1}", used),
                Check = SurfaceMeshFile.Check(meshes[i]), Depth = depth[i], Bytes = bytes[i], Hash = StepImport.HashOf(bytes[i]),
            };
            if (materials is not null && (materials.GetValueOrDefault(Path.GetFileName(files[i])) ?? materials.GetValueOrDefault(stem)) is { } m)
            {
                row.Material = m;
                row.Match = "chosen";
            }
            else if (known.FirstOrDefault(t => string.Equals(t.Name, stem, StringComparison.OrdinalIgnoreCase)) is { } byName)
            {
                row.Material = byName.Name;
                row.Match = "name";
            }
            rows.Add(row);
        }
        var notes = new List<string>();
        foreach (var (a, b) in crossing)
            notes.Add($"'{Path.GetFileName(files[a])}' and '{Path.GetFileName(files[b])}' cross each other rather than nest; where they overlap, the later one in the list wins.");
        foreach (var r in rows.Where(r => r.Flip && r.Fault is null))
            notes.Add($"'{Path.GetFileName(r.SourcePath)}' faces inward; it is turned round when read.");
        foreach (var r in rows.Where(r => !r.Check.SelfIntersectionTested))
            notes.Add($"'{Path.GetFileName(r.SourcePath)}' has {r.Check.Triangles:N0} triangles, too many to test for self-intersection; it was checked for closure only.");
        string group = StepImport.ProposeName(files.Count == 1 ? Path.GetFileNameWithoutExtension(files[0]) : "tissues", C3dGroups.DefaultStem, C3dGroups.Names(target));
        return new SurfaceImportPlan { Rows = rows, Unit = unit, Group = group, Notes = notes };
    }

    public static AnatomyImportResult ApplySurfaces(SurfaceImportPlan plan, C3dDocument doc, string c3dPath)
    {
        var faults = plan.Rows.Where(r => r.Fault is not null).ToList();
        if (faults.Count > 0)
            throw new LabelVolumeException(AnatomyDiagnostics.Mesh(string.Join(" ", faults.Select(r => $"'{Path.GetFileName(r.SourcePath)}' {r.Fault}.")) + " Nothing was imported."));
        var used = StepImport.UsedNames(doc);
        foreach (var r in plan.Rows)
        {
            if (NameValidator.Validate(r.Name) is { } bad) throw new LabelVolumeException(AnatomyDiagnostics.Name($"'{r.Name}' cannot be a name: {bad}"));
            if (used.Contains(r.Name)) throw new LabelVolumeException(AnatomyDiagnostics.Name($"'{r.Name}' is already a name in this document."));
        }
        if (plan.Rows.Count > 1 && !string.IsNullOrEmpty(plan.Group) && C3dGroups.NameRefusal(doc, plan.Group) is { } why)
            throw new LabelVolumeException(AnatomyDiagnostics.Name(why));
        string dir = Path.GetDirectoryName(Path.GetFullPath(c3dPath))!;
        var files = new List<(string, byte[], bool)>();
        var made = new List<C3dObject>();
        foreach (var r in plan.Rows)
        {
            var (copy, created) = StepImport.CopyInto(dir, Path.GetFileName(r.SourcePath), r.Bytes, r.Hash, write: true);
            files.Add((copy, r.Bytes, created));
            var mesh = new C3dMesh
            {
                // a tissue states εr and σ together, which on an ordinary object asks for a Role; a tissue surface is a dielectric
                Name = r.Name, Material = r.Material, Role = Em3dRole.Dielectric, File = Path.GetFileName(copy), Hash = r.Hash, Unit = plan.Unit,
                SourcePath = StepImport.SourceSpelling(r.SourcePath, c3dPath),
                Group = plan.Rows.Count > 1 && !string.IsNullOrEmpty(plan.Group) ? plan.Group : null,
            };
            doc.Objects.Add(mesh);
            made.Add(mesh);
        }
        var notes = new List<string>(plan.Notes);
        var none = plan.Rows.Where(r => r.Material is null).Select(r => r.Name).ToList();
        if (none.Count > 0)
            notes.Add($"{none.Count} surface{(none.Count == 1 ? " has" : "s have")} no material, so {(none.Count == 1 ? "it is" : "they are")} drawn and ignored by the solver until given one: {string.Join(", ", none)}.");
        return new AnatomyImportResult(made, files, notes);
    }
}
