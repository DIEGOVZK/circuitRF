// The Pads and Network steps (brief-modelcomposer-14 R-mc14-7, R-mc14-8; overview D5, D9): the pad mode and files, the
// network — stock or the user's own, opened, drawn or pasted — its values table and its fit method. Every change is an
// edit through CmcExtrinsic's setters (one undo step); installing a network is ModelCommands' (D15).
//
// The network panel is the schematic canvas drawing the network's own .csch. Copy and paste go through the platform
// clipboard as a schematic selection, on the Smith Chart's seams: the window supplies the two delegates that touch the
// clipboard, so the decision each makes is drivable with no display.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Design.Modeling;
using CircuitRF.Design.Schematic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ModelComposer;

public sealed partial class ModelComposerViewModel
{
    // ── the seams the window fills ───────────────────────────────────────────

    /// <summary>Asks for a file to open: a title and its patterns; null when cancelled.</summary>
    public Func<string, IReadOnlyList<string>, Task<string?>>? PickFile { get; set; }

    /// <summary>Puts a schematic selection on the clipboard — the window's one call to <c>SchematicClipboard.CopyAsync</c>.</summary>
    public Func<SchematicEditModel, Task>? NetworkCopySink { get; set; }

    /// <summary>Reads a schematic selection off the clipboard — the window's one call to <c>SchematicClipboard.PasteAsync</c>.
    /// Null, or a null answer, is nothing of ours there.</summary>
    /// A network's connectivity is often carried by its net labels, so they travel with the parts and the wires.
    public Func<Task<(IReadOnlyList<EditableComponent> Components, IReadOnlyList<EditableWire> Wires,
                      IReadOnlyList<EditableNetLabel> NetLabels)?>>? NetworkPasteSource { get; set; }

    /// <summary>Opens a <c>.csch</c> in the workspace's schematic editor — Edit on the network panel.</summary>
    public Action<string>? OpenSchematic { get; set; }

    // ── pads (R-mc14-7) ──────────────────────────────────────────────────────

    public IReadOnlyList<PadsKind> PadsKinds { get; } = Enum.GetValues<PadsKind>();

    private PadsSettings _pads = PadsSettings.None;
    private bool _loadingExtrinsic;

    public PadsKind PadsKind
    {
        get => _pads.Kind;
        set { if (value != _pads.Kind) SetPads(_pads with { Kind = value }, $"Pads: {value}"); }
    }

    public bool PadsOpenShort => _pads.Kind == PadsKind.OpenShort;
    public bool PadsTwoPorts => _pads.Kind is PadsKind.TwoPorts or PadsKind.Network;
    public string PadsOpen => _pads.Open ?? "";
    public string PadsShort => _pads.Short ?? "";
    public string PadsGate => _pads.Gate ?? "";
    public string PadsDrain => _pads.Drain ?? "";

    private bool SetPads(PadsSettings next, string label) =>
        !_loadingExtrinsic && EditPart(label, CmcExtrinsic.Pads, CmcExtrinsic.With, next);

    /// <summary>Browses for one pad file — <c>open</c>, <c>short</c>, <c>gate</c> or <c>drain</c>.</summary>
    [RelayCommand]
    private async Task PickPadFile(string which)
    {
        if (PickFile is null || DocumentFolder is not { } dir)
        {
            if (DocumentFolder is null) Warn("Save the document first; its files are found relative to it.");
            return;
        }
        string[] patterns = _pads.Kind == PadsKind.Network ? ["*.csch"] : ["*.s2p"];
        if (await PickFile($"Pads — {which}", patterns) is not { } file) return;
        string rel = Path.GetRelativePath(dir, file).Replace('\\', '/');
        var next = which switch
        {
            "open" => _pads with { Open = rel },
            "short" => _pads with { Short = rel },
            "gate" => _pads with { Gate = rel },
            _ => _pads with { Drain = rel },
        };
        SetPads(next, $"Pads: {which} {rel}");
    }

