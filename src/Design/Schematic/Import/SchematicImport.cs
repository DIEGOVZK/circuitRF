// Import Schematic's two functions — brief-schimp-1 R-si1-8/9, overview D1, D4, D10.
//
// The GUI command, the drop, the CLI noun and the MCP tool call ONE of these and hold no import logic of their own:
//   Import    — a new cell with its own schematic (and a cell per distinct child sheet);
//   BuildInto — the same build, handed back as a drawing for an open schematic's editor to add as one undo step.
//
// Both write the symbol cells into `<source-stem>_parts/` beside the target cell (D4): a cell whose .csym is
// byte-identical is reused, a same-named cell that differs is a refusal naming both. Every refusal is decided BEFORE
// anything is written, so a refused import writes nothing — and Import writes the target's .csch only after the whole
// build succeeded, never a partial cell.

using CircuitRF.Design.Cells;
using CircuitRF.Design.Symbol;

namespace CircuitRF.Design.Schematic.Import;

/// <summary>What <see cref="SchematicImport.Import"/> did (R-si1-8).</summary>
/// <param name="CellDir">The new cell, or null when refused.</param>
/// <param name="CreatedCells">Cell folders written beside it — symbol cells and child-sheet cells.</param>
/// <param name="ReusedCells">Symbol cells already there, byte-identical, and used as they are.</param>
/// <param name="Mapping">What each part became.</param>
/// <param name="Messages">The report, one sentence a line, the connectivity verdict among them.</param>
/// <param name="Refusal">Why nothing was written, or null.</param>
public sealed record SchematicImportResult(
    string? CellDir, string? SchematicPath, IReadOnlyList<string> CreatedCells, IReadOnlyList<string> ReusedCells,
    IReadOnlyList<ImportPartDecision> Mapping, ImportConnectivityResult? Connectivity, IReadOnlyList<string> Messages,
    string? Refusal)
{
    public bool Ok => Refusal is null;
}

/// <summary>What <see cref="SchematicImport.BuildInto"/> hands an open schematic's editor (R-si1-9): the drawing to add as
/// one undo step, renamed against what the schematic already holds, and the report.</summary>
/// <param name="Drawing">Components, wires, labels, dots and canvas objects to add; null when refused.</param>
/// <param name="GridSize">The connection grid the drawing's points need. Finer than the open schematic's, the editor must
/// adopt it, or points half a square apart would be joined.</param>
public sealed record SchematicImportEdit(
    SchematicEditModel? Drawing, SchematicMergeRenames? Renames, double GridSize, IReadOnlyList<string> CreatedCells,
    IReadOnlyList<string> ReusedCells, IReadOnlyList<ImportPartDecision> Mapping, ImportConnectivityResult? Connectivity,
    IReadOnlyList<string> Messages, string? Refusal)
{
    public bool Ok => Refusal is null;
}

public static class SchematicImport
{
    /// <summary>The folder beside a target cell that symbol and child-sheet cells go in (overview D4).</summary>
    public static string PartsFolderName(ForeignSchematic source) =>
        SchematicImportMap.CellNameFor(Path.GetFileNameWithoutExtension(source.SourcePath)) + "_parts";

    /// <summary>
    /// <paramref name="source"/> as a new cell named <paramref name="cellName"/> under <paramref name="parentDir"/>
    /// (R-si1-8). A name <see cref="NameValidator"/> refuses, or one already there, is a refusal naming it.
    /// </summary>
    public static SchematicImportResult Import(ForeignSchematic source, string parentDir, string cellName,
                                               SchematicImportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= new SchematicImportOptions();
        SchematicImportResult Refused(string why, IReadOnlyList<string>? messages = null, IReadOnlyList<ImportPartDecision>? mapping = null) =>
            new(null, null, [], [], mapping ?? [], null, messages ?? [], why);

        cellName = (cellName ?? "").Trim();
        if (NameValidator.Validate(cellName) is { } bad) return Refused($"'{cellName}' cannot be a cell name: {bad}");
        string parent = Path.GetFullPath(parentDir);
        string cellDir = Path.Combine(parent, cellName);
        if (Directory.Exists(cellDir) || File.Exists(cellDir))
            return Refused($"A cell named '{cellName}' already exists in {parent}; choose another name.");

        string partsDir = Path.Combine(parent, PartsFolderName(source));
        string schematicDir = CellFolder.SubFolderPath(cellDir, ViewType.Schematic);
        var built = SchematicImportBuild.Build(source, cellName, schematicDir, partsDir, options);
        if (!built.Ok) return Refused(string.Join(" ", built.Refusals), built.Notes, built.Mapping);

        var plan = Plan(built, partsDir);
        if (plan.Refusal is { } refusal) return Refused(refusal, built.Notes, built.Mapping);

        var top = built.Top!;
        top.Model.SchematicImportSource = new SchematicImportProvenance
        {
            Format = source.Format,
            Source = Path.GetFileName(source.SourcePath),
            ImportedUtc = (options.Now ?? DateTime.UtcNow).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
        };

        string schematicPath;
        try
        {
            WriteCells(built, partsDir, plan);
            schematicPath = CellCreate.Create(parent, cellName, CellViews.Schematic, top.Model).SchematicPath!;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Refused($"The import could not be written: {ex.Message}", built.Notes, built.Mapping);
        }
        top.Model.SchematicDirectory = schematicDir;

        var connectivity = ImportConnectivity.Compare(source, built);
        var messages = Report(built, plan, connectivity);
        messages.Insert(0, $"Imported {Path.GetFileName(source.SourcePath)} as {cellName}: {schematicPath}.");
        return new SchematicImportResult(cellDir, schematicPath, plan.Created, plan.Reused, built.Mapping, connectivity, messages, null);
    }

