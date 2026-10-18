using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CircuitRF.Ui.ThreeD;

namespace CircuitRF.Ui.Views.ThreeD;

/// <summary>
/// brief-sar-11 — File ▸ Import ▸ Voxel Model…: a label volume's table and size report, then Import. Holds no state of its own:
/// <see cref="VoxelImportDialogViewModel"/> plans through AnatomyImport; the editor commits. Returns true on Import.
/// </summary>
public partial class VoxelImportDialog : Window
{
    public VoxelImportDialog() => InitializeComponent();

    public VoxelImportDialog(VoxelImportDialogViewModel vm) : this()
    {
        DataContext = vm;
        vm.CloseRequested += ok => Close(ok);
        // a file that states its own layout reads at once; a raw volume or a mesh waits for its fields
        Opened += async (_, _) => { if (!vm.NeedsLayout) await vm.ReadAsync(); };
    }

    private async void OnBrowseLabels(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not VoxelImportDialogViewModel vm) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Label list", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("CSV") { Patterns = ["*.csv", "*.txt", "*.tsv"] }, new FilePickerFileType("All Files") { Patterns = ["*.*"] }],
        });
        if (files.Count == 0) return;
        vm.LabelsCsv = files[0].Path.LocalPath;
        await vm.ReadAsync();
    }
}