    // ── the network (R-mc14-8) ───────────────────────────────────────────────

    /// <summary>The network choices: none, then the stock networks.</summary>
    public IReadOnlyList<string> StockChoices { get; } = ["None", .. StockNetworks.All.Select(s => s.Title)];

    private NetworkSettings _network = NetworkSettings.None;

    /// <summary>The network's file name, or empty with no network.</summary>
    public string NetworkName => _network.Schematic ?? "";

    /// <summary>The one-line refusal of a network that is not a valid six-pin network; empty otherwise.</summary>
    [ObservableProperty] private string _networkRefusal = "";

    /// <summary>The network drawn as the schematic canvas draws it, or null.</summary>
    [ObservableProperty] private SchematicModel? _networkDrawing;

    private SixPinNetwork? _six;
    private (string Path, DateTime Stamp)? _sixKey;

    [RelayCommand]
    private async Task ChooseStock(string title)
    {
        if (!IsActive) return;
        if (DocumentPath is null && !await SaveAsync(saveAs: false)) return;
        string? id = StockNetworks.All.FirstOrDefault(s => s.Title == title)?.Id;
        try
        {
            var chosen = ModelCommands.ChooseNetwork(Document, DocumentPath!, id);
            EditPart(id is null ? "Network: none" : $"Network: {title}", d => d.Network, (d, n) => d with { Network = n }, chosen.Network);
        }
        catch (ModelingException ex) { Error(ex.Message); }
    }

    [RelayCommand]
    private async Task OpenNetwork()
    {
        if (!IsActive || PickFile is null) return;
        if (DocumentPath is null && !await SaveAsync(saveAs: false)) return;
        if (await PickFile("Extrinsic network", ["*.csch"]) is not { } file) return;
        try
        {
            var next = ModelCommands.UseNetworkFile(Document, DocumentPath!, file);
            EditPart($"Network: {Path.GetFileName(file)}", d => d.Network, (d, n) => d with { Network = n }, next.Network);
        }
        catch (ModelingException ex) { Error(ex.Message); }
    }

    /// <summary>Opens the network's <c>.csch</c> in the schematic editor, where it is drawn and edited like any schematic.</summary>
    [RelayCommand]
    private void EditNetwork()
    {
        if (NetworkPath() is { } p) OpenSchematic?.Invoke(p);
    }

    private string? NetworkPath() =>
        _network.Schematic is { } s && DocumentFolder is { } dir ? DataImport.ResolveSource(dir, s) : null;

    /// <summary>
    /// Pastes a schematic selection as the network: one with the six pins is written beside the document and installed (one
    /// undo step); one without them is the refusal naming the pins it found, and nothing is written.
    /// </summary>
    [RelayCommand]
    public async Task<bool> PasteNetwork()
    {
        if (NetworkPasteSource is null) return false;
        if (await NetworkPasteSource() is not { } pasted || pasted.Components.Count == 0)
        {
            Warn("The clipboard holds no schematic selection.");
            return false;
        }
        if (DocumentPath is null && !await SaveAsync(saveAs: false)) return false;
        var model = new SchematicEditModel();
        model.Components.AddRange(pasted.Components);
        model.Wires.AddRange(pasted.Wires);
        model.NetLabels.AddRange(pasted.NetLabels);
        string json = SchematicPersistence.Serialize(model, IsPassive ? "topology" : "network");
        if (IsPassive) return InstallTopology(json);
        try
        {
            var next = ModelCommands.InstallNetwork(Document, DocumentPath!, "network", json);
            return EditPart($"Network pasted: {CmcExtrinsic.Network(next).Schematic}", d => d.Network, (d, n) => d with { Network = n }, next.Network);
        }
        catch (ModelingException ex)
        {
            Error(ex.Message);
            return false;
        }
    }

