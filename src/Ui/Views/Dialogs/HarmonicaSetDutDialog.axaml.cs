using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Devices.External;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// §4.3's <i>Set DUT…</i>. Every decision lives in <see cref="HarmonicaDutEditor"/> and
/// <see cref="HarmonicaDutCatalog"/>; what is here is the window, the two pickers, and the row
/// widgets — the parts a headless test cannot construct anyway.
/// </summary>
public partial class HarmonicaSetDutDialog : Window
{
    /// <summary>
    /// R7B §2 — the ONE place combo index maps to <see cref="DutKind"/>. Diode is deliberately not in
    /// here: §1 keeps the type fully supported everywhere except this offer list, so a document that
    /// already carries one gets a fourth, legacy-only item appended in the constructor instead of a
    /// fifth permanent entry nobody would otherwise choose ("we don't loadpull diodes").
    /// </summary>
    private static readonly DutKind[] KindOrder = [DutKind.Sdd, DutKind.NativeFet, DutKind.External, DutKind.Composed];
    private static readonly string[] KindLabels = ["SDD equations", "Native FET", "External model", "Model Composer (.cmodel)"];

    private HarmonicaDutEditor _editor = new(new DutSpec { Kind = DutKind.Sdd, TypeName = "SDD" });
    private ExternalDeviceDescriptor? _descriptor;
    private IReadOnlyList<HarmonicaExternalType> _types = [];
    private DutKind[] _kindItems = KindOrder;
    private bool _loading;

    public HarmonicaSetDutDialog() => InitializeComponent();

    public HarmonicaSetDutDialog(DutSpec current) : this()
    {
        _editor = new HarmonicaDutEditor(current);

        _loading = true;
        LawCombo.ItemsSource = HarmonicaDutCatalog.NativeFetLaws.Select(l => l.Display).ToList();

        // §1 — Diode is offered only as a legacy item, and only on a document that already carries
        // one: present so the state is never invisible, absent so nobody can pick it fresh.
        bool legacyDiode = current.Kind == DutKind.Diode;
        _kindItems = legacyDiode ? [.. KindOrder, DutKind.Diode] : KindOrder;
        KindCombo.ItemsSource = legacyDiode ? [.. KindLabels, "Diode (legacy)"] : KindLabels;

        int kindIdx = Array.IndexOf(_kindItems, current.Kind);
        KindCombo.SelectedIndex = kindIdx >= 0 ? kindIdx : 0;

        int law = IndexOfLaw(current.TypeName);
        LawCombo.SelectedIndex = law >= 0 ? law : 0;

        SddPorts2.IsChecked = current.SddPortCount != 3;
        SddPorts3.IsChecked = current.SddPortCount == 3;

        // A provider name is either the built-in file form or a kit name — the built-in resolver's
        // own spelling decides which, so there is no second rule for telling them apart.
        string? file = current.Provider is null ? null : VerilogAFileResolver.ModelFileIn(current.Provider);
        SourceFile.IsChecked = file is not null || current.Kind != DutKind.External;
        SourceKit.IsChecked  = current.Kind == DutKind.External && file is null && current.Provider is not null;
        ModelFileBox.Text    = file ?? "";

        RefreshKits();
        if (file is null && current.Provider is { Length: > 0 })
            KitCombo.SelectedItem = current.Provider;

        CmodelFileBox.Text = current.ModelFile ?? "";
        FingersBox.Text    = current.Fingers?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
        BuildSwitchRows();

        _loading = false;

        ApplyKindVisibility();
        RefreshExternalTypes(current.TypeName);
        RebuildParameterRows();
        RefreshMapping();
        RefreshStatus();
    }

    /// <summary>The DUT the user settled on, or null when the dialog was cancelled.</summary>
    public static async Task<DutSpec?> ShowAsync(Window owner, DutSpec current)
        => await new HarmonicaSetDutDialog(current).ShowDialog<DutSpec?>(owner);

    // ── kind ──────────────────────────────────────────────────────────────────

