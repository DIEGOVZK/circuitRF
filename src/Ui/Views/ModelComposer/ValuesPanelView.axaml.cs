using Avalonia.Controls;

namespace CircuitRF.Ui.Views.ModelComposer;

/// <summary>The network values table. Every cell binds to its row, which hands each edit to the view model.</summary>
public partial class ValuesPanelView : UserControl
{
    public ValuesPanelView() => InitializeComponent();
}
