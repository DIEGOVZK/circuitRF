using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.Layout;

/// <summary>
/// One layer an open layout draws that the technology does not define — its Vis and Sel write to
/// <see cref="UndefinedLayerSession"/>, never to the technology, so toggling one neither dirties the
/// editor nor reaches the file.
/// </summary>
public sealed partial class UndefinedLayerRowViewModel : ObservableObject
{
    private readonly string _techPath;
    private readonly bool _isRefreshing;

    internal LayerKey Key { get; }

    /// <summary>The fallback palette's name, which is what the canvas and the Messages warning call it.</summary>
    public string Name { get; }

    public Avalonia.Media.Color SwatchColor { get; }

    [ObservableProperty] private bool _visible;
    [ObservableProperty] private bool _selectable;

    internal UndefinedLayerRowViewModel(string techPath, UndefinedLayerSession.Entry entry)
    {
        _techPath = techPath;
        Key = entry.Key;
        var def = FallbackPalette.For(entry.Key);
        Name = def.Name;
        SwatchColor = new(def.Color.A, def.Color.R, def.Color.G, def.Color.B);
        _isRefreshing = true;
        Visible = entry.Visible;
        Selectable = entry.Selectable;
        _isRefreshing = false;
    }

    partial void OnVisibleChanged(bool value)
    {
        if (!_isRefreshing) UndefinedLayerSession.Set(_techPath, Key, value, Selectable);
    }

    partial void OnSelectableChanged(bool value)
    {
        if (!_isRefreshing) UndefinedLayerSession.Set(_techPath, Key, Visible, value);
    }
}
