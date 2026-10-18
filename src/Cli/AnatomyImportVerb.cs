// brief-sar-11 — `import voxels` and `import surfaces`: anatomy into a .c3d, headless. Argument parsing, refusals and reporting
// only — every decision is AnatomyImport's (src/Design), the pair of functions the 3D editor's Import ▸ Voxel Model… and
// Surface Meshes… call, so a model imported here and one imported in the GUI are the same object. What the dialog would ask
// is a flag: a raw volume's layout (--dims, --type, --voxel), a tetrahedral mesh's voxel size (--voxel), a label's material
// (--material label=name, or =air), a surface's material (--material stem=name).
//
// LENGTHS CARRY A UNIT. --voxel 2.5mm, --grid-step 5mm: a bare number could be millimetres or metres and both are plausible
// for anatomy, so it is refused, as `render` refuses a bare layout coordinate.
//
// THE .c3d IS CREATED when it does not exist (an empty 3D view), and saved after the import; --report reads, plans and
// prints the size report and the label table, and writes nothing.

using System.Globalization;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.ThreeD.Import;

namespace CircuitRF.Cli;

internal static class AnatomyImportVerb
{
    public const string UsageText =
        "       circuitrf import voxels <file.nii[.gz]|.h5|.raw|.node|.ele> --into <x.c3d> [--labels <list.csv>] [--material L=name|air]... "
      + "[--name N] [--dims nx,ny,nz --type uint8|int16|… --voxel <len>[,<len>,<len>] [--big-endian] [--header N]] "
      + "[--voxel <len> --unit mm (a tetrahedral mesh)] [--crop i0,j0,k0,i1,j1,k1] [--grid-step <len>] [--report]\n"
      + "       circuitrf import surfaces <a.stl> [b.obj …] | <folder> --into <x.c3d> [--unit mm] [--material stem=name]... [--group G] [--report]";

    // ── voxels ─────────────────────────────────────────────────────────────────────────────────

    public static int RunVoxels(string[] args)
    {
        const string noun = "voxels";
        string? source = null, into = null, labels = null, name = null, type = null, unit = null;
        int[]? dims = null, crop = null;
        double[]? voxel = null;
        double gridStep = 0;
        bool bigEndian = false, report = false;
        long header = 0;
        var materials = new Dictionary<int, string>();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (a)
            {
                case "--into": into = Next(); break;
                case "--labels": labels = Next(); break;
                case "--name": name = Next(); break;
                case "--type": type = Next(); break;
                case "--unit": unit = Next(); break;
                case "--big-endian": bigEndian = true; break;
                case "--report": report = true; break;
                case "--header":
                    if (!long.TryParse(Next(), NumberStyles.Integer, CultureInfo.InvariantCulture, out header) || header < 0)
                        return Bad(noun, "--header takes a byte count.");
                    break;
                case "--dims":
                    if ((dims = Ints(Next(), 3)) is null) return Bad(noun, "--dims takes three voxel counts: nx,ny,nz.");
                    break;
                case "--crop":
                    if ((crop = Ints(Next(), 6)) is null) return Bad(noun, "--crop takes six voxel indices: i0,j0,k0,i1,j1,k1 (upper ends exclusive).");
                    break;
                case "--voxel":
                {
                    string? t = Next();
                    var parts = t?.Split(',') ?? [];
                    var lens = parts.Select(Length).ToArray();
                    if (parts.Length is not (1 or 3) || lens.Any(l => l is not > 0))
                        return Bad(noun, $"--voxel takes a length with its unit (2.5mm), or three (1mm,1mm,2mm); '{t}' is not one.");
                    voxel = parts.Length == 1 ? [lens[0]!.Value, lens[0]!.Value, lens[0]!.Value] : [.. lens.Select(l => l!.Value)];
                    break;
                }
                case "--grid-step":
                {
                    string? t = Next();
                    if (Length(t ?? "") is not { } g || !(g > 0)) return Bad(noun, $"--grid-step takes a length with its unit (5mm); '{t}' is not one.");
                    gridStep = g;
                    break;
                }
                case "--material":
                {
                    string? t = Next();
                    int eq = t?.IndexOf('=') ?? -1;
                    if (eq <= 0 || !int.TryParse(t![..eq], NumberStyles.Integer, CultureInfo.InvariantCulture, out int label))
                        return Bad(noun, $"--material takes label=material (or label=air); '{t}' is not one.");
                    materials[label] = t[(eq + 1)..];
                    break;
                }
                default:
                    if (a.StartsWith('-')) { JsonRun.Report(CliDiagnostics.ImportUnknownOption(a)); Console.Error.WriteLine("Usage:\n" + UsageText); return 1; }
                    if (source is not null) return Bad(noun, "one volume, please.");
                    source = a;
                    break;
            }
        }
        if (source is null) return Bad(noun, "a volume file is required.");
        JsonRun.InputPath = source;
        if (!File.Exists(source)) return JsonRun.Fail(CliDiagnostics.ImportSourceNotFound(source));
        if (into is null) return Bad(noun, "--into <x.c3d> says which 3D view the body goes into.");
        if (!string.Equals(Path.GetExtension(into), C3dPersistence.Extension, StringComparison.OrdinalIgnoreCase))
            return Bad(noun, $"--into names a .c3d; '{into}' is not one.");

