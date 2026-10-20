using System.Linq;
using Avalonia.Controls;
using CircuitRF.Design.Modeling;
using CircuitRF.Ui.ModelComposer;

namespace CircuitRF.Ui.Views.ModelComposer;

/// <summary>
/// The network panel. The canvas's own ⌘C / ⌘V reach the view model's copy and paste; the stock menu is built from the
/// stock networks the view model lists.
/// </summary>
public partial class NetworkPanelView : UserControl
{
    public NetworkPanelView()
    {
        InitializeComponent();
        FitButton.Click += (_, _) => Canvas.ZoomToFit();
        Canvas.ClipboardCopyRequested += (_, _) => (DataContext as ModelComposerViewModel)?.CopyNetworkCommand.Execute(null);
        Canvas.ClipboardPasteRequested += (_, _) => (DataContext as ModelComposerViewModel)?.PasteNetworkCommand.Execute(null);
        StockButton.Click += (_, _) =>
        {
            if (DataContext is not ModelComposerViewModel vm) return;
            var menu = new ContextMenu
            {
                ItemsSource = vm.StockChoices.Select(title => new MenuItem
                {
                    Header = title,
                    Command = vm.ChooseStockCommand,
                    CommandParameter = title,
                }).ToList(),
            };
            menu.Open(StockButton);
        };
    }
}
