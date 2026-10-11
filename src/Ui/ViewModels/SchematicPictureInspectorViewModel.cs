using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Commands.Schematic;
using CircuitRF.Render;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// The Properties panel for pictures placed in a schematic — what it shows while the selection is one or more bitmaps and
/// nothing else: the file, the position of the picture's centre, its size and its opacity. Each change is one undoable
/// command through the schematic's own stack. A field the selected pictures disagree on is blank, and typing into it
/// sets all of them.
///
/// <para>Width and height keep the picture's aspect while <see cref="KeepAspect"/> is on (the default), as the canvas
/// gripper does: a picture stretched by accident is the usual way one ends up wrong. There is no rotation row because a
/// schematic bitmap is drawn unrotated, and no lock row because a locked schematic bitmap cannot be clicked at all — a
/// lock set here could not be cleared here.</para>
/// </summary>
public sealed partial class SchematicPictureInspectorViewModel : ObservableObject
{
    private SchematicViewModel? _vm;
    private bool _refreshing;
    private string? _focused;   // the field being typed in, which a refresh leaves alone

    /// <summary>True while the selection is one or more pictures and nothing else.</summary>
    [ObservableProperty] private bool _isActive;

    [ObservableProperty] private string _pathText = "";
    [ObservableProperty] private bool _pathIsBroken;
    [ObservableProperty] private string _xText = "";
    [ObservableProperty] private string _yText = "";
    [ObservableProperty] private string _widthText = "";
    [ObservableProperty] private string _heightText = "";
    [ObservableProperty] private string _opacityText = "";
    [ObservableProperty] private bool _keepAspect = true;
    [ObservableProperty] private string? _error;

    public bool HasError => Error is not null;

    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));

    /// <summary>Raised when <see cref="IsActive"/> changes, so the panel can switch what it shows.</summary>
    public event Action? ActiveChanged;

    partial void OnIsActiveChanged(bool value) => ActiveChanged?.Invoke();

    public void SetContext(SchematicViewModel? vm)
    {
        if (_vm is not null)
        {
            _vm.Selection.Changed -= OnChanged;
            _vm.EditModel.Changed -= OnChanged;
        }
        _vm = vm;
        if (_vm is not null)
        {
            _vm.Selection.Changed += OnChanged;
            _vm.EditModel.Changed += OnChanged;
        }
        Refresh();
    }

    private void OnChanged(object? sender, EventArgs e) => Refresh();

    private List<EditableBitmap> Selected()
    {
        if (_vm is null || _vm.Selection.Count == 0 || _vm.Selection.HasSelectedSegments) return [];
        var pictures = new List<EditableBitmap>();
        foreach (var id in _vm.Selection.Ids)
        {
            if (_vm.EditModel.FindCanvasObject(id) is not EditableBitmap bm) return [];
            pictures.Add(bm);
        }
        return pictures;
    }

    /// <summary>Re-reads the fields from the selected pictures.</summary>
    public void Refresh()
    {
        var pictures = Selected();
        _refreshing = true;
        try
        {
            IsActive = pictures.Count > 0;
            if (!IsActive) return;
            static string Shared(IEnumerable<double> values, Func<double, string> format)
            {
                var distinct = values.Select(v => Math.Round(v, 6)).Distinct().ToList();
                return distinct.Count == 1 ? format(distinct[0]) : "";
            }
            static string Number(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
            var paths = pictures.Select(p => p.ImagePath).Distinct().ToList();
            PathIsBroken = pictures.Count == 1 && (paths[0].Length == 0 || !System.IO.File.Exists(paths[0]));
            if (_focused != "Path") PathText = paths.Count == 1 ? paths[0] : "";
            if (_focused != "X") XText = Shared(pictures.Select(p => p.X), Number);
            if (_focused != "Y") YText = Shared(pictures.Select(p => p.Y), Number);
            if (_focused != "Width") WidthText = Shared(pictures.Select(p => p.Width), Number);
            if (_focused != "Height") HeightText = Shared(pictures.Select(p => p.Height), Number);
            if (_focused != "Opacity")
                OpacityText = Shared(pictures.Select(p => (1 - p.Transparency) * 100), v => v.ToString("0.#", CultureInfo.InvariantCulture));
        }
        finally { _refreshing = false; }
    }

    public void SetFocusedField(string? key) => _focused = key;

    /// <summary>Escape: the field shows the pictures' value again.</summary>
    public void RevertField(string key)
    {
        _focused = null;
        Error = null;
        Refresh();
    }

    /// <summary>Commits the field named by <paramref name="key"/> (the view's Tag) with <paramref name="text"/>.</summary>
    public void CommitField(string key, string text)
    {
        if (_refreshing || _vm is null) return;
        var pictures = Selected();
        if (pictures.Count == 0) return;
        text = (text ?? "").Trim();
        if (key == "Path")
        {
            if (text.Length == 0) { Refresh(); return; }
            Apply(pictures.Select(p => (IUiCommand)new SetCanvasObjectPropertyCommand<string>(
                _vm.EditModel, p, "Image Path", p.ImagePath, text, v => { BitmapCache.Invalidate(p.ImagePath); p.ImagePath = v; })), "Image Path");
            return;
        }
        if (text.Length == 0) { Refresh(); return; }   // a blank shared field left as it was changes nothing
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v))
        {
            Error = $"'{text}' is not a number.";
            return;
        }
        switch (key)
        {
            case "X":
                Apply(pictures.Select(p => (IUiCommand)new ResizeCanvasObjectCommand(_vm.EditModel, p, v, p.Y, p.Width, p.Height)), "Move");
                break;
            case "Y":
                Apply(pictures.Select(p => (IUiCommand)new ResizeCanvasObjectCommand(_vm.EditModel, p, p.X, v, p.Width, p.Height)), "Move");
                break;
            case "Width" or "Height":
                if (v <= 0) { Error = $"The {key.ToLowerInvariant()} must be greater than 0."; return; }
                Apply(pictures.Select(p =>
                {
                    double aspect = p.Width > 0 && p.Height > 0 ? p.Height / p.Width : 1;
                    var (w, h) = key == "Width"
                        ? (v, KeepAspect ? v * aspect : p.Height)
                        : (KeepAspect ? v / aspect : p.Width, v);
                    // The centre stays put, so the picture grows and shrinks about where it is.
                    return (IUiCommand)new ResizeCanvasObjectCommand(_vm.EditModel, p, p.X, p.Y, w, h);
                }), "Resize");
                break;
            case "Opacity":
                if (v < 0 || v > 100) { Error = "The opacity must be between 0 and 100 %."; return; }
                Apply(pictures.Select(p => (IUiCommand)new SetCanvasObjectPropertyCommand<double>(
                    _vm.EditModel, p, "Opacity", p.Transparency, 1 - v / 100, t => p.Transparency = t)), "Opacity");
                break;
        }
    }

    private void Apply(IEnumerable<IUiCommand> commands, string description)
    {
        Error = null;
        var list = commands.ToList();
        if (list.Count == 0) return;
        _vm!.Execute(list.Count == 1 ? list[0] : new CommandBatch(description, list));
        Refresh();
    }
}