        RawVolumeLayout? raw = null;
        bool tet = TetMesh.IsTetMesh(source);
        if (!tet && C3dVoxelBodies.FormatOf(source) == "raw")
        {
            if (dims is null || type is null || voxel is null)
                return Bad(noun, $"'{Path.GetFileName(source)}' is a raw volume: --dims, --type and --voxel say what its bytes are.");
            if (RawVolumeLayout.ParseType(type) is not { } t) return Bad(noun, $"--type '{type}' is not one of uint8, int8, uint16, int16, uint32, int32.");
            raw = new RawVolumeLayout(dims[0], dims[1], dims[2], t, voxel[0], voxel[1], voxel[2], bigEndian, header);
        }
        if (tet && voxel is null) return Bad(noun, "a tetrahedral mesh is voxelised: --voxel says at what size (2mm).");
        double tetUnit = 1e-3;
        if (unit is not null)
        {
            if (C3dMeshes.UnitMetres(unit) is not { } u) return Bad(noun, $"--unit '{unit}' is not m, cm, mm, um, in or mil.");
            tetUnit = u;
        }

        string c3d = Path.GetFullPath(into);
        var doc = File.Exists(c3d) ? C3dPersistence.LoadFromFile(c3d) : new C3dDocument();
        var tech = TechnologyFor(doc, c3d);
        long Dbu(double m) => (long)Math.Round(m * 1e6 * doc.DbuPerMicron);
        var options = new VoxelImportOptions
        {
            LabelsCsv = labels, Raw = raw, TetVoxelM = tet ? voxel![0] : 0, TetUnitM = tetUnit, Name = name,
            Crop = crop is null ? null : new C3dVoxelRange { I0 = crop[0], J0 = crop[1], K0 = crop[2], I1 = crop[3], J1 = crop[4], K1 = crop[5] },
            GridStep = Dbu(gridStep), Materials = materials, AllowUnmapped = report,
        };
        VoxelImportPlan plan;
        try { plan = AnatomyImport.PlanVoxels(source, options, tech, doc, doc.DbuPerMicron); }
        catch (LabelVolumeException e) { return JsonRun.Fail(e.Diagnostic); }
        catch (Exception e) when (e is RfCore.Hdf5.OpenEmsHdf5Exception or IOException)
        {
            return JsonRun.Fail(CliDiagnostics.ImportRefused(e.Message));
        }
        foreach (string n in plan.Notes) Note(n);
        foreach (string line in plan.Report.Lines(plan.Rows)) Console.Error.WriteLine($"[circuitRF] {line}");
        if (plan.Report.Warning is { } w) JsonRun.Note(CliDiagnostics.ImportMessage(w));
        if (report)
        {
            if (plan.Unmapped is { Count: > 0 } unmapped)
                Note(C3dVoxelBodies.UnmappedRefusal(plan.Name, unmapped));
            return 0;
        }