    /// <summary>Copies the network as a schematic selection, which pastes into any schematic.</summary>
    [RelayCommand]
    public async Task<bool> CopyNetwork()
    {
        if (IsPassive) return await CopyTopology();
        if (NetworkPath() is not { } p || !File.Exists(p))
        {
            Warn("There is no network to copy.");
            return false;
        }
        var model = SchematicPersistence.LoadFromFile(p).model;
        if (NetworkCopySink is not null) await NetworkCopySink(model);
        Info($"Copied {Path.GetFileName(p)}.");
        return true;
    }

    // ── the values table ─────────────────────────────────────────────────────

    public ObservableCollection<NetworkValueRow> NetworkValues { get; } = [];

    public IReadOnlyList<string> ScalingRules { get; } = PeripheryScaling.Known;

    // ── the fit method ───────────────────────────────────────────────────────

    public IReadOnlyList<string> NetworkMethods { get; } = ["ColdFet", "MultiBias", "ColdFetThenMultiBias", "Fixed"];

    public string NetworkMethod
    {
        get => _network.Variables.Count > 0 && _network.Variables.Values.All(v => v.Lock) ? "Fixed"
             : (_network.Fit ?? NetworkFitSettings.Default).Method.ToString();
        set
        {
            if (_loadingExtrinsic || value == NetworkMethod) return;
            if (value == "Fixed")
            {
                SetNetwork(_network with { Variables = _network.Variables.ToDictionary(kv => kv.Key, kv => kv.Value with { Lock = true }, StringComparer.Ordinal) },
                           "Network: every value held");
                return;
            }
            var fit = (_network.Fit ?? NetworkFitSettings.Default) with { Method = Enum.Parse<NetworkFitMethod>(value) };
            var vars = NetworkMethod == "Fixed"
                ? _network.Variables.ToDictionary(kv => kv.Key, kv => kv.Value with { Lock = false }, StringComparer.Ordinal)
                : _network.Variables;
            SetNetwork(_network with { Fit = fit, Variables = vars }, $"Network method: {value}");
        }
    }

    private NetworkFitSettings Fit => _network.Fit ?? NetworkFitSettings.Default;

