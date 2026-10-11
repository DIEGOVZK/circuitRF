using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Layout.Recognition;
using CircuitRF.Design.Layout.Recognition.Image;
using CircuitRF.Design.Workspace;
using CircuitRF.Design.Layout;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Recognition;
using CircuitRF.Ui.Theming;
using CircuitRF.Ui.ViewModels.ProjectTree;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// Create Schematic / Layout from Image (brief-img-5-dialog.md R-im5-6, R-im5-7, R-im5-9) — the shell around the
/// dialog: opening it on the workspace with its technologies and the user's own settings, and writing what it creates
/// into the workspace's surfaces — the result opened focused, the report in Messages, a trace into the open layout as
/// one undo step with what it added selected.
///
/// <para><b>It never runs except from the user's invocation</b> (the L5 rule). Every way in
/// (brief-img-6-entry-points.md) — the Design ▸ Create rows, a placed picture's right-click and the Project Tree's picture
/// rows — calls <see cref="ShowImageDialog"/>; Edit ▸ Paste Image as Schematic / Layout ask nothing and paste into the
/// document in focus with every default. What is written is written by
/// <see cref="ImageTrace.Run"/> and <see cref="ImageRecognition.Run"/> — the functions the CLI calls (overview D2).</para>
/// </summary>
public partial class WorkspaceViewModel : IImageDialogHost
{
    private CreateSchematicFromArtworkDialog? _imageDialog;
    private PasteImageAvailability? _pasteImage;

    // ── the Design menu (R-im6-1): enabled whenever a workspace is open ──────────────────────────────

    private bool IsWorkspaceOpenForImage() => CurrentWorkspaceRoot is not null;

    [RelayCommand(CanExecute = nameof(IsWorkspaceOpenForImage))]
    private void CreateSchematicFromImage(Window? owner) => ShowImageDialog(null, makeSchematic: true, owner);

    [RelayCommand(CanExecute = nameof(IsWorkspaceOpenForImage))]
    private void CreateLayoutFromImage(Window? owner) => ShowImageDialog(null, makeSchematic: false, owner);

    // ── Edit ▸ Paste Image as … (R-im6-2): enabled only when the clipboard holds a picture ────────────

    /// <summary>The clipboard the paste rows read — the window's, installed by it; null until a window has one.</summary>
    public Func<IPictureClipboard?> PictureClipboard { get; set; } = () => null;

    /// <summary>Whether the clipboard holds a picture, as last asked by <see cref="RefreshPasteImage"/>.</summary>
    public PasteImageAvailability PasteImage => _pasteImage ??= NewPasteImage();

    private PasteImageAvailability NewPasteImage()
    {
        var p = new PasteImageAvailability(() => PictureClipboard());
        p.Changed += RaiseImageCommandsChanged;
        return p;
    }

    /// <summary>
    /// Asks the clipboard again — called just before the Edit menu shows (NeedsUpdate on macOS, SubmenuOpened
    /// elsewhere) and when the window is activated, because the clipboard changes while the user is in another
    /// application. Nothing else calls it: the clipboard is never polled.
    /// </summary>
    public Task RefreshPasteImage() => PasteImage.RefreshAsync();

    private bool CanPasteImage() => CurrentWorkspaceRoot is not null && PasteImage.Available;

    // Paste Image as Schematic / as Layout paste INTO the document in focus, so each is enabled only while a document of
    // its own kind is — and asks nothing: every setting is the default. Design ▸ Create ▸ is where the options are.
    private bool CanPasteImageAsSchematic() => PasteImage.Available && IsSchematicDocumentActive();

    private bool CanPasteImageAsLayout() => PasteImage.Available && IsLayoutDocumentActive();

