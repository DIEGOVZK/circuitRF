using System;
using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.VisualTree;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.DataDisplay.Controls;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CircuitRF.Ui.ModelComposer;

namespace CircuitRF.Ui.Views.ModelComposer;

/// <summary>
/// The plots panel. The code-behind does what only a view can: it gives each <c>PlotControl</c> its container (railRF's
/// <c>BindImpedancePlot</c> — without it a double-click adds no marker), hands the bias plane its lasso through the
/// overlay seam, follows the light/dark variant, and lights the selected tab.
/// </summary>
public partial class PlotsPanelView : UserControl
{
    private ModelComposerViewModel? _vm;

    public PlotsPanelView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Bind(DataContext as ModelComposerViewModel);
        ActualThemeVariantChanged += (_, _) => SyncTheme();
        TabStrip.LayoutUpdated += (_, _) => SyncTabs();
    }

    private void Bind(ModelComposerViewModel? vm)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
        _vm = vm;
        if (vm is null) return;
        vm.PropertyChanged += OnVmChanged;
        Host(RectPlotControl, vm.RectContainer);
        Host(SmithPlotControl, vm.SmithContainer);
        RectPlotControl.Overlay = vm.RectContainer.Overlay;
        SyncTheme();
    }

    private static void Host(PlotControl plot, PlotContainerViewModel container)
    {
        plot.NextMarkerIndexProvider = container.GetNextMarkerIndex;
        plot.FindMarkerInfoBoxVmProvider = container.FindMarkerInfoBoxVm;
        plot.ContainerProvider = () => container;
        plot.SelectedMarkersProvider = container.GetSelectedMarkers;
        plot.StepSelectedMarkersHandler = container.StepSelectedMarkers;
        plot.DoubleTapped += (_, e) =>
        {
            plot.HandleDoubleTapAt(e.GetPosition(plot));
            e.Handled = true;
        };
        plot.PlotChanged += container.OnPlotChanged;
        container.PlotNeedsRedraw += (_, _) => plot.InvalidateVisual();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm is null) return;
        if (e.PropertyName is nameof(ModelComposerViewModel.SelectedPlotTab))
        {
            RectPlotControl.Overlay = _vm.RectContainer.Overlay;
            SyncTabs();
        }
        if (e.PropertyName is nameof(ModelComposerViewModel.RectPlot)) RectPlotControl.InvalidateVisual();
        if (e.PropertyName is nameof(ModelComposerViewModel.SmithPlot)) SmithPlotControl.InvalidateVisual();
    }

    private void OnTabClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tab } && _vm is not null) _vm.SelectedPlotTab = tab;
    }

    private void SyncTabs()
    {
        if (_vm is null) return;
        foreach (var b in TabStrip.GetVisualDescendants().OfType<Button>())
            b.Classes.Set("ToolActive", b.Tag as string == _vm.SelectedPlotTab);
    }

    private void SyncTheme()
    {
        if (_vm is null) return;
        _vm.PlotHost.Theme = ActualThemeVariant == ThemeVariant.Dark ? RenderTheme.Dark : RenderTheme.Light;
        RectPlotControl.SetValue(PlotControl.PlotThemeProperty, _vm.PlotHost.Theme);
        SmithPlotControl.SetValue(PlotControl.PlotThemeProperty, _vm.PlotHost.Theme);
    }
}
