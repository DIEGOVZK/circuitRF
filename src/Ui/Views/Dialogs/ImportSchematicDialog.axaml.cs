// File ▸ Import ▸ Schematic…'s dialog — brief-schimp-5 R-si5-2. The state is ImportSchematicViewModel's; this file only
// draws its preview, off the UI thread as the component chooser draws its own (ComponentPreviewRaster), and closes with
// the import's result. Import writes; Cancel writes nothing.

using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using CircuitRF.Design.Schematic.Import;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Theming;
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
        PreviewPane.SizeChanged += (_, _) => Redraw();
        Opened += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); Redraw(); };
        Closed += (_, _) => (PreviewImage.Source as IDisposable)?.Dispose();
    }

    private async void Redraw()
    {
        if (_vm is null) return;
        int gen = ++_drawGeneration;
        var drawing = _vm.Preview?.Drawing;
        PreviewPlaceholder.Text = _vm.ReadRefusal ?? (drawing is null ? (_vm.IsBusy ? "Building the preview…" : "Nothing to draw.") : "");
        if (drawing is null) { SetImage(null); return; }
        if (PreviewPane.Bounds.Width < 1) return;

        // Read on the UI thread and handed over by value: a worker may not touch bounds or the theme service.
        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        int w = (int)Math.Round(PreviewPane.Bounds.Width * scaling), h = (int)Math.Round(PreviewPane.Bounds.Height * scaling);
        var variant = ActualThemeVariant == ThemeVariant.Dark ? ColorVariant.Dark : ColorVariant.Light;
        var theme = SchematicRenderTheme.FromTheme(ThemeService.Active, variant);
        Bitmap? bitmap = await Task.Run(() =>
        {
            try { return ComponentPreviewRenderer.RenderSchematic(drawing, theme, w, h); }
            catch (Exception) { return null; }
        });
        if (gen != _drawGeneration) { bitmap?.Dispose(); return; }
        SetImage(bitmap);
        if (bitmap is null) PreviewPlaceholder.Text = "The preview could not be drawn.";
    }

    private void SetImage(Bitmap? bitmap)
    {
        var old = PreviewImage.Source as IDisposable;
        PreviewImage.Source = bitmap;
        old?.Dispose();
    }

    private void OnImport(object? sender, RoutedEventArgs e)
    {
        if (_vm is not { CanImport: true }) return;
        SchematicImportResult? result = _vm.Import();
        Close(result);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
