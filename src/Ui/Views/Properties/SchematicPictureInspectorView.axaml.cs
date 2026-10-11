using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Views.Properties;

/// <summary>The Properties panel for pictures placed in a schematic. Each text field is dispatched by its <c>Tag</c>:
/// LostFocus and Enter commit, Escape reverts — as <see cref="LayoutShapePropertiesView"/>.</summary>
public partial class SchematicPictureInspectorView : UserControl
{
    public SchematicPictureInspectorView() => InitializeComponent();

    private SchematicPictureInspectorViewModel? Vm => DataContext as SchematicPictureInspectorViewModel;

    private void OnFieldGotFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { Tag: string key }) Vm?.SetFocusedField(key);
    }

    private void OnFieldLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox { Tag: string key } tb) return;
        Vm?.SetFocusedField(null);
        Vm?.CommitField(key, tb.Text ?? "");
    }

    private void OnFieldKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { Tag: string key } tb) return;
        if (e.Key is Key.Enter or Key.Return) Vm?.CommitField(key, tb.Text ?? "");
        else if (e.Key == Key.Escape) Vm?.RevertField(key);
    }

    // The file picker lives in code-behind, never the view model (UI firewall).
    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is null || TopLevel.GetTopLevel(this)?.StorageProvider is not { } picker) return;
        var files = await picker.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose Picture",
            AllowMultiple = false,
            FileTypeFilter = new List<FilePickerFileType>
            {
                new("Image Files") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.webp" } },
            },
        });
        if (files.Count > 0) Vm.CommitField("Path", files[0].Path.LocalPath);
    }
}