    [RelayCommand(CanExecute = nameof(CanPasteImageAsSchematic))]
    private async Task PasteImageAsSchematic()
    {
        const string command = "Paste Image as Schematic";
        if (ResolveActiveDocumentForCommands() is not SchematicDocument doc)
        {
            Messages.Error($"{command}: click into a schematic first — the picture is pasted into the schematic in focus.");
            return;
        }
        var read = await PasteImage.ReadAsync();
        if (read is null) { Messages.Error($"{command}: the clipboard holds no picture."); return; }
        if (!read.Ok) { Messages.Error($"{command}: {read.Refusal}"); return; }

        var vm = doc.ActiveViewModel;
        string? path = doc.FilePath is { Length: > 0 } fp ? Path.GetFullPath(fp) : null;
        var input = new CircuitRF.Design.Schematic.Recognition.SchematicImageInput { Source = read.Source! };
        CircuitRF.Design.Schematic.Recognition.SchematicImagePasteDrawing paste;
        try
        {
            paste = await Task.Run(() => CircuitRF.Design.Schematic.Recognition.SchematicImageRecognition.PasteDrawing(
                input, path is null ? null : Path.GetDirectoryName(path)));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
        {
            Messages.Error($"{command}: the picture could not be read: {ex.Message}");
            return;
        }
        if (!paste.Ok)
        {
            PostImageReport(paste.Reading.Report, command, null);
            Messages.Error($"{command}: {paste.Refusal}");
            return;
        }

        // The picture goes under the drawing as an ordinary bitmap, kept beside the schematic so the reference holds.
        string? pictureRef = null;
        if (path is null)
            Messages.Info($"{command}: the schematic has not been saved, so the picture is not kept under the drawing.");
        else
            // The ABSOLUTE path: an open document holds its bitmaps' paths resolved (the readers resolve a stored relative
            // one against the document's folder as they load); a relative path here is resolved against the process's
            // working directory, finds nothing, and draws the missing-picture cross.
            try { pictureRef = ImageKeep.ForDocument(path, CircuitRF.Design.Cells.ViewType.Schematic, read.Source!).Kept.FullPath; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Messages.Warning($"{command}: the picture could not be kept beside the schematic, so it is not under the drawing: {ex.Message}");
            }

        var (x, y, inView) = vm.PicturePastePlace(paste.Picture.Width, paste.Picture.Height);
        var edit = CircuitRF.Design.Schematic.Recognition.SchematicImageRecognition.PasteInto(paste, vm.EditModel, (x, y), pictureRef);
        if (SchematicImageEditCommand.For(vm, edit, command) is not { } paste1)
        {
            PostImageReport(edit.Report, command, null);
            Messages.Error($"{command}: {edit.Refusal ?? "nothing in the picture was recognised as a part."}");
            return;
        }
        vm.Execute(paste1);
        PostImageReport(edit.Report, command, null);
        if (!inView) doc.RequestZoomToWorldRect(x, y, x + paste.Picture.Width, y + paste.Picture.Height);
    }

    [RelayCommand(CanExecute = nameof(CanPasteImageAsLayout))]
    private async Task PasteImageAsLayout()
    {
        const string command = "Paste Image as Layout";
        if (ResolveActiveDocumentForCommands() is not LayoutDocument doc)
        {
            Messages.Error($"{command}: click into a layout first — the picture is pasted into the layout in focus.");
            return;
        }
        var read = await PasteImage.ReadAsync();
        if (read is null) { Messages.Error($"{command}: the clipboard holds no picture."); return; }
        if (!read.Ok) { Messages.Error($"{command}: {read.Refusal}"); return; }

        var vm = doc.ActiveViewModel;
        if (vm.Technology is not { } tech)
        {
            Messages.Error($"{command}: the layout has no technology, so there are no layers to trace the picture onto.");
            return;
        }
        string? path = doc.FilePath is { Length: > 0 } fp ? Path.GetFullPath(fp) : null;
        var input = new ImageTraceInput { Source = read.Source!, Technology = tech, DbuPerMicron = vm.Model.DbuPerMicron };
        ImageTraceResult result;
        try { result = await Task.Run(() => ImageTrace.PasteTrace(input)); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
        {
            Messages.Error($"{command}: the picture could not be traced: {ex.Message}");
            return;
        }
        if (!result.Ok || result.Shapes.Count == 0)
        {
            PostImageReport(result.Report, command, null);
            Messages.Error($"{command}: {result.Refusal ?? "nothing in the picture was traced."}");
            return;
        }

        var shapes = new List<LayoutShape>();
        if (result.Underlay is { } underlay && path is not null)
            try
            {
                // Absolute, as an open layout holds every bitmap's path (see the schematic paste above).
                underlay.ImagePathRef = ImageKeep.ForDocument(path, CircuitRF.Design.Cells.ViewType.Layout, read.Source!).Kept.FullPath;
                shapes.Add(underlay);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Messages.Warning($"{command}: the picture could not be kept beside the layout, so it is not under the shapes: {ex.Message}");
            }
        else if (path is null)
            Messages.Info($"{command}: the layout has not been saved, so the picture is not kept under the shapes.");
        shapes.AddRange(result.Shapes);
        bool inView = vm.InsertTracedPicture(shapes);
        PostImageReport(result.Report, command, null);
        if (!inView) doc.RequestZoomToFit();
    }

    /// <summary>Edit ▸ Paste Image as Touchstone… (brief-img-14 R-im14-13): IM-6's clipboard rule, a plot read.</summary>
    [RelayCommand(CanExecute = nameof(CanPasteImage))]
    private async Task PasteImageAsTouchstone(Window? owner)
    {
        var read = await PasteImage.ReadAsync();
        if (read is null) Messages.Error("Paste Image as Touchstone: the clipboard holds no picture.");
        else if (!read.Ok) Messages.Error($"Paste Image as Touchstone: {read.Refusal}");
        else ShowPlotDialog(read.Source, csv: false, owner);
    }

    /// <summary>Tools ▸ Digitise Plot… (R-im14-13): the dialog, empty, in plot mode.</summary>
    [RelayCommand(CanExecute = nameof(IsWorkspaceOpenForImage))]
    private void DigitisePlot(Window? owner) => ShowPlotDialog(null, csv: true, owner);

    public void CreateTouchstoneFromImageFile(ProjectTreeNodeViewModel node)
    {
        var read = ImageSource.FromFile(node.AbsolutePath);
        if (read.Ok) ShowPlotDialog(read.Source, csv: false, null);
        else Messages.Error($"Create Touchstone from Plot: {read.Refusal}");
    }

    /// <summary>The picture dialog in plot mode — reading <paramref name="source"/> as a plot (added to the session when
    /// the dialog is already reading plots), or empty.</summary>
    public void ShowPlotDialog(ImageSource? source, bool csv, Window? owner)
    {
        bool wasOpen = _imageDialog is not null;
        if (!wasOpen) ShowImageDialog(null, makeSchematic: false, owner);
        if (_imageDialog?.DataContext is not ImageSourceViewModel vm) return;
        if (source is not null) vm.ReadPlot(source, csv);
        else if (!wasOpen) vm.StartPlotMode(csv);
        _imageDialog.Activate();
    }

    private void RaiseImageCommandsChanged()
    {
        CreateSchematicFromImageCommand.NotifyCanExecuteChanged();
        CreateLayoutFromImageCommand.NotifyCanExecuteChanged();
        PasteImageAsSchematicCommand.NotifyCanExecuteChanged();
        PasteImageAsLayoutCommand.NotifyCanExecuteChanged();
        PasteImageAsTouchstoneCommand.NotifyCanExecuteChanged();
        DigitisePlotCommand.NotifyCanExecuteChanged();
    }

    // ── a placed picture (R-im6-3) and the Project Tree (R-im6-4) ────────────────────────────────────

    /// <summary>The dialog on a placed bitmap's picture or a tree file; a picture that cannot be read is said in
    /// Messages, in its own sentence.</summary>
    public void ShowImageDialogFor(ImageSourceResult read, bool makeSchematic, Window? owner)
    {
        if (read.Ok) ShowImageDialog(read.Source, makeSchematic, owner);
        else Messages.Error($"{(makeSchematic ? "Create Schematic from Image" : "Create Layout from Image")}: {read.Refusal}");
    }

    public void CreateFromImageFile(ProjectTreeNodeViewModel node, bool makeSchematic) =>
        ShowImageDialogFor(ImageSource.FromFile(node.AbsolutePath), makeSchematic, null);

    /// <summary>
    /// Opens the picture dialog — empty, or already reading <paramref name="source"/> — with Make preset. One dialog per
    /// window: when it is already open, a picture handed in is read there and the dialog comes forward.
    /// </summary>
    public void ShowImageDialog(ImageSource? source, bool makeSchematic, Window? owner)
    {
        if (CurrentWorkspaceRoot is not { } root)
        {
            Messages.Error("Create from Image: open a workspace first — the result is a new cell in it.");
            return;
        }
        if (_imageDialog is { } open)
        {
            if (source is not null && open.DataContext is ImageSourceViewModel reading) reading.Read(source, makeSchematic);
            open.Activate();
            return;
        }

        var (technologies, preferred) = ImageTechnologies(root, source);
        var prefs = AppPreferencesIo.Load();
        var vm = new ImageSourceViewModel(root, technologies, preferred, source, makeSchematic,
                                          post: a => Dispatcher.UIThread.Post(a),
                                          options: new ImageTraceOptions { KeepUnderlay = prefs.KeepSourcePictureUnderResult ?? true })
        {
            ArtworkSides = prefs.LinkSchematicLayoutOrientation ?? true,
            AdvancedExpanded = prefs.ImageDialogAdvancedExpanded ?? false,
        };
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(ImageSourceViewModel.AdvancedExpanded)) return;
            var p = AppPreferencesIo.Load();
            p.ImageDialogAdvancedExpanded = vm.AdvancedExpanded ? true : null;
            AppPreferencesIo.Save(p);
        };

        var dialog = new CreateSchematicFromArtworkDialog(vm);
        vm.LayoutCreated += run =>
        {
            ReportImageLayout(run);
            dialog.Close();
        };
        vm.SchematicCreated += run =>
        {
            ReportImageSchematic(run);
            dialog.Close();
        };
        vm.SchematicPictureCreated += run =>
        {
            _factory.ProjectTreeTool?.Refresh();
            PostImageReport(run.Report, "Create Schematic from Image", run.SchematicPath);
            if (run.SchematicPath is { } schematic) OpenOrActivateSchematic(Path.GetFullPath(schematic));
            dialog.Close();
        };
        vm.SchematicPictureDrawn += (edit, placed) =>
        {
            DrawImageSchematic(edit, placed);
            dialog.Close();
        };
        vm.TouchstoneCreated += (path, assembly, kept, session) =>
        {
            ReportTouchstone(path, assembly, kept, session);
            dialog.Close();
        };
        vm.PlotCsvCreated += path =>
        {
            Messages.Post(MessageLevel.Success, $"Digitise Plot: wrote {Path.GetFileName(path)}", path);
            _factory.ProjectTreeTool?.Refresh();
            dialog.Close();
        };
        vm.OpenSchematic = path => _openDocsByPath.ContainsKey(Path.GetFullPath(path)) ? GetOrCreateSession(Path.GetFullPath(path)).EditModel : null;
        dialog.Closed += (_, _) => _imageDialog = null;
        _imageDialog = dialog;
        if (ResolveOwner(owner) is { } window) dialog.Show(window);
        else dialog.Show();
    }

