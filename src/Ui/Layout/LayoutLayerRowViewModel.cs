// One row of the layout editor's Layers list (brief-layout-layers-panel.md R-lyr-2).

using CircuitRF.Design.Layout;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.Layout;

/// <summary>
/// One drawing layer, as the layout editor's Layers list shows it — Vis and Sel boxes, a swatch and a
/// name. The look and behaviour copy railRF's board-pane list (<c>RailLayerRowViewModel</c>).
/// </summary>
/// <remarks>
/// <b>It writes through the view model, never through the technology.</b> Both boxes are questions
/// about this VIEW of the layout: <see cref="LayoutEditorViewModel"/> owns the hidden and locked sets,
/// this row asks it to change them, and is then told the answer. So a row cannot come to disagree with
/// the drawing, and nothing here can dirty the <c>.clay</c> or the <c>.ctech</c>.
/// </remarks>
public sealed partial class LayoutLayerRowViewModel : ObservableObject
{
    private readonly LayoutEditorViewModel _owner;
    private bool _isRefreshing;

    internal LayoutLayerRowViewModel(LayoutEditorViewModel owner, LayerDef layer, bool defined,
                                     bool visible, bool selectable, bool isCurrent)
    {
        _owner      = owner;
        Key         = layer.Key;
        Name        = layer.Name is { Length: > 0 } n ? n : $"L{layer.Key.Layer}/{layer.Key.Datatype}";
        SwatchColor = new Avalonia.Media.Color(255, layer.Color.R, layer.Color.G, layer.Color.B);
        IsDefined   = defined;
        _visible    = visible;
        _selectable = selectable;
        _isCurrent  = isCurrent;
    }

    /// <summary>The drawing layer this row is about.</summary>
    public LayerKey Key { get; }

    /// <summary>The technology's own name for it, or the fallback palette's spelling.</summary>
    public string Name { get; }

    /// <summary>The layer's colour, opaque — the swatch identifies the layer; it is not a preview of
    /// the fill, so it is drawn at full alpha rather than at the layer's fill opacity.</summary>
    public Avalonia.Media.Color SwatchColor { get; }

    /// <summary>False for a layer this layout's shapes use that the technology does not define — drawn
    /// with the fallback palette, and not offered as a drawing layer by the toolbar.</summary>
    public bool IsDefined { get; }

    public string Tip => IsDefined
        ? $"{Name} — click to draw on this layer"
        : $"{Name} — the technology does not define this layer, so it is drawn with a generated colour";

    /// <summary>Whether this view draws the layer.</summary>
    [ObservableProperty] private bool _visible;

    /// <summary>Whether a click, marquee, Select All or snap grab can pick a shape on it. The box is
    /// disabled while <see cref="Visible"/> is off, because a hidden layer is never pickable.</summary>
    [ObservableProperty] private bool _selectable;

    /// <summary>True for the current drawing layer — the toolbar's Layer: choice.</summary>
    [ObservableProperty] private bool _isCurrent;

    partial void OnVisibleChanged(bool value)
    {
        if (!_isRefreshing) _owner.SetLayerVisible(Key, value);
    }

    partial void OnSelectableChanged(bool value)
    {
        if (!_isRefreshing) _owner.SetLayerSelectable(Key, value);
    }

    /// <summary>Clicking the name makes this the current drawing layer.</summary>
    [RelayCommand(CanExecute = nameof(IsDefined))]
    private void MakeCurrent() => _owner.MakeLayerCurrent(Key);

    /// <summary>Sets the boxes without writing back.</summary>
    internal void Refresh(bool visible, bool selectable, bool isCurrent)
    {
        _isRefreshing = true;
        Visible    = visible;
        Selectable = selectable;
        IsCurrent  = isCurrent;
        _isRefreshing = false;
    }
}