    private void OnKindChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || !IsInitialized || KindCombo.SelectedIndex < 0) return;

        _editor.SetKind(_kindItems[KindCombo.SelectedIndex]);

        if (_editor.Kind == DutKind.NativeFet && LawCombo.SelectedIndex >= 0)
            _editor.SetNativeLaw(HarmonicaDutCatalog.NativeFetLaws[LawCombo.SelectedIndex].TypeName);

        ApplyKindVisibility();
        if (_editor.Kind == DutKind.External) RefreshExternalTypes(_editor.TypeName);
        RebuildParameterRows();
        RefreshMapping();
        RefreshStatus();
    }

    private void ApplyKindVisibility()
    {
        bool fet = _editor.Kind == DutKind.NativeFet;
        bool ext = _editor.Kind == DutKind.External;
        bool sdd = _editor.Kind == DutKind.Sdd;
        bool cm  = _editor.Kind == DutKind.Composed;

        ComposedChooser.IsVisible = cm;
        ParamHeader.IsVisible     = !cm;
        LawLabel.IsVisible        = fet;
        LawCombo.IsVisible        = fet;
        SddChooser.IsVisible      = sdd;
        ExternalChooser.IsVisible = ext;
        MappingPanel.IsVisible    = ext;

        ParamBorder.IsVisible    = !sdd && !cm;
        SddEditorPanel.IsVisible = sdd;

        FileRow.IsVisible = ext && SourceFile.IsChecked == true;
        KitRow.IsVisible  = ext && SourceKit.IsChecked  == true;
    }

    /// <summary>R-h9c-11 — SDD2 vs SDD3.</summary>
    private void OnSddPortsChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading || !IsInitialized || _editor.Kind != DutKind.Sdd) return;
        _editor.SddPortCount = SddPorts3.IsChecked == true ? 3 : 2;
        RevalidateSddText();
    }

    private void OnLawChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || LawCombo.SelectedIndex < 0 || _editor.Kind != DutKind.NativeFet) return;
        _editor.SetNativeLaw(HarmonicaDutCatalog.NativeFetLaws[LawCombo.SelectedIndex].TypeName);
        RebuildParameterRows();
        RefreshStatus();
    }

    private static int IndexOfLaw(string typeName)
    {
        for (int i = 0; i < HarmonicaDutCatalog.NativeFetLaws.Count; i++)
            if (string.Equals(HarmonicaDutCatalog.NativeFetLaws[i].TypeName, typeName,
                              StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    // ── external: a file, or a kit ────────────────────────────────────────────

    private void OnExternalSourceChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading || !IsInitialized) return;
        ApplyKindVisibility();
        OnProviderCommitted(sender, e);
    }

    private void OnProviderCommitted(object? sender, RoutedEventArgs e)
    {
        if (_loading || _editor.Kind != DutKind.External) return;
        RefreshExternalTypes(null);
        RebuildParameterRows();
        RefreshMapping();
        RefreshStatus();
    }

    private async void OnBrowseModelFileClick(object? sender, RoutedEventArgs e)
    {
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a compiled model",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Compiled model") { Patterns = ["*.osdi"] }],
        });
        if (picked.Count == 0) return;

        ModelFileBox.Text = picked[0].Path.LocalPath;
        OnProviderCommitted(sender, e);
    }

    private async void OnAddKitFolderClick(object? sender, RoutedEventArgs e)
    {
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Add a folder holding device kits",
            AllowMultiple = false,
        });
        if (picked.Count == 0) return;

        HarmonicaDutCatalog.AddKitFolder(picked[0].Path.LocalPath);
        RefreshKits();
        OnProviderCommitted(sender, e);
    }

    private void RefreshKits()
    {
        object? keep = KitCombo.SelectedItem;
        KitCombo.ItemsSource = HarmonicaDutCatalog.KitNames();
        if (keep is not null) KitCombo.SelectedItem = keep;
    }

    private string? CurrentProvider()
    {
        if (SourceKit.IsChecked == true)
            return KitCombo.SelectedItem as string;

        string path = ModelFileBox.Text?.Trim() ?? "";
        return path.Length == 0 ? null : HarmonicaDutCatalog.ProviderForModelFile(path);
    }

    private void RefreshExternalTypes(string? preferType)
    {
        if (_editor.Kind != DutKind.External)
        {
            _types = [];
            _descriptor = null;
            TypeCombo.ItemsSource = null;
            return;
        }

        string? provider = CurrentProvider();
        _types = provider is null ? [] : HarmonicaDutCatalog.Describe(provider, out string? err);
        if (provider is null) _typeError = null;
        else _typeError = _types.Count == 0 ? DescribeError(provider) : null;

        TypeCombo.ItemsSource = _types.Select(t => t.Display).ToList();

        int idx = preferType is { Length: > 0 }
            ? _types.ToList().FindIndex(t => string.Equals(t.TypeId, preferType, StringComparison.Ordinal))
            : -1;
        TypeCombo.SelectedIndex = idx >= 0 ? idx : (_types.Count > 0 ? 0 : -1);

        CommitSelectedType(provider);
    }

    private string? _typeError;

    private string? DescribeError(string provider)
    {
        HarmonicaDutCatalog.Describe(provider, out string? err);
        return err;
    }

    private void CommitSelectedType(string? provider)
    {
        int i = TypeCombo.SelectedIndex;
        _descriptor = i >= 0 && i < _types.Count ? _types[i].Descriptor : null;
        _editor.SetExternal(provider ?? "", _descriptor);
    }

    private void OnTypeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || _editor.Kind != DutKind.External) return;
        CommitSelectedType(CurrentProvider());
        RebuildParameterRows();
        RefreshMapping();
        RefreshStatus();
    }

    // ── brief-modelcomposer-23 — a Model Composer .cmodel ─────────────────────

    private async void OnBrowseCmodelClick(object? sender, RoutedEventArgs e)
    {
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a Model Composer model",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Model Composer model") { Patterns = ["*.cmodel"] }],
        });
        if (picked.Count == 0) return;

        CmodelFileBox.Text = picked[0].Path.LocalPath;
        OnCmodelFileCommitted(sender, e);
    }

    private void OnCmodelFileCommitted(object? sender, RoutedEventArgs e)
    {
        if (_loading || _editor.Kind != DutKind.Composed) return;
        string path = CmodelFileBox.Text?.Trim().Trim('"') ?? "";
        _editor.ModelFile = path.Length == 0 ? null : System.IO.Path.GetFullPath(path);
        BuildSwitchRows();
        RefreshStatus();
    }

    private void OnFingersCommitted(object? sender, RoutedEventArgs e)
    {
        if (_loading || _editor.Kind != DutKind.Composed) return;
        string text = FingersBox.Text?.Trim() ?? "";
        _editor.Fingers = text.Length == 0 ? null
            : int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int n)
                ? n : 0;
        RefreshStatus();
    }

    /// <summary>
    /// R-mc23-7 — one checkbox per switch, from the editor's staged switches. A switch the chosen model cannot honour is
    /// disabled, with MC-21's own note as its tooltip; <c>N</c> shows the model's own <c>N0</c> while it is blank.
    /// </summary>
    private void BuildSwitchRows()
    {
        SwitchHost.Children.Clear();
        var loaded = HarmonicaDutEditor.TryLoad(_editor.ModelFile, out _);
        FingersBox.PlaceholderText = loaded is null ? "" : $"{loaded.N0}";
        string name = _editor.ModelFile is { Length: > 0 } f ? System.IO.Path.GetFileName(f) : "";

        foreach (string sw in ComposedSwitches.Names)
        {
            string? missing = loaded is null ? null : ComposedSwitches.MissingPart(sw, loaded);
            var check = new CheckBox
            {
                Content   = sw,
                IsChecked = _editor.Switches[sw],
                IsEnabled = missing is null,
            };
            ToolTip.SetTip(check, missing is null
                ? ComponentTypeRegistry.ParameterDescription(SymbolKind.ComposedFet, sw)
                : $"'{name}' {missing}; {sw}=false changes nothing.");
            check.IsCheckedChanged += (_, _) =>
            {
                if (_loading) return;
                _editor.Switches = _editor.Switches.With(sw, check.IsChecked == true);
                RefreshStatus();
            };
            SwitchHost.Children.Add(check);
        }
    }

    // ── parameters (R-h8-2 — read from the model, never a table) ──────────────

    private void RebuildParameterRows()
    {
        // R7B §3.9 — an SDD gets its own text editor instead of per-parameter rows, and the trap this
        // guards against: falling through into the loop below would call SetParameter for every row
        // it built, which for SDD would write equation keys back as if they were declared scalar
        // parameters.
        if (_editor.Kind == DutKind.Sdd)
        {
            ParamHeader.Text = "SDD equations and variables";
            RefreshSddEditor();
            return;
        }

        // brief-modelcomposer-23 — a .cmodel's file, N and switches are its own rows above; it has no others.
        if (_editor.Kind == DutKind.Composed)
        {
            ParamHost.Children.Clear();
            return;
        }

        ParamHost.Children.Clear();

        // The one reader. HarmonicaInputs.DeclaredModelParameters answers "what does this model
        // declare" for every kind, so the dialog does not get a second opinion.
        var probe = HarmonicaViewModel.DefaultModel() with { Dut = _editor.Build() };
        var declared = HarmonicaInputs.DeclaredModelParameters(probe);

        ParamHeader.Text = declared.Count == 0
            ? "This model declares no parameters here."
            : $"{declared.Count} parameter(s), as the model itself declares them";

        foreach (var input in declared)
        {
            string name = input.Key[HarmonicaInputs.ParameterPrefix.Length..];

            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("150,*,44"),
                ColumnSpacing     = 6,
            };

            var label = new TextBlock
            {
                Text = name, Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
            };
            ToolTip.SetTip(label, input.Tooltip);
            Grid.SetColumn(label, 0);

            var box = new TextBox { Text = input.Text, Tag = name };
            box.LostFocus += (_, _) => { _editor.SetParameter(name, box.Text ?? ""); RefreshStatus(); };
            Grid.SetColumn(box, 1);

            var unit = new TextBlock
            {
                Text = input.Unit, Opacity = 0.55, FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(unit, 2);

            row.Children.Add(label);
            row.Children.Add(box);
            row.Children.Add(unit);
            ParamHost.Children.Add(row);

            // Seed the editor with whatever is being shown, so a parameter the user never touches
            // still travels — a declared default that vanished because nobody clicked in the box
            // would be a device configured differently from the one on screen.
            _editor.SetParameter(name, input.Text);
        }
    }

    // ── R7B §3.9 — the SDD text editor ──────────────────────────────────────────

    /// <summary>Seeds the box from the editor's staged text (kind just switched TO SDD, or the
    /// dialog just opened on one) and revalidates.</summary>
    private void RefreshSddEditor()
    {
        _loading = true;
        SddTextBox.Text = _editor.SddText ?? "";
        _loading = false;
        RevalidateSddText();
    }

    private void OnSddTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_loading || _editor.Kind != DutKind.Sdd) return;
        _editor.SddText = SddTextBox.Text ?? "";
        RevalidateSddText();
    }

    /// <summary>
    /// §3.2/§3.6 — re-parses the box on every keystroke (cheap: <c>Parser.Parse</c> on a handful of
    /// short expressions), updates the status line's counts and the per-line error list, and drives
    /// the SAME "can I commit" path every other kind uses (<see cref="RefreshStatus"/>).
    /// </summary>
    private void RevalidateSddText()
    {
        var parsed = HarmonicaSddText.Parse(_editor.SddText ?? "", _editor.SddPortCount);

        SddStatusLabel.Text =
            $"{parsed.Variables.Count} variable(s) · {parsed.Equations.Count} equation(s) · " +
            $"{_editor.SddPortCount} port(s)";

        SddErrorsLabel.Text = string.Join('\n', parsed.Problems.Select(p =>
            p.Line > 0 ? $"line {p.Line}: {p.Message}" : p.Message));

        RefreshStatus();
    }

    // ── §4.5.5 — the mapping, offered from the model's OWN node names ─────────

    private void RefreshMapping()
    {
        var choices = HarmonicaDutEditor.NodeChoices(_descriptor);
        var withNone = new List<string> { "(not named)" };
        withNone.AddRange(choices);

        _loading = true;
        foreach (var (combo, value) in new (ComboBox, string?)[]
                 {
                     (GateCombo,   _editor.GateNode),
                     (DrainCombo,  _editor.DrainNode),
                     (SourceCombo, _editor.SourcePin),
                 })
        {
            combo.ItemsSource = withNone;
            int i = value is { Length: > 0 } ? withNone.IndexOf(value) : 0;
            combo.SelectedIndex = i >= 0 ? i : 0;
            combo.IsEnabled = choices.Count > 0;
        }
        _loading = false;
    }

    private void OnMappingChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _editor.GateNode  = Selected(GateCombo);
        _editor.DrainNode = Selected(DrainCombo);
        _editor.SourcePin = Selected(SourceCombo);
        RefreshStatus();

        static string? Selected(ComboBox c)
            => c.SelectedIndex <= 0 ? null : c.SelectedItem as string;
    }

    // ── status + commit ───────────────────────────────────────────────────────

    private void RefreshStatus()
    {
        string? problem = _typeError ?? _editor.Validate();

        if (problem is null && _editor.Kind == DutKind.External
            && _editor.Build().IntrinsicMapping is null)
            problem = "The intrinsic plane is not named: the glyphs and the loadline will be empty.";

        // §1 — Diode is kept fully working for a document that already has one, but is no longer
        // offered for a new device; say so, since the combo item alone ("(legacy)") is easy to miss.
        if (problem is null && _editor.Kind == DutKind.Diode)
            problem = "Diode is kept for documents that already use it and is no longer offered for " +
                      "a new device — switch to another kind to move off it.";

        // R-mc23-7 — a model with a part switched off says so before it is set.
        if (problem is null && _editor.Kind == DutKind.Composed && !_editor.Switches.IsDefault)
            problem = _editor.Switches.Describe();

        StatusLabel.Text = problem ?? "";
        ApplyButton.IsEnabled = _typeError is null && _editor.Validate() is null;
    }

    private void OnApplyClick(object? sender, RoutedEventArgs e)
    {
        if (_editor.Validate() is not null) return;
        Close(_editor.Build());
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
