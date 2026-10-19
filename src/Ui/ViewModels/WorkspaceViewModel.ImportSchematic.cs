using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Design.Schematic.Import;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels.ProjectTree;
using CircuitRF.Ui.Views.Dialogs;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// Import Schematic's ways in (brief-schimp-5): File ▸ Import ▸ Schematic… and a schematic file dropped on the project
/// tree open the dialog; one dropped on an open schematic is built into it at the drop point. <b>No import logic here</b>
/// (overview D1): the read, the preview, the import and the build-into are <see cref="SchematicImport"/>'s, the functions
/// the CLI and the MCP tool call. What is here is picking, prompting and reporting.
/// </summary>
public partial class WorkspaceViewModel : ISchematicFileDropHost
{
    private const string ImportSchematicCommandName = "Import Schematic";

    private bool CanImportSchematic() => CurrentWorkspacePath is not null;

    /// <summary>R-si5-1: File ▸ Import ▸ Schematic…, into the tree's selected folder, else the workspace root.</summary>
    [RelayCommand(CanExecute = nameof(CanImportSchematic))]
    private async Task ImportSchematic(Window? owner)
    {
        if (CurrentWorkspaceRoot is not { } root) return;
        var window = ResolveOwner(owner);
        if (window is null) return;
        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Schematic",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType($"{SchematicImport.FileFilterName} ({string.Join("; ", SchematicImport.FileExtensions.Select(e => "*" + e))})")
                {
                    Patterns = [.. SchematicImport.FileExtensions.Select(e => "*" + e)],
                },
                new FilePickerFileType("All files") { Patterns = ["*"] },
            ],
        });
        if (files.Count == 0 || files[0].Path.LocalPath is not { Length: > 0 } path) return;
        await ShowImportSchematicDialogAsync(window, path, SelectedTreeFolder() ?? root);
    }

    /// <summary>R-si5-4: a schematic file dropped on a workspace or folder row — the dialog, targeting that folder.</summary>
    public Task ImportSchematicFileAsync(string sourceFile, string? destFolderDir)
    {
        if (CurrentWorkspaceRoot is not { } root) return Task.CompletedTask;
        return ShowImportSchematicDialogAsync(ResolveOwner(null), sourceFile, destFolderDir ?? root);
    }

    /// <summary>The folder the tree's selection names: a folder is itself, a cell is its parent; anything else, none.</summary>
    private string? SelectedTreeFolder()
    {
        if (_factory.ProjectTreeTool?.Selection.FirstOrDefault() is not { } node) return null;
        string path = node.AbsolutePath;
        if (node.Kind == NodeKind.Cell) return Path.GetDirectoryName(path);
        return node.Kind is NodeKind.Workspace or NodeKind.UserFolder && Directory.Exists(path) ? path : null;
    }

    private async Task ShowImportSchematicDialogAsync(Window? owner, string path, string targetFolder)
    {
        if (owner is null) return;
        using var vm = new ImportSchematicViewModel(path, targetFolder);
        var result = await new ImportSchematicDialog(vm).ShowDialog<SchematicImportResult?>(owner);
        if (result is not null) ReportSchematicImport(result);
    }

    /// <summary>
    /// The import's report in Messages — the first line names what was written, the connectivity verdict is a warning
    /// when it is not matched — then the new cell's schematic opens (R-si5-2).
    /// </summary>
    internal void ReportSchematicImport(SchematicImportResult result)
    {
        if (!result.Ok)
        {
            Messages.Error($"{ImportSchematicCommandName}: {result.Refusal}");
            return;
        }
        foreach (string line in result.Messages)
        {
            bool mismatch = result.Connectivity is { Matched: false } c && line == c.Summary;
            Messages.Post(mismatch ? MessageLevel.Warning : MessageLevel.Info, line,
                          line == result.Messages[0] ? result.SchematicPath : null);
        }
        _factory.ProjectTreeTool?.Refresh();
        if (result.SchematicPath is { } schematic) OpenOrActivateSchematic(schematic);
    }

    /// <summary>The read and the build R-si5-3 runs off the UI thread — a seam for the tests, which make it block.</summary>
    internal Func<string, SchematicEditModel, (double X, double Y), SchematicImportEdit> SchematicFileBuilder { get; set; } =
        (path, into, at) =>
        {
            var read = SchematicImport.Read(path);
            return read.Ok
                ? SchematicImport.BuildInto(read.Schematic!, into, at)
                : new SchematicImportEdit(null, null, into.GridSize, [], [], [], null, [], read.Refusal);
        };

    /// <summary>
    /// R-si5-3, overview D10: <paramref name="path"/> built into <paramref name="doc"/>'s schematic with its top-left at
    /// <paramref name="at"/>, read and built off the UI thread with Esc to cancel (the paste's live row), then added as one
    /// undo step. No dialog: every part per the map, no ports. One line in Messages, with Details… opening the report.
    /// An unsaved schematic is refused by the build: its parts are written as cells beside it, and it has no "beside" yet.
    /// </summary>
    public async Task BuildSchematicFileIntoAsync(SchematicDocument doc, string path, (double X, double Y) at)
    {
        const string command = ImportSchematicCommandName;
        string file = Path.GetFileName(path);
        if (_paste is not null)
        {
            Messages.Error($"{command}: {file} was not built — another drop or paste is still reading.");
            return;
        }
        var vm = doc.ActiveViewModel;
        var live = vm.EditModel;
        // A snapshot of what the build reads from the open schematic (its names, grid and folder) — the editor keeps its
        // model on the UI thread while the build runs.
        var into = new SchematicEditModel
        {
            SchematicDirectory = live.SchematicDirectory,
            GridSize = live.GridSize,
            Tuning = live.Tuning?.Clone(),
        };
        into.Components.AddRange(live.Components);
        into.NetLabels.AddRange(live.NetLabels);

        var (row, _, cancellation, cts) = BeginPaste(command, doc, $"building {file}");
        SchematicImportEdit edit;
        try
        {
            var builder = SchematicFileBuilder;
            var token = cts.Token;
            // Cancelled before the build begins, nothing is written; once it begins it finishes, because it writes the
            // part cells — and those cells are then used, not left behind unexplained.
            edit = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                return builder(path, into, at);
            });
        }
        catch (OperationCanceledException)
        {
            row.Complete(MessageLevel.Info, $"{command} cancelled — {file} was not built.");
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            row.Complete(MessageLevel.Error, $"{command}: {file} could not be built: {ex.Message}");
            return;
        }
        finally { EndPaste(cancellation, cts); }

        if (!edit.Ok)
        {
            row.Complete(MessageLevel.Error, $"{command}: {edit.Refusal}");
            return;
        }
        if (SchematicImportEditCommand.For(vm, edit) is not { } command1)
        {
            row.Complete(MessageLevel.Error, $"{command}: {file} draws nothing to build.");
            return;
        }
        vm.Execute(command1);

        int native = edit.Mapping.Count(d => d.Becomes == ImportPartKind.Native);
        int cells = edit.Mapping.Count(d => d.Becomes == ImportPartKind.SymbolCell);
        int parts = edit.Mapping.Count(d => d.Becomes != ImportPartKind.Ground);
        bool matched = edit.Connectivity?.Matched == true;
        string into1 = doc.FilePath is { Length: > 0 } fp ? Path.GetFileName(fp) : "the schematic";
        string sentence = $"Built {file} into {into1}: {parts} part{(parts == 1 ? "" : "s")} ({native} native, {cells} symbol "
                        + $"cell{(cells == 1 ? "" : "s")}, {edit.ReusedCells.Count} reused), "
                        + (matched ? "connectivity matched." : "connectivity NOT matched — see Details.");
        string report = string.Join(Environment.NewLine, edit.Messages.Concat(edit.CreatedCells.Select(c => $"Wrote the cell {c}.")));
        row.CompleteWithAction(matched ? MessageLevel.Success : MessageLevel.Warning, sentence, "Details…",
            () => TextConfirmDialog.AskAsync(ResolveOwner(null), command, sentence, report, null));
        if (edit.CreatedCells.Count > 0) _factory.ProjectTreeTool?.Refresh();
    }
}
