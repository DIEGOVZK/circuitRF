// Create Schematic from Image, for a schematic picture — brief-img-10-schematic-emit.md R-im10-1, R-im10-4 … R-im10-6,
// R-im10-10 (overview D4, D5, D12, D13, D14, D16).
//
//   picture ─ IM-7 SchematicImageReading.Read ─ IM-8 SchematicSymbols.Read ─ IM-9 ImageText.Read + LabelAssociation
//           ─ SchematicImageEmit.Build (the TestBench, the parts table, the exact hints)
//           ─ NetlistSchematic.Build — with the exact hints (As drawn) or without them (Tidy) — the one schematic writer
//
// Mirrors ArtworkRecognition.Circuit / Run: Circuit writes nothing (the CLI's read-only default, the dialog's preview),
// Run writes a cell — the schematic, the picture kept beside it and laid under it — and IntoSchematic makes the drawing
// a placed bitmap's schematic takes over the bitmap, for the GUI to add as one undo step (D14). The GUI, the CLI and the
// MCP tool call these and nothing else (D2). A refusal at any step before the write writes nothing.

using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Recognition;
using CircuitRF.Design.Revision;
using CircuitRF.Engine;

namespace CircuitRF.Design.Schematic.Recognition;

/// <summary><i>Arrange</i>: the picture's own arrangement (D12), or the automatic one.</summary>
public enum SchematicArrange { AsDrawn, Tidy }

/// <summary>What a schematic-picture run reads — the dialog's fields and the CLI's flags, one for one.</summary>
public sealed record SchematicImageInput
{
    public required ImageSource Source { get; init; }

    /// <summary>What the picture was read as (IM-2); null: it is classified. Recorded in the provenance.</summary>
    public ImageKindResult? Kind { get; init; }

    /// <summary>The reading's options, the user's corrections among them (R-im10-8).</summary>
    public SchematicImageOptions Options { get; init; } = new();

    /// <summary>The technology a physical line's εeff comes from and the schematic names, or null (D8).</summary>
    public Technology? Technology { get; init; }
    public string? TechnologyPath { get; init; }

    /// <summary>An edited parts table (R-im10-7): text wins over a file.</summary>
    public string? PartsCsvPath { get; init; }
    public string? PartsCsvText { get; init; }

    public SchematicArrange Arrange { get; init; } = SchematicArrange.AsDrawn;

    /// <summary>D5's setting: the picture under the result.</summary>
    public bool KeepUnderlay { get; init; } = true;

    /// <summary>The symbol classifier and the text reader; null: the template matcher and the stroke reader.</summary>
    public IImageSymbolClassifier? Classifier { get; init; }
    public IImageTextReader? TextReader { get; init; }
}

/// <summary>What a reading produced, written nowhere.</summary>
/// <param name="Report">IM-7's, IM-8's, IM-9's and the emit's findings, in that order (R-im10-10).</param>
public sealed record SchematicImageResult(
    SchematicImageRead? Read, SchematicSymbolsRead? Symbols, IReadOnlyList<ImageWord> Words, ImageTextLabels? Labels,
    SchematicImageCircuit? Circuit, RecognitionReport Report, string? Refusal)
{
    public bool Ok => Refusal is null && Circuit is not null;

    /// <summary>The parts table, pixel rows; empty before the emit.</summary>
    public PartsTable Parts => Circuit?.Parts ?? PartsTable.Empty with { Pixels = true };
}

public enum SchematicImageTargetKind { NewCell, Replace }

/// <summary>Where a new schematic goes (D14).</summary>
public sealed record SchematicImageTarget(SchematicImageTargetKind Kind, string? ParentDir = null, string? CellName = null,
                                          string? CellDir = null)
{
    /// <summary>The checkpoint's intent before a replace.</summary>
    public const string ReplaceIntent = "Create Schematic from Image";

    public static SchematicImageTarget NewCell(string parentDir, string name) => new(SchematicImageTargetKind.NewCell, parentDir, name);

    public static SchematicImageTarget Replace(string cellDir) => new(SchematicImageTargetKind.Replace, CellDir: cellDir);

    /// <summary>Whether <paramref name="cellDir"/>'s schematic was made from a schematic picture by this command — it
    /// carries an <c>ImageSource</c> block of kind schematic — and so may be replaced.</summary>
    public static bool IsReplaceable(string cellDir)
    {
        if (RecognitionTarget.PrimarySchematic(cellDir) is not { } csch) return false;
        try { return SchematicPersistence.LoadFromFile(csch).model.ImageSource is { Kind: "schematic" }; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            return false;
        }
    }
}