    /// <summary>
    /// <paramref name="source"/> built for the open schematic <paramref name="into"/> (R-si1-9): its bounding box's top-left
    /// at <paramref name="topLeft"/> snapped to the grid, analyses dropped, every instance, net-label and variable name
    /// <paramref name="into"/> already has renamed. Symbol cells are written beside <paramref name="into"/>'s cell, which
    /// is why an unsaved schematic is refused. <paramref name="into"/> itself is not changed.
    /// </summary>
    public static SchematicImportEdit BuildInto(ForeignSchematic source, SchematicEditModel into, (double X, double Y) topLeft,
                                                SchematicImportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(into);
        options ??= new SchematicImportOptions();
        SchematicImportEdit Refused(string why, IReadOnlyList<string>? messages = null, IReadOnlyList<ImportPartDecision>? mapping = null) =>
            new(null, null, into.GridSize, [], [], mapping ?? [], null, messages ?? [], why);

        if (into.SchematicDirectory is not { Length: > 0 } schematicDir)
            return Refused("Save the schematic first: building a design into it writes its parts as cells beside it.");
        schematicDir = Path.GetFullPath(schematicDir);
        string parent = File.Exists(Path.Combine(Path.GetDirectoryName(schematicDir) ?? schematicDir, CellFolder.CcellFileName))
            ? Path.GetDirectoryName(schematicDir)!
            : schematicDir;
        string partsDir = Path.Combine(Path.GetDirectoryName(parent) ?? parent, PartsFolderName(source));

        var built = SchematicImportBuild.Build(source, Path.GetFileName(parent), schematicDir, partsDir, options);
        if (!built.Ok) return Refused(string.Join(" ", built.Refusals), built.Notes, built.Mapping);
        var plan = Plan(built, partsDir);
        if (plan.Refusal is { } refusal) return Refused(refusal, built.Notes, built.Mapping);

        try { WriteCells(built, partsDir, plan); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Refused($"The parts could not be written: {ex.Message}", built.Notes, built.Mapping);
        }

        var connectivity = ImportConnectivity.Compare(source, built);
        var model = built.Top!.Model;

        // Where it goes: the drawing's top-left on the point, by whole grid squares, so its own grid is kept.
        var (minX, minY) = TopLeftOf(model);
        double step = NetlistSchematic.Grid;
        Shift(model, Math.Round((topLeft.X - minX) / step) * step, Math.Round((topLeft.Y - minY) / step) * step);
        model.Analyses.Clear();
        var renames = SchematicMerge.RenameAgainst(model, into, instances: true);

        var messages = Report(built, plan, connectivity);
        if (renames.Instances.Count + renames.Nets.Count + renames.Variables.Count > 0)
            messages.Add("Renamed, because the schematic already has them: "
                       + string.Join(", ", renames.Instances.Concat(renames.Nets).Concat(renames.Variables).Select(kv => $"{kv.Key} → {kv.Value}")) + ".");
        if (model.GridSize < into.GridSize)
            messages.Add($"The design is drawn on a {model.GridSize:0}-unit grid; the schematic's connection grid becomes {model.GridSize:0} "
                       + "so its points connect where the source's do.");
        return new SchematicImportEdit(model, renames, Math.Min(model.GridSize, into.GridSize), plan.Created, plan.Reused, built.Mapping,
                                       connectivity, messages, null);
    }

    // ── the cells ───────────────────────────────────────────────────────────────────────────────

    private sealed record CellPlan(List<string> Created, List<string> Reused, HashSet<string> Skip, string? Refusal);