        AnatomyImportResult result;
        try
        {
            result = AnatomyImport.ApplyVoxels(plan, doc, c3d);
            Directory.CreateDirectory(Path.GetDirectoryName(c3d)!);
            C3dPersistence.SaveToFile(c3d, doc);
        }
        catch (LabelVolumeException e) { return JsonRun.Fail(e.Diagnostic); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return JsonRun.Fail(CliDiagnostics.ImportRefused(e.Message)); }
        foreach (string n in result.Notes.Except(plan.Notes)) Note(n);
        var (file, _, created) = result.Files[0];
        Console.Error.WriteLine($"[circuitRF] added the voxel body '{result.Objects[0].Name}' to {c3d}" +
                                (created ? $" and copied the volume to {file}" : $"; {file} was already there"));
        Console.WriteLine(c3d);
        JsonRun.AddOutput("3d", c3d);
        JsonRun.AddOutput("voxels", file);
        return 0;
    }

    // ── surfaces ───────────────────────────────────────────────────────────────────────────────

    public static int RunSurfaces(string[] args)
    {
        const string noun = "surfaces";
        var files = new List<string>();
        string? into = null, group = null;
        string unit = "mm";
        bool report = false;
        var materials = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (a)
            {
                case "--into": into = Next(); break;
                case "--group": group = Next(); break;
                case "--unit": unit = Next() ?? ""; break;
                case "--report": report = true; break;
                case "--material":
                {
                    string? t = Next();
                    int eq = t?.IndexOf('=') ?? -1;
                    if (eq <= 0) return Bad(noun, $"--material takes file=material (the file's name or stem); '{t}' is not one.");
                    materials[t![..eq]] = t[(eq + 1)..];
                    break;
                }
                default:
                    if (a.StartsWith('-')) { JsonRun.Report(CliDiagnostics.ImportUnknownOption(a)); Console.Error.WriteLine("Usage:\n" + UsageText); return 1; }
                    files.Add(a);
                    break;
            }
        }
        if (files.Count == 0) return Bad(noun, "at least one surface file (STL, OBJ, PLY), or a folder of them, is required.");
        JsonRun.InputPath = files[0];
        foreach (string f in files)
            if (!File.Exists(f) && !Directory.Exists(f)) return JsonRun.Fail(CliDiagnostics.ImportSourceNotFound(f));
        // a folder is every surface file in it, one tissue each — how a segmentation's per-tissue export usually arrives
        files = [.. files.SelectMany(f => Directory.Exists(f)
            ? Directory.GetFiles(f).Where(SurfaceMeshFile.IsSurfaceMesh).OrderBy(x => x, StringComparer.Ordinal)
            : (IEnumerable<string>)[f])];
        if (files.Count == 0) return Bad(noun, "the folder holds no STL, OBJ or PLY file.");
        if (into is null) return Bad(noun, "--into <x.c3d> says which 3D view the surfaces go into.");
        if (!string.Equals(Path.GetExtension(into), C3dPersistence.Extension, StringComparison.OrdinalIgnoreCase))
            return Bad(noun, $"--into names a .c3d; '{into}' is not one.");

        string c3d = Path.GetFullPath(into);
        var doc = File.Exists(c3d) ? C3dPersistence.LoadFromFile(c3d) : new C3dDocument();
        SurfaceImportPlan plan;
        try { plan = AnatomyImport.PlanSurfaces(files, unit, materials, TechnologyFor(doc, c3d), doc); }
        catch (LabelVolumeException e) { return JsonRun.Fail(e.Diagnostic); }
        catch (IOException e) { return JsonRun.Fail(CliDiagnostics.ImportRefused(e.Message)); }
        if (group is not null) plan.Group = group;
        foreach (var r in plan.Rows)
            Console.Error.WriteLine($"[circuitRF] {r.Name,-20} {(r.Material ?? "(no material)"),-32} {r.Check.Triangles,9:N0} triangles  " +
                                    (r.Fault is { } f ? f : "closed") + (r.Depth > 0 ? $"  inside {r.Depth}" : ""));
        foreach (string n in plan.Notes) Note(n);
        if (report) return plan.Rows.Any(r => r.Fault is not null) ? 1 : 0;

        AnatomyImportResult result;
        try
        {
            result = AnatomyImport.ApplySurfaces(plan, doc, c3d);
            Directory.CreateDirectory(Path.GetDirectoryName(c3d)!);
            C3dPersistence.SaveToFile(c3d, doc);
        }
        catch (LabelVolumeException e) { return JsonRun.Fail(e.Diagnostic); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return JsonRun.Fail(CliDiagnostics.ImportRefused(e.Message)); }
        foreach (string n in result.Notes.Except(plan.Notes)) Note(n);
        Console.Error.WriteLine($"[circuitRF] added {result.Objects.Count} mesh object(s) to {c3d}");
        Console.WriteLine(c3d);
        JsonRun.AddOutput("3d", c3d);
        foreach (var (path, _, _) in result.Files) JsonRun.AddOutput("mesh", path);
        return 0;
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────────

    private static Technology? TechnologyFor(C3dDocument doc, string c3d)
    {
        var (res, _) = TechnologyResolver.ResolveForDocument(doc.TechRef, c3d, null, new TechnologyCache());
        if (res.ResolvedPath is { } p) Console.Error.WriteLine($"[circuitRF] technology: {p}");
        return res.Tech;
    }

    private static void Note(string text)
    {
        Console.Error.WriteLine($"[circuitRF] {text}");
        JsonRun.Note(CliDiagnostics.ImportMessage(text));
    }

    private static int Bad(string noun, string why)
    {
        JsonRun.Report(CliDiagnostics.ImportAnatomyArgs(noun, why));
        Console.Error.WriteLine("Usage:\n" + UsageText);
        return 1;
    }

    private static int[]? Ints(string? text, int count)
    {
        var parts = text?.Split(',') ?? [];
        if (parts.Length != count) return null;
        var v = new int[count];
        for (int k = 0; k < count; k++)
            if (!int.TryParse(parts[k], NumberStyles.Integer, CultureInfo.InvariantCulture, out v[k])) return null;
        return v;
    }

    /// <summary>A length with its unit, metres; null for a bare number or an unknown unit.</summary>
    internal static double? Length(string text)
    {
        text = text.Trim();
        int at = 0;
        while (at < text.Length && (char.IsDigit(text[at]) || text[at] is '.' or '-' or '+' or 'e' or 'E') &&
               !(text[at] is 'e' or 'E' && (at + 1 >= text.Length || !(char.IsDigit(text[at + 1]) || text[at + 1] is '-' or '+'))))
            at++;
        if (at == 0 || at == text.Length) return null;
        if (!double.TryParse(text[..at], NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return null;
        return C3dMeshes.UnitMetres(text[at..]) is { } k ? v * k : null;
    }
}