public sealed record SchematicImageRunOptions
{
    public RecognitionEmitOptions Emit { get; init; } = new();

    /// <summary>The checkpoint a replace takes first; null is the real one (<see cref="WorkspaceCheckpoints.BeforeWrite"/>).</summary>
    public Func<string, string, string?>? Checkpoint { get; init; }

    /// <summary>The time the provenance records; null is now.</summary>
    public DateTime? Now { get; init; }
}

/// <summary>What one run did.</summary>
public sealed record SchematicImageRun(SchematicImageResult Reading, SchematicEditModel? Schematic, string? SchematicPath,
                                       string? CellDir, string? Refusal)
{
    public bool Ok => Refusal is null && SchematicPath is not null;
    public RecognitionReport Report => Reading.Report;
    public bool CheckpointTaken { get; init; }
}

/// <summary>The drawing made over a placed bitmap, for the editor to add as one undo step (D14).</summary>
/// <param name="Drawing">The components, wires, net labels, tuning entries and analysis to add — at the bitmap's
/// frame, with no underlay (the bitmap is the picture).</param>
/// <param name="RenamedVariables">Each variable the schematic already had, and the name the new one took.</param>
/// <param name="RenamedNets">Each net name the schematic already had, and the name the new net took.</param>
public sealed record SchematicImageEdit(SchematicImageResult Reading, SchematicEditModel? Drawing,
                                        IReadOnlyDictionary<string, string> RenamedVariables,
                                        IReadOnlyDictionary<string, string> RenamedNets, string? Refusal)
{
    public bool Ok => Refusal is null && Drawing is not null;
    public RecognitionReport Report => Reading.Report;
}

public static class SchematicImageRecognition
{
    /// <summary>Read and emit, and write nothing (R-im10-1).</summary>
    public static SchematicImageResult Circuit(SchematicImageInput input, RecognitionEmitOptions? emit = null, RunControl? control = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        return Read(input, emit ?? new RecognitionEmitOptions(), null, control);
    }