    /// <summary>
    /// R-im5-6: the workspace's technologies, and the one chosen first — the technology of the layout a placed picture
    /// sits in, else the workspace default, else the first.
    /// </summary>
    internal (IReadOnlyList<ImageTechnologyChoice> Choices, int Preferred) ImageTechnologies(string root, ImageSource? source)
    {
        var paths = new List<string>();
        try
        {
            paths.AddRange(Directory.EnumerateFiles(root, "*.ctech", new EnumerationOptions
            {
                RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true,
            }).Select(Path.GetFullPath).Order(StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        string? preferred = null;
        if (source?.Placement is LayoutBitmapPlacement placed)
            preferred = GetOrCreateLayoutSession(Path.GetFullPath(placed.Document)).ResolvedTechPath;
        if (preferred is null)
            try
            {
                if (WorkspacePersistence.LoadFromFile(Path.Combine(root, ".cws")).DefaultTechRef is { Length: > 0 } techRef)
                    preferred = Path.GetFullPath(CircuitRF.Core.RefPath.Resolve(root, techRef));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException) { }

        if (preferred is not null && File.Exists(preferred) && !paths.Contains(preferred, StringComparer.OrdinalIgnoreCase))
            paths.Insert(0, preferred);
        var choices = paths.Select(p => new ImageTechnologyChoice(Path.GetFileNameWithoutExtension(p), p)).ToList();
        int index = preferred is null ? 0 : Math.Max(0, paths.FindIndex(p => string.Equals(p, preferred, StringComparison.OrdinalIgnoreCase)));
        return (choices, index);
    }

    /// <summary>
    /// Make Layout written: the layout opens focused. Into the open layout (D14), the shapes are added through the
    /// editor's own commands as ONE undo step, and what was added is selected.
    /// </summary>
    private void ReportImageLayout(ImageTraceRun run)
    {
        PostImageReport(run.Report, "Create Layout from Image", run.LayoutPath);
        if (run.Edit is { } edit)
        {
            string clay = Path.GetFullPath(edit.ClayPath);
            OpenOrActivateLayout(clay);
            var layoutVm = GetOrCreateLayoutSession(clay);
            if (ImageTraceEditCommand.For(layoutVm.Model, edit) is { } command)
            {
                layoutVm.Execute(command);
                layoutVm.SelectShapes(edit.Shapes);
            }
            return;
        }
        _factory.ProjectTreeTool?.Refresh();
        if (run.LayoutPath is { } path) OpenOrActivateLayout(Path.GetFullPath(path));
    }

    /// <summary>Make Schematic written: the cell holds both views; the schematic opens focused, and the Tuning panel,
    /// which follows the focused schematic, lists its unknown values (as AS-8 R-as8-4).</summary>
    private void ReportImageSchematic(ImageRecognitionRun run)
    {
        _factory.ProjectTreeTool?.Refresh();
        PostImageReport(run.Report, "Create Schematic from Image", run.SchematicPath);
        if (run.SchematicPath is { } schematic) OpenOrActivateSchematic(Path.GetFullPath(schematic));
    }

    /// <summary>
    /// A schematic picture drawn over the placed bitmap it is (brief-img-10 R-im10-6, D14): the schematic it sits in comes
    /// forward, the drawing is added through the editor's own commands as ONE undo step, and what was added is selected.
    /// </summary>
    private void DrawImageSchematic(CircuitRF.Design.Schematic.Recognition.SchematicImageEdit edit, SchematicBitmapPlacement placed)
    {
        string csch = Path.GetFullPath(placed.Document);
        OpenOrActivateSchematic(csch);
        var schematicVm = GetOrCreateSession(csch);
        if (SchematicImageEditCommand.For(schematicVm, edit) is { } command) schematicVm.Execute(command);
        PostImageReport(edit.Report, "Create Schematic from Image", null);
    }

    /// <summary>
    /// After Create, for a plot (brief-img-14 R-im14-11): the report and the health check's verdict to Messages, the file
    /// a known data source, and a Data Display opened on it with the plot types the pictures showed — so the result is
    /// compared with its source at a glance.
    /// </summary>
    private void ReportTouchstone(string path, CircuitRF.Design.Imaging.Plots.TouchstoneAssemblyResult assembly,
                                  IReadOnlyList<KeptPicture> kept, CircuitRF.Design.Imaging.Plots.PlotSession session)
    {
        const string command = "Create Touchstone from Plot";
        Messages.Post(MessageLevel.Success, $"{command}: wrote {Path.GetFileName(path)}", path);
        foreach (string line in assembly.Report) Messages.Post(MessageLevel.Info, $"{command}: {line}", path);
        foreach (var k in kept.Where(k => !k.Reused)) Messages.Post(MessageLevel.Info, $"{command}: kept {k.Ref} beside it", k.FullPath);
        AddKnownFile(path);
        _factory.ProjectTreeTool?.Refresh();
        var types = session.Plots.Where(p => !p.Excluded)
                           .Select(p => p.Type switch
                           {
                               CircuitRF.Design.Imaging.Plots.PlotType.Smith => PlotType.Smith,
                               CircuitRF.Design.Imaging.Plots.PlotType.Polar => PlotType.Polar,
                               _ => PlotType.Rect,
                           })
                           .Distinct().ToList();
        _ = OpenTouchstoneDisplayAsync(path, types.Count == 0 ? [PlotType.Rect] : types);
    }

    private async Task OpenTouchstoneDisplayAsync(string snp, IReadOnlyList<PlotType> types)
    {
        try
        {
            var vm = new CircuitRF.Ui.DataDisplay.DataDisplayDocumentViewModel();
            var doc = new CircuitRF.Ui.DataDisplay.DataDisplayDocument(Path.GetFileNameWithoutExtension(snp), vm);
            _scratchDataDisplays.Add(doc);
            vm.Window.SetOpenFileAsNewDisplayAction(OpenDataDisplayFromFileAsync);
            vm.Window.GetResultsRootAction = GetResultsRoot;
            WireDataDisplayLibraryEvents(vm);
            WireDataDisplayTreeDirty(doc);
            _factory.OpenDocument(doc);
            var lib = vm.Window.DataSourceLibrary;
            lib.RefreshAvailableDataSources();
            await lib.SelectDataSourceAsync(lib.AvailableDataSources.FirstOrDefault(s =>
                string.Equals(s.AbsolutePath, Path.GetFullPath(snp), StringComparison.OrdinalIgnoreCase))?.LogicalId
                ?? Path.GetFileName(snp));
            if (vm.Window.DataDisplay is not { } display) return;
            for (int i = 0; i < types.Count; i++)
            {
                var container = i == 0 && display.Plots.FirstOrDefault() is { } first ? first : display.AddPlot(types[i]);
                if (container.Inspector.PlotType != types[i]) container.Inspector.PlotType = types[i];
                if (container.Inspector.AddTraceCommand.CanExecute(null)) container.Inspector.AddTraceCommand.Execute(null);
            }
        }
        catch (Exception ex)
        {
            Messages.Warning($"Create Touchstone from Plot: the file was written, but no Data Display could be opened on it: {ex.Message}", snp);
        }
    }

    private void PostImageReport(RecognitionReport report, string command, string? written)
    {
        foreach (var finding in report.Findings)
        {
            bool wrote = finding.Class is RecognitionFindingClass.SchematicWritten or RecognitionFindingClass.ImageLayoutWritten;
            Messages.Post(wrote ? MessageLevel.Success : MessageLevel.Info, $"{command}: {finding.Sentence}", wrote ? written : null);
        }
    }
}
