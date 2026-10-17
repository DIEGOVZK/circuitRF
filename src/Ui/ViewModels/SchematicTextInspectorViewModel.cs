using System;
using CircuitRF.Ui.Commands.Schematic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// The Properties panel for a free text placed in a schematic — what it shows while the selection is one
/// text and nothing else. The rows are the symbol editor's text rows (<see cref="ITextPrimitiveFields"/>,
/// drawn by the same <c>TextPrimitiveFieldsView</c>), and each one commits as the symbol inspector's does:
/// on change, one undoable command through the schematic's own stack.
/// </summary>
public sealed partial class SchematicTextInspectorViewModel : ObservableObject, ITextPrimitiveFields
{
    private SchematicViewModel? _vm;
    private EditableText?       _text;
    private bool                _refreshing;

    /// <summary>True while the selection is one text and nothing else.</summary>
    [ObservableProperty] private bool _isActive;

    [ObservableProperty] private string           _textContent = "";
    [ObservableProperty] private double           _textAnchorX;
    [ObservableProperty] private double           _textAnchorY;
    [ObservableProperty] private double           _textFontSize;
    [ObservableProperty] private SymbolFontStyle  _textFontStyle;
    [ObservableProperty] private SymbolColorRole  _textColorRole;
    [ObservableProperty] private SymbolTextAlign  _textAlign;
    [ObservableProperty] private SymbolTextVAlign _textVAlign;
    [ObservableProperty] private SymbolRotation   _textRotation;

    /// <summary>Free text on a schematic has no instance to be turned upside down with.</summary>
    public bool ShowTextForceReadable => false;
    public bool TextForceReadable { get => false; set { } }

    /// <summary>Raised when <see cref="IsActive"/> changes, so the panel can switch what it shows.</summary>
    public event Action? ActiveChanged;

    partial void OnIsActiveChanged(bool value) => ActiveChanged?.Invoke();

    public void SetContext(SchematicViewModel? vm)
    {
        if (_vm is not null)
        {
            _vm.Selection.Changed -= OnChanged;
            _vm.EditModel.Changed -= OnChanged;
            _vm.PropertyChanged   -= OnViewModelPropertyChanged;
        }
        _vm = vm;
        if (_vm is not null)
        {
            _vm.Selection.Changed += OnChanged;
            _vm.EditModel.Changed += OnChanged;
            _vm.PropertyChanged   += OnViewModelPropertyChanged;
        }
        Refresh();
    }

    private void OnChanged(object? sender, EventArgs e) => Refresh();

    /// <summary>A move drag changes the text live and commits only on release; the anchor rows follow it.</summary>
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (IsActive && e.PropertyName == nameof(SchematicViewModel.Overlay)) Refresh();
    }

    private EditableText? Selected()
    {
        if (_vm is null || _vm.Selection.Count != 1 || _vm.Selection.HasSelectedSegments) return null;
        foreach (var id in _vm.Selection.Ids)
            return _vm.EditModel.FindCanvasObject(id) as EditableText;
        return null;
    }

    /// <summary>Re-reads the rows from the selected text.</summary>
    public void Refresh()
    {
        _refreshing = true;
        try
        {
            _text    = Selected();
            IsActive = _text is not null;
            if (_text is not { } t) return;
            TextContent   = t.Text;
            TextAnchorX   = t.X;
            TextAnchorY   = t.Y;
            TextFontSize  = t.FontSize;
            TextFontStyle = t.FontStyle;
            TextColorRole = t.ColorRole;
            TextAlign     = t.Align;
            TextVAlign    = t.VAlign;
            TextRotation  = t.Rotation;
        }
        finally { _refreshing = false; }
    }

    private void Set<T>(string name, T oldValue, T newValue, Action<EditableText, T> setter)
    {
        if (_refreshing || _vm is null || _text is not { } t || Equals(oldValue, newValue)) return;
        _vm.Execute(new SetCanvasObjectPropertyCommand<T>(_vm.EditModel, t, name, oldValue, newValue, v => setter(t, v)));
    }

    partial void OnTextContentChanged(string? oldValue, string newValue)
        => Set("Text", _text?.Text ?? "", newValue, (t, v) => t.Text = v);
    partial void OnTextAnchorXChanged(double oldValue, double newValue)
        => Set("AnchorX", _text?.X ?? 0, newValue, (t, v) => t.X = v);
    partial void OnTextAnchorYChanged(double oldValue, double newValue)
        => Set("AnchorY", _text?.Y ?? 0, newValue, (t, v) => t.Y = v);
    partial void OnTextFontSizeChanged(double oldValue, double newValue)
    {
        if (newValue > 0) Set("Font Size", _text?.FontSize ?? 0, newValue, (t, v) => t.FontSize = v);
    }
    partial void OnTextFontStyleChanged(SymbolFontStyle oldValue, SymbolFontStyle newValue)
        => Set("Font Style", oldValue, newValue, (t, v) => t.FontStyle = v);
    partial void OnTextColorRoleChanged(SymbolColorRole oldValue, SymbolColorRole newValue)
        => Set("Color Role", oldValue, newValue, (t, v) => t.ColorRole = v);
    partial void OnTextAlignChanged(SymbolTextAlign oldValue, SymbolTextAlign newValue)
        => Set("Align", oldValue, newValue, (t, v) => t.Align = v);
    partial void OnTextVAlignChanged(SymbolTextVAlign oldValue, SymbolTextVAlign newValue)
        => Set("VAlign", oldValue, newValue, (t, v) => t.VAlign = v);
    partial void OnTextRotationChanged(SymbolRotation oldValue, SymbolRotation newValue)
        => Set("Rotation", oldValue, newValue, (t, v) => t.Rotation = v);
}
