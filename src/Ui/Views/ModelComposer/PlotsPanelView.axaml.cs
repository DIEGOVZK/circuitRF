using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
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
/// <c>BindImpedancePlot</c> — without it a double-click adds no marker), keeps the containers on the plots so the markers'
/// info boxes stand beside their markers, hands the bias plane its lasso through the overlay seam, removes the selected
/// markers on Delete, commits a model-sweep box on Return, follows the light/dark variant, and lights the selected tab.
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
        LayoutUpdated += (_, _) => SyncContainers();
        KeyDown += OnKeyDown;
        AddHandler(KeyDownEvent, OnSweepKey, RoutingStrategies.Tunnel);
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
        vm.BiasPlaneLasso.MarkerAt = (x, y) => RectPlotControl.HasMarkerAt(new Avalonia.Point(x, y));
        SyncTheme();
        SyncContainers();
    }

    private void Host(PlotControl plot, PlotContainerViewModel container)
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
        plot.MarkerMoved += (_, _) => container.OnMarkerMoved();
        plot.MarkerAdded += container.OnMarkerAdded;
        plot.MarkerAdded += (marker, _) => _vm?.PlaceNewInfoBox(container, marker);
        container.PlotNeedsRedraw += (_, _) => plot.InvalidateVisual();
    }

    /// <summary>
    /// Keeps each container's rectangle on the plot showing, in the info-box layer's coordinates — the Match Designer's
    /// <c>SyncPlotContainers</c>. Without it a container keeps the seed rectangle its plot host gave it, and a new marker's
    /// box is placed at the panel's top left rather than beside its marker. Both containers take the shown plot's rectangle:
    /// the two plots share one cell. A rectangle that has not moved is not re-published.
    /// </summary>
    private void SyncContainers()
    {
        if (_vm is null) return;
        var plot = _vm.IsSmithTab ? SmithPlotControl : RectPlotControl;
        if (plot.Bounds.Width < 1 || plot.Bounds.Height < 1) return;
        if (plot.TranslatePoint(default, MarkerInfoBoxLayer) is not { } origin) return;
        foreach (var container in new[] { _vm.RectContainer, _vm.SmithContainer })
        {
            if (Math.Abs(container.Left - origin.X) < 0.5 && Math.Abs(container.Top - origin.Y) < 0.5
                && Math.Abs(container.Width - plot.Bounds.Width) < 0.5 && Math.Abs(container.Height - plot.Bounds.Height) < 0.5)
                continue;
            container.Left = origin.X;
            container.Top = origin.Y;
            container.Width = plot.Bounds.Width;
            container.Height = plot.Bounds.Height;
            container.NotifyViewProperties();
        }
    }

    /// <summary>A sweep box commits on Return as on leaving it, and Escape puts back the value it had.</summary>
    private static void OnSweepKey(object? sender, KeyEventArgs e)
    {
        if (e.Source is not TextBox box || !box.Classes.Contains("sweep")) return;
        var binding = BindingOperations.GetBindingExpressionBase(box, TextBox.TextProperty);
        if (e.Key == Key.Enter) binding?.UpdateSource();
        else if (e.Key == Key.Escape) binding?.UpdateTarget();
        else return;
        e.Handled = true;
    }

    /// <summary>Delete removes the selected markers — never from a text field, where the key is the field's.</summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key != Key.Delete || _vm is null) return;
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox) return;
        var selected = _vm.PlotHost.MarkerInfoBoxes.Where(b => b.IsSelected).ToList();
        if (selected.Count == 0) return;
        foreach (var box in selected) box.Container.RemoveMarkerWithUndo(box.Marker, box.Trace);
        e.Handled = true;
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
