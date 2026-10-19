// File ▸ Import ▸ Schematic…'s dialog — brief-schimp-5 R-si5-2, overview D1, D9.
//
// A SHELL, and nothing else. What the file is, what each part becomes, the drawing, the connectivity check and the write
// all come from src/Design: SchematicImport.Read, .PortCandidates, .Preview and .Import — the functions the drop, the
// CLI and the MCP tool call. What is here is the dialog's state: the fields, the two tables, and re-asking Preview
// whenever a field that changes the build changes. A source scan (SchematicImportEntryPointTests) holds that shut.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CircuitRF.Design.Schematic.Import;

namespace CircuitRF.Ui.ViewModels;

/// <summary>One row of the parts table: what a source part becomes, and whether the user pushed it to a symbol cell.</summary>
public sealed partial class ImportSchematicPartRow(ImportPartDecision decision, Action changed) : ObservableObject
{
    public ImportPartDecision Decision { get; private set; } = decision;

    public string Reference => Decision.Reference;
    public string Symbol => Decision.SymbolName;
    public string Value => Decision.Value ?? "";

    /// <summary>The "becomes" column.</summary>
    public string Becomes => Decision.Becomes switch
    {
        ImportPartKind.Native => $"{Decision.Native} {Decision.Parameter}={Decision.Expression}{(Decision.Unit is { Length: > 0 } u ? " " + u : "")}",
        ImportPartKind.Ground => "ground",
        _ => $"symbol cell {Decision.CellName}",
    };

    /// <summary>A native part may be pushed back to a symbol cell (R-si1-3); never the other way.</summary>
    public bool CanBeSymbolCell => Decision.Becomes == ImportPartKind.Native || ForceSymbolCell;

    [ObservableProperty] private bool _forceSymbolCell;

    partial void OnForceSymbolCellChanged(bool value) => changed();

    internal void Update(ImportPartDecision decision)
    {
        Decision = decision;
        OnPropertyChanged(nameof(Becomes));
        OnPropertyChanged(nameof(CanBeSymbolCell));
    }
}

/// <summary>One row of the ports list (overview D9): unchecked by default — what an RF port is, is the user's call.</summary>
public sealed partial class ImportSchematicPortRow(ImportPortCandidate candidate, Action changed) : ObservableObject
{
    public ImportPortCandidate Candidate { get; } = candidate;
    public string Description => Candidate.Description;

    [ObservableProperty] private bool _makePort;

    partial void OnMakePortChanged(bool value) => changed();
}

public sealed partial class ImportSchematicViewModel : ObservableObject, IDisposable
{
    private ForeignSchematic? _source;
    private SchematicImportPreview? _preview;
    private int _generation;
    private bool _seeding;

    /// <param name="sourcePath">The file to import (the picker's answer, or the dropped file).</param>
    /// <param name="targetFolder">Where the new cell goes — the tree's folder, else the workspace root.</param>
    public ImportSchematicViewModel(string sourcePath, string targetFolder)
    {
        SourcePath = sourcePath;
        TargetFolder = targetFolder;
        _cellName = SchematicImportMap.CellNameFor(Path.GetFileNameWithoutExtension(sourcePath));
        _nameError = SchematicImport.CellNameRefusal(targetFolder, _cellName);
        Loaded = ReadAsync();
    }

    public string SourcePath { get; }
    public string TargetFolder { get; }

    /// <summary>Completes when the file has been read and the first preview built — what a test awaits.</summary>
    public Task Loaded { get; private set; }

    /// <summary>The preview the import would write; replaced (and the old one disposed) on every rebuild.</summary>
    public SchematicImportPreview? Preview => _preview;

    /// <summary>Raised on the thread that rebuilt the preview — the dialog redraws its picture.</summary>
    public event Action? PreviewChanged;

    public ObservableCollection<ImportSchematicPartRow> Parts { get; } = [];
    public ObservableCollection<ImportSchematicPortRow> Ports { get; } = [];