    /// <summary>Which cells are new and which already there (overview D4) — decided before anything is written.</summary>
    private static CellPlan Plan(ImportBuildResult built, string partsDir)
    {
        var created = new List<string>();
        var reused = new List<string>();
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cell in built.SymbolCells)
        {
            string dir = Path.Combine(partsDir, cell.Name);
            if (!Directory.Exists(dir)) { created.Add(dir); continue; }
            string? existing = Directory.Exists(CellFolder.SubFolderPath(dir, ViewType.Symbol))
                ? Directory.EnumerateFiles(CellFolder.SubFolderPath(dir, ViewType.Symbol), "*.csym").Order(StringComparer.Ordinal).FirstOrDefault()
                : null;
            if (existing is null || File.ReadAllText(existing) != cell.Csym)
                return new CellPlan(created, reused, skip,
                    $"The cell {dir} already exists and is not the symbol '{cell.SourceName}' this design draws; "
                  + "move or rename it, or import into another folder.");
            reused.Add(dir);
            skip.Add(cell.Name);
        }
        foreach (var cell in built.SheetCells)
        {
            string dir = Path.Combine(partsDir, cell.Name);
            if (Directory.Exists(dir))
                return new CellPlan(created, reused, skip,
                    $"The cell {dir} already exists, and the design's sheet '{cell.SheetName}' would be written there; "
                  + "move or rename it, or import into another folder.");
            created.Add(dir);
        }
        return new CellPlan(created, reused, skip, null);
    }

    private static void WriteCells(ImportBuildResult built, string partsDir, CellPlan plan)
    {
        if (built.SymbolCells.Count + built.SheetCells.Count > 0) Directory.CreateDirectory(partsDir);
        foreach (var cell in built.SymbolCells.Where(c => !plan.Skip.Contains(c.Name)))
            WriteSymbolOnlyCell(partsDir, cell.Name, cell.Symbol);
        foreach (var cell in built.SheetCells)
        {
            string dir = WriteSymbolOnlyCell(partsDir, cell.Name, cell.Symbol);
            var schematic = built.Schematics.First(s => s.SheetCell == cell);
            CellCreate.WriteSchematicView(dir, cell.Name, cell.Name, schematic.Model);
            schematic.Model.SchematicDirectory = CellFolder.SubFolderPath(dir, ViewType.Schematic);
        }
    }

    /// <summary>A cell with one view, its symbol — Import Component's own layout of one (R-si1-4).</summary>
    private static string WriteSymbolOnlyCell(string partsDir, string name, CircuitRF.Design.Symbol.Symbol symbol)
    {
        string dir = CellFolder.CreateCellFolder(partsDir, name);
        string file = name + ".csym";
        SymbolPersistence.SaveToFile(Path.Combine(CellFolder.SubFolderPath(dir, ViewType.Symbol), file), symbol);
        string ccellPath = Path.Combine(dir, CellFolder.CcellFileName);
        var ccell = CellPersistence.LoadFromFile(ccellPath);
        ccell.PrimarySymbol = file;
        ccell.NumPorts = symbol.PortCount;
        CellPersistence.SaveToFile(ccellPath, ccell);
        CellSymbolResolver.Invalidate(dir);
        return dir;
    }

    // ── the report ──────────────────────────────────────────────────────────────────────────────

    private static List<string> Report(ImportBuildResult built, CellPlan plan, ImportConnectivityResult connectivity)
    {
        int native = built.Mapping.Count(d => d.Becomes == ImportPartKind.Native);
        int cells = built.Mapping.Count(d => d.Becomes == ImportPartKind.SymbolCell);
        var lines = new List<string>
        {
            $"{built.Mapping.Count(d => d.Becomes != ImportPartKind.Ground)} part(s): {native} native, {cells} as symbol cells "
          + $"({plan.Created.Count} cell(s) written, {plan.Reused.Count} reused).",
        };
        if (cells > 0)
            lines.Add("A symbol cell has no model: a run refuses it until it is given one (a schematic, an S-parameter file or a model).");
        lines.AddRange(built.Notes);
        lines.Add(connectivity.Summary);
        lines.AddRange(connectivity.Geometric.Differences.Select(d => $"Against the drawing: {d}."));
        lines.AddRange(connectivity.Declared?.Differences.Select(d => $"Against the declared nets: {d}.") ?? []);
        if (connectivity.SourceAgreement is { Matched: false } self)
            lines.AddRange(self.Differences.Select(d => $"The source disagrees with itself — its declared nets against its drawing: {d}."));
        return lines;
    }

    // ── moving a drawing ────────────────────────────────────────────────────────────────────────

    private static (double X, double Y) TopLeftOf(SchematicEditModel m)
    {
        var xs = m.Components.Select(c => c.X).Concat(m.Wires.SelectMany(w => w.Points.Select(p => p.X)))
                  .Concat(m.NetLabels.Select(l => l.X)).Concat(m.CanvasObjects.Select(o => o.X)).DefaultIfEmpty(0);
        var ys = m.Components.Select(c => c.Y).Concat(m.Wires.SelectMany(w => w.Points.Select(p => p.Y)))
                  .Concat(m.NetLabels.Select(l => l.Y)).Concat(m.CanvasObjects.Select(o => o.Y)).DefaultIfEmpty(0);
        return (xs.Min(), ys.Min());
    }

    private static void Shift(SchematicEditModel m, double dx, double dy)
    {
        foreach (var c in m.Components) { c.X += dx; c.Y += dy; }
        foreach (var w in m.Wires)
            for (int i = 0; i < w.Points.Count; i++) w.Points[i] = (w.Points[i].X + dx, w.Points[i].Y + dy);
        foreach (var l in m.NetLabels) { l.X += dx; l.Y += dy; }
        foreach (var d in m.Dots) { d.X += dx; d.Y += dy; }
        foreach (var o in m.CanvasObjects)
        {
            o.X += dx; o.Y += dy;
            if (o is EditablePrimitive p) { p.P1X += dx; p.P1Y += dy; p.P2X += dx; p.P2Y += dy; }
        }
    }
}