    /// <summary>R-im10-1 — read → emit → draw → write: a cell holding the schematic, with the picture kept beside it and,
    /// As drawn, under it.</summary>
    public static SchematicImageRun Run(SchematicImageInput input, SchematicImageTarget target, SchematicImageRunOptions? options = null,
                                        RunControl? control = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(target);
        options ??= new SchematicImageRunOptions();
        var empty = new SchematicImageResult(null, null, [], null, null, new RecognitionReport(), null);
        SchematicImageRun Before(string why) => new(empty, null, null, null, why);

        // ── where it goes ────────────────────────────────────────────────────────────────────────────
        string cellDir, cellName;
        string? existing = null;
        if (target.Kind == SchematicImageTargetKind.NewCell)
        {
            cellName = (target.CellName ?? "").Trim();
            if (NameValidator.Validate(cellName) is { } bad) return Before($"'{cellName}' cannot be a cell name: {bad}");
            string parent = Path.GetFullPath(target.ParentDir ?? ".");
            cellDir = Path.Combine(parent, cellName);
            if (Directory.Exists(cellDir) || File.Exists(cellDir))
                return Before($"A cell named '{cellName}' already exists in {parent}; choose another name.");
        }
        else
        {
            cellDir = Path.GetFullPath(target.CellDir ?? "");
            cellName = Path.GetFileName(cellDir);
            if (!SchematicImageTarget.IsReplaceable(cellDir))
                return Before($"{cellName}'s schematic was not created from a schematic picture; choose a new cell.");
            existing = RecognitionTarget.PrimarySchematic(cellDir);
        }
        string schematicDir = existing is not null ? Path.GetDirectoryName(existing)! : CellFolder.SubFolderPath(cellDir, ViewType.Schematic);

        // ── the reading, and its drawing ─────────────────────────────────────────────────────────────
        var reading = Read(input, options.Emit, null, control);
        SchematicImageRun Refused(string why) => new(reading, null, null, null, why);
        if (!reading.Ok) return Refused(reading.Refusal ?? "The picture could not be read.");
        var report = reading.Report;
        var now = options.Now ?? DateTime.UtcNow;
        if (Draw(reading, input, schematicDir) is not { } model)
            return Refused("The recognised circuit cannot be drawn: " + report.Of(RecognitionFindingClass.DrawingNotes)?.Sentence);
        model.TechRef = input.TechnologyPath is { } tech ? SchematicTechnology.StoredRef(Path.GetFullPath(tech), schematicDir) : null;

        // ── the write: one checkpoint before a replace, the picture kept, the schematic over it ───────
        bool checkpoint = existing is not null;
        string path;
        try
        {
            if (checkpoint)
            {
                var take = options.Checkpoint
                           ?? ((p, intent) => WorkspaceCheckpoints.BeforeWrite(p, intent, CheckpointOrigin.SavePoint)?.Render());
                if (take(existing!, SchematicImageTarget.ReplaceIntent) is { } failed)
                    return Refused($"The history checkpoint a replace takes first could not be taken, so nothing was replaced: {failed}");
            }
            else CellFolder.CreateCellFolder(Path.GetDirectoryName(cellDir)!, cellName);

            string pictureRef = ImageKeep.Into(cellDir, cellName, input.Source).RefFrom(schematicDir);
            if (Underlay(reading, input, model, pictureRef) is { } underlay) model.CanvasObjects.Add(underlay);
            model.ImageSource = Provenance(input, pictureRef, now);

            if (existing is not null)
            {
                path = existing;
                SchematicPersistence.SaveToFile(path, model, cellName);
            }
            else path = CellCreate.WriteSchematicView(cellDir, cellName, cellName, model);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Refused($"The schematic could not be written: {ex.Message}");
        }

        report.Add(RecognitionFindingClass.SchematicWritten, 1,
            (checkpoint ? $"A history checkpoint (\"{SchematicImageTarget.ReplaceIntent}\") was taken, then " : "") +
            $"{cellName}'s schematic was {(checkpoint ? "replaced" : "written")}: {path}.");
        return new SchematicImageRun(reading, model, path, cellDir, null) { CheckpointTaken = checkpoint };
    }

    /// <summary>
    /// D14 — the drawing a placed bitmap's schematic takes, over the bitmap: at the frame its rect defines, with no
    /// second underlay, every variable or net name <paramref name="into"/> already has given a suffix. Nothing is
    /// changed here; the editor adds <see cref="SchematicImageEdit.Drawing"/> through its own commands.
    /// </summary>
    public static SchematicImageEdit IntoSchematic(SchematicImageInput input, SchematicEditModel into, SchematicBitmapPlacement placed,
                                                   RecognitionEmitOptions? emit = null, RunControl? control = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(into);
        ArgumentNullException.ThrowIfNull(placed);
        var frame = SchematicFrame.Of(placed, input.Source.Raster.Width);
        var reading = Read(input, emit ?? new RecognitionEmitOptions(), frame, control);
        var none = new Dictionary<string, string>();
        if (!reading.Ok) return new SchematicImageEdit(reading, null, none, none, reading.Refusal ?? "The picture could not be read.");
        string? dir = Path.GetDirectoryName(placed.Document);
        if (Draw(reading, input, dir) is not { } model)
            return new SchematicImageEdit(reading, null, none, none,
                "The recognised circuit cannot be drawn: " + reading.Report.Of(RecognitionFindingClass.DrawingNotes)?.Sentence);
        if (placed.RotationDeg != 0)
            reading.Report.Add(RecognitionFindingClass.PictureUnderlay, 1,
                "The picture is turned on the sheet; the drawing is laid over its unturned rectangle.");

        // Names the schematic already has take a suffix, so nothing new joins anything there.
        var haveVariables = into.Components.Where(c => c.Symbol == SymbolKind.Var).SelectMany(c => c.Parameters).Select(p => p.Name)
                                .Concat(into.Tuning?.Variables.Select(v => v.Key) ?? []).ToHashSet(StringComparer.Ordinal);
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var v in model.Components.Where(c => c.Symbol == SymbolKind.Var).SelectMany(c => c.Parameters))
            if (haveVariables.Contains(v.Name))
            {
                string n = v.Name;
                for (int k = 2; haveVariables.Contains(n) || variables.ContainsValue(n); k++) n = $"{v.Name}_{k}";
                variables[v.Name] = n;
            }
        foreach (var c in model.Components)
            foreach (var p in c.Parameters)
            {
                if (c.Symbol == SymbolKind.Var && variables.TryGetValue(p.Name, out var renamed)) p.Name = renamed;
                else if (variables.TryGetValue(p.Expression, out var used)) p.Expression = used;
            }
        if (model.Tuning is { } tuning) foreach (var t in tuning.Variables) if (variables.TryGetValue(t.Key, out var r)) t.Key = r;

