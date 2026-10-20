using Avalonia.Controls;
using CircuitRF.Ui.ModelComposer;

namespace CircuitRF.Ui.Views.ModelComposer;

/// <summary>The Model Validation Report dialog. Its options, runs and export are the view model's; this only closes it.</summary>
public partial class ModelReportDialog : Window
{
    public ModelReportDialog() => InitializeComponent();

    public ModelReportDialog(ModelReportViewModel vm) : this()
    {
        DataContext = vm;
        CloseButton.Click += (_, _) => Close();
    }
}
