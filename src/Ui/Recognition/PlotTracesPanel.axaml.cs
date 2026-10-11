using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace CircuitRF.Ui.Recognition;

/// <summary>The plot mode's right column (brief-img-14 R-im14-12). Everything it shows is PlotSessionViewModel's.</summary>
public partial class PlotTracesPanel : UserControl
{
    private PlotSessionViewModel? _vm;

    public PlotTracesPanel()
    {
        InitializeComponent();
        // A press anywhere on a row selects its trace — on its combos and text too, which handle the press themselves
        // (R-ptu3-2). Tunnelling, so it is heard before they take it.
        TracesList.AddHandler(PointerPressedEvent, OnRowPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        GroupsList.AddHandler(PointerPressedEvent, OnRowPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
        _vm = DataContext as PlotSessionViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnVmChanged;
    }

    private void OnRowPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_vm is null) return;
        switch ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext)
        {
            case PlotTraceRowViewModel row: _vm.SelectedTrace = row; break;
            case PlotTraceGroupViewModel group: _vm.SelectedGroup = group; break;
        }
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Posted: chosen on the canvas after a re-read, the row's container is not made yet.
        if (e.PropertyName is nameof(PlotSessionViewModel.SelectedTrace) or nameof(PlotSessionViewModel.Detailed))
            Dispatcher.UIThread.Post(BringSelectedIntoView, DispatcherPriority.Loaded);
    }

    /// <summary>Scrolls the column just enough to show the selected row (R-ptu3-1).</summary>
    public void BringSelectedIntoView()
    {
        if (_vm is null) return;
        Control? item = _vm.Detailed
            ? _vm.SelectedTrace is { } row ? TracesList.ContainerFromItem(row) : null
            : _vm.SelectedGroup is { } group ? GroupsList.ContainerFromItem(group) : null;
        if (item is null
            || Scroller.Content is not Visual content || item.TranslatePoint(default, content) is not { } at) return;
        double top = at.Y, bottom = at.Y + item.Bounds.Height;
        double view = Scroller.Viewport.Height, offset = Scroller.Offset.Y;
        if (view <= 0) return;
        double to = top < offset || bottom - top > view ? top : bottom > offset + view ? bottom - view : offset;
        if (to != offset) Scroller.Offset = new Vector(Scroller.Offset.X, to);
    }
}
