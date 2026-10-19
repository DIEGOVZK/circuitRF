using Avalonia.Controls;
using CircuitRF.Ui.ThreeD;

namespace CircuitRF.Ui.Views.ThreeD;

/// <summary>brief-sar-12 — Place Against Phantom…: holds no state of its own; <see cref="DevicePlacementDialogViewModel"/> places
/// through C3dDevicePlacement.</summary>
public partial class DevicePlacementDialog : Window
{
    public DevicePlacementDialog() => InitializeComponent();

    public DevicePlacementDialog(DevicePlacementDialogViewModel vm) : this()
    {
        DataContext = vm;
        vm.CloseRequested += ok => Close(ok);
    }
}