        var haveNets = into.NetLabels.Select(l => l.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nets = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var label in model.NetLabels)
        {
            if (!haveNets.Contains(label.Name) && !nets.ContainsKey(label.Name)) continue;
            if (!nets.TryGetValue(label.Name, out var n))
            {
                n = label.Name;
                for (int k = 2; haveNets.Contains(n) || nets.ContainsValue(n); k++) n = $"{label.Name}_{k}";
                nets[label.Name] = n;
            }
            label.Name = n;
        }

        if (variables.Count > 0)
            reading.Report.Add(RecognitionFindingClass.PictureVariablesRenamed, variables.Count,
                "The schematic already has " + string.Join(", ", variables.Select(kv => $"{kv.Key} (the new one is {kv.Value})")) + ".");
        if (nets.Count > 0)
            reading.Report.Add(RecognitionFindingClass.PictureVariablesRenamed, nets.Count,
                "The schematic already has the net " + string.Join(", ", nets.Select(kv => $"{kv.Key} (the new one is {kv.Value})")) +
                ", so the two are not joined.");
        if (into.Analyses.Count > 0 && model.Analyses.Count > 0)
        {
            model.Analyses.Clear();
            reading.Report.Add(RecognitionFindingClass.PictureEmitNotes, 1,
                "The schematic already has analyses, so no S-parameter analysis was added.");
        }
        return new SchematicImageEdit(reading, model, variables, nets, null);
    }

    // ── the reading ─────────────────────────────────────────────────────────────────────────────────

    private static SchematicImageResult Read(SchematicImageInput input, RecognitionEmitOptions emit, SchematicFrame? frame, RunControl? control)
    {
        var report = new RecognitionReport();
        var read = SchematicImageReading.Read(input.Source, input.Options, control);
        foreach (var line in read.Report.Lines()) report.Add(RecognitionFindingClass.PictureLineWork, 1, line);
        if (!read.Ok) return new SchematicImageResult(read, null, [], null, null, report, read.Refusal);

        control?.BeginStage("Naming symbols");
        var symbols = SchematicSymbols.Read(read, input.Classifier);
        foreach (var line in symbols.Report.Lines()) report.Add(RecognitionFindingClass.PictureSymbols, 1, line);

        control?.BeginStage("Reading words");
        var words = ImageText.Read(read, input.TextReader);
        var labels = LabelAssociation.Associate(read, symbols, words);
        foreach (var line in labels.Report.Lines()) report.Add(RecognitionFindingClass.PictureWords, 1, line);
        (control?.Token ?? CancellationToken.None).ThrowIfCancellationRequested();

        control?.BeginStage("Making the circuit");
        var circuit = SchematicImageEmit.Build(read, symbols, labels, input.Options.Overrides, new SchematicEmitOptions
        {
            Emit = emit, Technology = input.Technology, TechnologyPath = input.TechnologyPath,
            PartsCsvText = input.PartsCsvText, PartsCsvPath = input.PartsCsvPath, Frame = frame,
        }, report, out string? refusal);
        return new SchematicImageResult(read, symbols, words, labels, circuit, report, refusal);
    }

    /// <summary>The drawing — as drawn or tidied — with its report lines; null when it cannot be drawn.</summary>
    private static SchematicEditModel? Draw(SchematicImageResult reading, SchematicImageInput input, string? schematicDir)
    {
        var circuit = reading.Circuit!;
        var report = reading.Report;
        var drawn = NetlistSchematic.Build(new Library("netlist"), circuit.TestBench, schematicDir,
                                           exact: input.Arrange == SchematicArrange.AsDrawn ? circuit.Exact : null);
        if (drawn.Schematic is null)
        {
            report.Add(RecognitionFindingClass.DrawingNotes, drawn.Refusals.Count, string.Join(" ", drawn.Refusals));
            return null;
        }
        report.Add(RecognitionFindingClass.DrawingNotes, drawn.Notes.Count, string.Join(" ", drawn.Notes));
        report.Add(RecognitionFindingClass.PicturePlacementFallbacks, drawn.PlacementFallbacks.Count,
            $"{string.Join(", ", drawn.PlacementFallbacks)} would land on another part where the picture draws " +
            $"{(drawn.PlacementFallbacks.Count == 1 ? "it, and was" : "them, and were")} placed beside the drawing.");
        if (input.Arrange == SchematicArrange.Tidy && input.KeepUnderlay)
            report.Add(RecognitionFindingClass.PictureUnderlay, 1,
                "Tidy arranges the parts its own way, so the picture is not laid under the drawing: it would not line up.");
        return drawn.Schematic;
    }

    /// <summary>D5: the picture as a locked 35 % bitmap behind the drawing, at the drawing's frame — As drawn only.</summary>
    private static EditableBitmap? Underlay(SchematicImageResult reading, SchematicImageInput input, SchematicEditModel model, string pictureRef)
    {
        if (input.Arrange != SchematicArrange.AsDrawn || !input.KeepUnderlay) return null;
        var f = reading.Circuit!.Frame;
        var raster = input.Source.Raster;
        double width = f.Scale * raster.Width, height = f.Scale * raster.Height;
        return new EditableBitmap
        {
            ImagePath = pictureRef, X = f.OffsetX + width / 2, Y = f.OffsetY + height / 2, Width = width, Height = height,
            Transparency = 1 - Layout.Recognition.Image.ImageTrace.UnderlayOpacity, IsLocked = true,
            ZOrder = model.CanvasObjects.Count > 0 ? model.CanvasObjects.Min(o => o.ZOrder) - 1 : 0,
        };
    }

    /// <summary>The <c>ImageSource</c> block (R-im10-10): the picture, the reading options and the corrections.</summary>
    internal static ImageProvenance Provenance(SchematicImageInput input, string pictureRef, DateTime now)
    {
        var kind = input.Kind ?? ImageKind.Classify(input.Source.Raster);
        if (kind.Kind != DrawingKind.Schematic) kind = kind.Force(DrawingKind.Schematic);
        var provenance = ImageProvenance.For(input.Source, kind, pictureRef, now);
        var o = input.Options;
        var d = new SchematicImageOptions();
        static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        provenance.Options["Arrange"] = input.Arrange == SchematicArrange.AsDrawn ? "as-drawn" : "tidy";
        provenance.Options["Crossings"] = o.Crossings switch { CrossingRule.Never => "never", CrossingRule.Always => "always", _ => "dot" };
        if (o.LineArt.K != d.LineArt.K) provenance.Options["Threshold"] = N(o.LineArt.K);
        if (o.LineArt.Window != d.LineArt.Window) provenance.Options["Window"] = o.LineArt.Window.ToString(CultureInfo.InvariantCulture);
        if (o.Overrides.Count > 0) provenance.Options["Overrides"] = ConnectionOverride.Spell(o.Overrides);
        if (input.PartsCsvText is not null || input.PartsCsvPath is not null) provenance.Options["PartsTable"] = "edited";
        return provenance;
    }
}