    [ObservableProperty] private string _formatText = "Reading…";
    [ObservableProperty] private string _pagesText = "";
    [ObservableProperty] private string _connectivityText = "";
    [ObservableProperty] private string _reportText = "";
    [ObservableProperty] private string? _readRefusal;
    [ObservableProperty] private string? _nameError;
    [ObservableProperty] private string? _importRefusal;
    [ObservableProperty] private bool _connectivityMatched;
    [ObservableProperty] private bool _showCodePage;
    [ObservableProperty] private bool _isBusy = true;
    [ObservableProperty] private string _cellName;
    [ObservableProperty] private string _codePageText = SchematicImport.DefaultCodePage.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Import is pressed only on a read file, a valid name, and a build the import would not refuse.</summary>
    public bool CanImport => !IsBusy && _source is not null && NameError is null && ImportRefusal is null;

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanImport));
    partial void OnNameErrorChanged(string? value) => OnPropertyChanged(nameof(CanImport));
    partial void OnImportRefusalChanged(string? value) => OnPropertyChanged(nameof(CanImport));

    partial void OnCellNameChanged(string value)
    {
        NameError = SchematicImport.CellNameRefusal(TargetFolder, value);
        Rebuild();
    }

    partial void OnCodePageTextChanged(string value)
    {
        if (!ShowCodePage) return;
        Loaded = ReadAsync();
    }

    /// <summary>The options the build takes from the two tables.</summary>
    public SchematicImportOptions Options => new()
    {
        Ports = [.. Ports.Where(p => p.MakePort).Select(p => p.Candidate.Key)],
        SymbolCells = [.. Parts.Where(p => p.ForceSymbolCell).Select(p => p.Reference)],
    };

    private int? CodePage =>
        int.TryParse(CodePageText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int cp) && cp > 0
            ? cp : null;

    private async Task ReadAsync()
    {
        int gen = ++_generation;
        IsBusy = true;
        string path = SourcePath;
        int? codePage = CodePage;
        var read = await Task.Run(() => SchematicImport.Read(path, codePage));
        if (gen != _generation) return;

        _source = read.Schematic;
        ShowCodePage = SchematicImport.TakesCodePage(read.Kind);
        ReadRefusal = read.Refusal;
        if (read.Schematic is not { } source)
        {
            FormatText = SchematicImport.FormatName(read.Kind);
            ReplacePreview(null);
            IsBusy = false;
            return;
        }
        FormatText = source.Version is { Length: > 0 } v ? $"{SchematicImport.FormatName(read.Kind)}, version {v}" : SchematicImport.FormatName(read.Kind);
        var pages = source.Pages.Select(p => p.Name).ToList();
        PagesText = $"{pages.Count} {(source.Hierarchical ? "sheet" : "page")}{(pages.Count == 1 ? "" : "s")}: {string.Join(", ", pages)}";

        // The tables are re-seeded from the new read, keeping what the user had ticked where the row is still there.
        var forced = Parts.Where(p => p.ForceSymbolCell).Select(p => p.Reference).ToHashSet(StringComparer.Ordinal);
        var ported = Ports.Where(p => p.MakePort).Select(p => p.Candidate.Key).ToHashSet(StringComparer.Ordinal);
        _seeding = true;
        Parts.Clear();
        foreach (var d in SchematicImportMap.Decide(source))
            Parts.Add(new ImportSchematicPartRow(d, Rebuild) { ForceSymbolCell = forced.Contains(d.Reference) });
        Ports.Clear();
        foreach (var c in SchematicImport.PortCandidates(source))
            Ports.Add(new ImportSchematicPortRow(c, Rebuild) { MakePort = ported.Contains(c.Key) });
        _seeding = false;
        await BuildPreviewAsync(gen);
    }

    private void Rebuild()
    {
        if (_seeding || _source is null) return;
        Loaded = BuildPreviewAsync(++_generation);
    }

    private async Task BuildPreviewAsync(int gen)
    {
        if (_source is not { } source) return;
        IsBusy = true;
        string folder = TargetFolder, name = CellName;
        var options = Options;
        var preview = await Task.Run(() => SchematicImport.Preview(source, folder, name, options));
        if (gen != _generation) { preview.Dispose(); return; }

        foreach (var row in Parts)
            if (preview.Mapping.FirstOrDefault(d => d.Reference == row.Reference) is { } d) row.Update(d);
        ImportRefusal = preview.Refusal == NameError ? null : preview.Refusal;
        ConnectivityMatched = preview.Connectivity?.Matched == true;
        ConnectivityText = preview.Connectivity?.Summary ?? "";
        ReportText = string.Join(Environment.NewLine, preview.Messages);
        ReplacePreview(preview);
        IsBusy = false;
    }

    private void ReplacePreview(SchematicImportPreview? preview)
    {
        var old = _preview;
        _preview = preview;
        PreviewChanged?.Invoke();
        old?.Dispose();
    }

    /// <summary>Import: <see cref="SchematicImport.Import"/> with the dialog's fields — the one function the CLI calls too.
    /// Null when there is nothing to import.</summary>
    public SchematicImportResult? Import()
    {
        if (_source is not { } source || !CanImport) return null;
        return SchematicImport.Import(source, TargetFolder, CellName, Options);
    }

    public void Dispose()
    {
        _generation++;
        ReplacePreview(null);
    }
}
