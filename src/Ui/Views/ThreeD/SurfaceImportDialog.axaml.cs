using Avalonia.Controls;
using CircuitRF.Ui.ThreeD;

namespace CircuitRF.Ui.Views.ThreeD;

/// <summary>
/// brief-sar-11 — File ▸ Import ▸ Surface Meshes…: the surfaces checked, ordered outermost first and given materials, then
/// Import. Holds no state of its own: <see cref="SurfaceImportDialogViewModel"/> plans through AnatomyImport; the editor commits.
/// </summary>
public partial class SurfaceImportDialog : Window
{
    public SurfaceImportDialog() => InitializeComponent();

    public SurfaceImportDialog(SurfaceImportDialogViewModel vm) : this()
    {
        DataContext = vm;
        vm.CloseRequested += ok => Close(ok);
        Opened += async (_, _) => await vm.ReadAsync();
    }
}
