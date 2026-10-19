// File ▸ Import ▸ Schematic…'s dialog — brief-schimp-5 R-si5-2. The state is ImportSchematicViewModel's; this file only
// hands its preview to the schematic editor's own canvas, read-only, so it pans and zooms as the editor does, and closes
// with the import's result. Import writes; Cancel writes nothing.

using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CircuitRF.Design.Schematic.Import;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Views.Dialogs;

public partial class ImportSchematicDialog : Window
{
    private readonly ImportSchematicViewModel? _vm;
    private int _drawGeneration;

    /// <summary>For the XAML previewer only.</summary>
    public ImportSchematicDialog() => InitializeComponent();

    public ImportSchematicDialog(ImportSchematicViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        vm.PreviewChanged += () => Dispatcher.UIThread.Post(Redraw);
        Opened += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); Redraw(); };
    }

    private async void Redraw()
    {
        if (_vm is null) return;
        int gen = ++_drawGeneration;
        var drawing = _vm.Preview?.Drawing;
        PreviewPlaceholder.Text = _vm.ReadRefusal ?? (drawing is null ? (_vm.IsBusy ? "Building the preview…" : "Nothing to draw.") : "");
        if (drawing is null) { Show(null); return; }

        // Built off the UI thread, as the raster preview was; only the hand-over is on it. A rebuilt preview (a port
        // ticked, a part made a symbol cell) keeps the view the user panned to — the canvas fits only its first model.
        SchematicModel? model = await Task.Run(() =>
        {
            try { return drawing.BuildRenderModel().Model; }
            catch (Exception) { return null; }
        });
        if (gen != _drawGeneration) return;
        Show(model);
        if (model is null) PreviewPlaceholder.Text = "The preview could not be drawn.";
    }

    private void Show(SchematicModel? model)
    {
        PreviewCanvas.Model = model;
        PreviewCanvas.IsVisible = model is not null;
    }

    private void OnImport(object? sender, RoutedEventArgs e)
    {
        if (_vm is not { CanImport: true }) return;
        SchematicImportResult? result = _vm.Import();
        Close(result);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
