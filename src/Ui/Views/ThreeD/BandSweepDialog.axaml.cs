using Avalonia.Controls;
using CircuitRF.Ui.ThreeD;

namespace CircuitRF.Ui.Views.ThreeD;

/// <summary>brief-sar-12 — Band Sweep…: holds no state of its own; <see cref="BandSweepDialogViewModel"/> writes and reads through
/// C3dBandSweeps.</summary>
public partial class BandSweepDialog : Window
{
    public BandSweepDialog() => InitializeComponent();

    public BandSweepDialog(BandSweepDialogViewModel vm) : this()
    {
        DataContext = vm;
        vm.CloseRequested += Close;
    }
}