    public string NetworkSeed
    {
        get => Fit.Seed.ToString(CultureInfo.InvariantCulture);
        set
        {
            if (_loadingExtrinsic) return;
            if (!ulong.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out ulong s)) { Error($"Seed: '{value}' is not a whole number."); RefreshExtrinsic(); return; }
            if (s != Fit.Seed) SetNetwork(_network with { Fit = Fit with { Seed = s } }, $"Network seed: {s}");
        }
    }

    public string WeightFlatness { get => ModelValueFormat.G(Fit.Weights.Flatness); set => SetWeight(value, w => Fit.Weights with { Flatness = w }, "flatness"); }
    public string WeightCold { get => ModelValueFormat.G(Fit.Weights.Cold); set => SetWeight(value, w => Fit.Weights with { Cold = w }, "cold"); }
    public string WeightSmallSignal { get => ModelValueFormat.G(Fit.Weights.SmallSignal); set => SetWeight(value, w => Fit.Weights with { SmallSignal = w }, "small signal"); }

    private void SetWeight(string text, Func<double, NetworkFitWeights> with, string name)
    {
        if (_loadingExtrinsic) return;
        if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double w) || w < 0)
        {
            Error($"Weight {name}: '{text}' is not a number of 0 or more.");
            RefreshExtrinsic();
            return;
        }
        var next = with(w);
        if (next != Fit.Weights) SetNetwork(_network with { Fit = Fit with { Weights = next } }, $"Network weight {name}: {ModelValueFormat.G(w)}");
    }

    /// <summary>The multi-bias fit's bias points, as the bias plane marks them.</summary>
    public IReadOnlyList<PointRef> FitPoints => Fit.Active;

    public string FitPointsText => Fit.Active.Count == 0 ? "all S points" : $"{Fit.Active.Count} point(s)";

    /// <summary>Adds the points to the multi-bias fit's set — or, when all are in it, takes them out. One undo step.</summary>
    public bool ToggleFitPoints(IReadOnlyList<(string Set, string Point)> points)
    {
        if (points.Count == 0) return false;
        var refs = points.Select(p => new PointRef(p.Set, p.Point)).ToList();
        bool allIn = refs.All(r => Fit.Active.Contains(r));
        var next = allIn ? Fit.Active.Where(r => !refs.Contains(r)).ToList() : Fit.Active.Concat(refs.Where(r => !Fit.Active.Contains(r))).ToList();
        return SetNetwork(_network with { Fit = Fit with { Active = next } },
                          allIn ? $"Fit points: {refs.Count} removed" : $"Fit points: {refs.Count} added");
    }

    private bool SetNetwork(NetworkSettings next, string label) =>
        EditPart(label, CmcExtrinsic.Network, CmcExtrinsic.With, next);

    // ── projection ───────────────────────────────────────────────────────────

    private void RefreshExtrinsic()
    {
        _loadingExtrinsic = true;
        try
        {
            if (IsActive)
            {
                try { _pads = CmcExtrinsic.Pads(Document); } catch (CmcFormatException) { _pads = PadsSettings.None; }
                try { _network = CmcExtrinsic.Network(Document); } catch (CmcFormatException) { _network = NetworkSettings.None; }
            }
            else
            {
                _pads = PadsSettings.None;
                _network = NetworkSettings.None;
            }
            LoadNetworkFile();
            RefreshValueRows();
            foreach (string n in new[]
                     {
                         nameof(PadsKind), nameof(PadsOpenShort), nameof(PadsTwoPorts), nameof(PadsOpen), nameof(PadsShort),
                         nameof(PadsGate), nameof(PadsDrain), nameof(NetworkName), nameof(NetworkMethod), nameof(NetworkSeed),
                         nameof(WeightFlatness), nameof(WeightCold), nameof(WeightSmallSignal), nameof(FitPoints), nameof(FitPointsText),
                     })
                OnPropertyChanged(n);
        }
        finally { _loadingExtrinsic = false; }
    }

    /// <summary>Reads the network's <c>.csch</c> — once per file version — into the canvas's drawing and the six-pin check.</summary>
    private void LoadNetworkFile()
    {
        if (NetworkPath() is not { } path)
        {
            _six = null;
            _sixKey = null;
            NetworkDrawing = null;
            NetworkRefusal = "";
            return;
        }
        var stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        if (_sixKey is { } k && k.Path == path && k.Stamp == stamp) return;
        _sixKey = (path, stamp);
        try
        {
            _six = SixPinNetwork.Load(path);
            NetworkRefusal = "";
        }
        catch (ModelingException ex)
        {
            _six = null;
            NetworkRefusal = ex.Message;
        }
        try { NetworkDrawing = File.Exists(path) ? SchematicPersistence.LoadFromFile(path).model.BuildRenderModel().Model : null; }
        catch (Exception) { NetworkDrawing = null; }
    }

    private void RefreshValueRows()
    {
        var vars = _six?.Variables ?? [];
        if (!vars.Select(v => v.Name).SequenceEqual(NetworkValues.Select(r => r.Name)))
        {
            NetworkValues.Clear();
            foreach (var v in vars) NetworkValues.Add(new NetworkValueRow(v.Name, v.Kind, CommitValueCell));
        }
        var result = _network.Result;
        foreach (var row in NetworkValues)
        {
            var v = vars.First(x => x.Name == row.Name);
            var s = Settings(v);
            string unit = row.Unit;
            string error = "", tip = "";
            bool poor = false;
            if (result?.Variables.TryGetValue(row.Name, out var fitted) == true && !s.Lock && s.Tie is null)
            {
                error = "±" + ModelValueFormat.Eng(fitted.StdError, unit);
                if (fitted.Spread > 0) tip = $"Spread across the starts: {ModelValueFormat.Eng(fitted.Spread, unit)}.";
                double value = s.Value ?? v.Value;
                poor = value != 0 && fitted.StdError / Math.Abs(value) > Fit.PoorRelativeError;
                if (fitted.AtBound is { } b) { poor = true; tip = $"At its {b.ToLowerInvariant()} bound."; }
            }
            if (result?.Report.FirstOrDefault(l => l.StartsWith(row.Name + " ", StringComparison.Ordinal)) is { } line)
            {
                poor = true;
                tip = line;
            }
            row.Load(ModelValueFormat.Eng(s.Value ?? v.Value, unit), s.Lock,
                     s.Min is { } mn ? ModelValueFormat.Eng(mn, unit) : "",
                     s.Max is { } mx ? ModelValueFormat.Eng(mx, unit) : "",
                     s.Tie is { } t ? (t.Factor == 1 ? t.To : $"{t.To} × {ModelValueFormat.G(t.Factor)}") : "",
                     s.Scaling ?? PeripheryScaling.Fixed, error, poor, tip);
        }
    }

    private NetworkVariableSettings Settings(NetworkVariable v) =>
        _network.Variables.TryGetValue(v.Name, out var s) ? s : SixPinNetwork.DefaultFor(v);

    /// <summary>A values-table cell's edit: one undo step, or the refusal and the cell put back.</summary>
    private void CommitValueCell(NetworkValueRow row, string field, string text)
    {
        if (_loadingExtrinsic || _six?.Variables.FirstOrDefault(v => v.Name == row.Name) is not { } v) return;
        var s = Settings(v);
        string unit = row.Unit;
        NetworkVariableSettings next;
        switch (field)
        {
            case nameof(NetworkValueRow.Value):
                if (!ModelValueFormat.TryParse(text, unit, out double value)) { Refuse($"{row.Name}: '{text}' is not a value in {unit}."); return; }
                next = s with { Value = value };
                break;
            case nameof(NetworkValueRow.Lock):
                next = s with { Lock = text == "true" };
                break;
            case nameof(NetworkValueRow.Min) or nameof(NetworkValueRow.Max):
                double? bound = null;
                if (text.Trim().Length > 0)
                {
                    if (!ModelValueFormat.TryParse(text, unit, out double b)) { Refuse($"{row.Name}: '{text}' is not a value in {unit}."); return; }
                    bound = b;
                }
                next = field == nameof(NetworkValueRow.Min) ? s with { Min = bound } : s with { Max = bound };
                break;
            case nameof(NetworkValueRow.Tie):
                if (!TryTie(text, out var tie)) { Refuse($"{row.Name}: '{text}' is not a tie; write another value's name, or name × factor."); return; }
                next = s with { Tie = tie };
                break;
            default:
                next = s with { Scaling = text };
                break;
        }
        if (next == s) return;
        var vars = new Dictionary<string, NetworkVariableSettings>(_network.Variables, StringComparer.Ordinal) { [row.Name] = next };
        SetNetwork(_network with { Variables = vars }, $"{row.Name}: {field.ToLowerInvariant()} {text}".TrimEnd());

        void Refuse(string line)
        {
            Error(line);
            RefreshExtrinsic();
        }
    }

    private static bool TryTie(string text, out VariableTie? tie)
    {
        tie = null;
        string t = text.Trim();
        if (t.Length == 0) return true;
        var parts = t.Split(['×', '*'], 2, StringSplitOptions.TrimEntries);
        if (parts[0].Length == 0 || !parts[0].All(c => char.IsLetterOrDigit(c) || c == '_')) return false;
        double factor = 1;
        if (parts.Length == 2 && !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out factor)) return false;
        tie = new VariableTie(parts[0], factor);
        return true;
    }
}
