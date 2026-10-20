using Avalonia.Controls;
using CircuitRF.Ui.ModelComposer;

namespace CircuitRF.Ui.Views.ModelComposer;

/// <summary>The step rail and the selected step's settings. Everything it shows is the view model's.</summary>
public partial class StepsPanelView : UserControl
{
    public StepsPanelView()
    {
        InitializeComponent();
        ProposalList.DoubleTapped += (_, _) =>
        {
            if (DataContext is ModelComposerViewModel vm && ProposalList.SelectedItem is ProposalRow row)
                vm.ChooseTopologyCommand.Execute(row);
        };
    }
}
