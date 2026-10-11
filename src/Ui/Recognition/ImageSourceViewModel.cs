// Create Schematic / Layout from Image — the picture source. brief-img-5-dialog.md R-im5-1 … R-im5-10; overview §4, D3,
// D5, D6, D7, D8, D14, D16.
//
// The body below the source — target, options, parts table, report, Create — is RecognitionSessionViewModel's, shared
// with the artwork source. What is here is what a picture adds: the picture itself (drop, paste, browse), what kind of
// drawing it is and what to make of it, the technology, the scale (ImageScaleViewModel), the colour → layer rows
// (ImageLayerRowViewModel), the advanced options, and the overlay the canvas draws.
//
// ── IT DECIDES NOTHING ───────────────────────────────────────────────────────────────────────────────
//
// Every control is a field of a record the CLI fills (ImageTraceInput, ImageTraceOptions, ImageScaleStatement,
// ImageLayerMap, the targets), and both surfaces hand them to the same functions through IImageRecognitionRunner. The
// overlay is rebuilt only from what a reading returned; the canvas draws it and never reads the picture itself.
//
// ── FAILURE IS SHOWN ON THE PICTURE (D16) ────────────────────────────────────────────────────────────
//
// A picture with no drawing in it, or a reading refused after it was made, is never a message box: the canvas shows the
// picture dimmed with the one sentence, the control that answers it is highlighted, and Create is disabled with that
// sentence as its tooltip.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Recognition;
using CircuitRF.Design.Layout.Recognition.Image;
using CircuitRF.Design.Layout.Recognition.Silkscreen;
using CircuitRF.Design.Schematic.Recognition;
using CircuitRF.Engine;

namespace CircuitRF.Ui.Recognition;

/// <summary>A technology the picture dialog offers.</summary>
public sealed record ImageTechnologyChoice(string Name, string Path)
{
    public override string ToString() => Name;
}

/// <summary>What one picture reading returned, for the source to draw.</summary>
internal sealed record ImagePreviewDetail(
    ImageKindResult Kind, Technology? Technology, string? TechnologyPath, ImageTraceResult? Trace,
    RecognitionResult? Recognition, ImageOverlay Overlay, IReadOnlyList<ImageTracePreset> MatchingPresets,
    SchematicImageResult? Schematic = null, BoardPhotoReading? Photo = null);

/// <summary>The dialog, on a picture source.</summary>
public sealed partial class ImageSourceViewModel : RecognitionSessionViewModel
{
    /// <summary>R-im5-2's empty-state line.</summary>
    public const string DropPrompt = "Drop a picture here, paste it (Cmd/Ctrl+V), or Browse…";

    /// <summary>D3's one line: a schematic picture does not make a layout directly.</summary>
    public const string MakeLayoutFromSchematicTip = "Create the schematic, then use Update Layout from Schematic";

    private readonly IImageRecognitionRunner _runner;
    private readonly string _workspaceDir;
    private readonly string? _presetDirectory;
    private readonly Dictionary<string, Technology?> _technologies = new(StringComparer.Ordinal);
    private readonly List<int> _editedColours = [];
    private ImageLayerMap? _editedMap;
    private ImageTraceResult? _trace;
    private Technology? _technology;

    /// <summary>The photo read square on the scale row's corners (IM-13), once read; null while the photo is read as taken.</summary>
    private BoardPhotoReading? _photo;
    private string? _defaultName;
    private string? _declinedPreset;
    private bool _syncingSelection;
    private int _wantedMake;
    private bool _refreshingMode;

    /// <param name="workspaceDir">Where a new cell is made: the workspace's folder.</param>
    /// <param name="technologies">The workspace's technologies (R-im5-6).</param>
    /// <param name="defaultTechnology">The one chosen first — the workspace default, or the layout a placed picture sits in.</param>
    /// <param name="source">The picture, or null: the dialog opens empty, as a drop zone.</param>
    /// <param name="makeSchematic">Make preset: a schematic (true) or a layout.</param>
    /// <param name="options">The trace options to start from — the D5 setting in <see cref="ImageTraceOptions.KeepUnderlay"/>.</param>
    /// <param name="presetDirectory">Where presets are kept; null is the user's own.</param>
    public ImageSourceViewModel(
        string workspaceDir, IReadOnlyList<ImageTechnologyChoice> technologies, int defaultTechnology,
        ImageSource? source, bool makeSchematic,
        IImageRecognitionRunner? runner = null, Action<Action>? post = null, TimeSpan? debounce = null,
        ImageTraceOptions? options = null, string? presetDirectory = null)
        : base(null, post, debounce)
    {
        _workspaceDir = Path.GetFullPath(workspaceDir);
        _runner = runner ?? ImageRecognitionRunner.Instance;
        _presetDirectory = presetDirectory;
        TechnologyChoices = technologies ?? [];
        _technologyIndex = TechnologyChoices.Count == 0 ? -1 : Math.Clamp(defaultTechnology, 0, TechnologyChoices.Count - 1);
        _makeIndex = makeSchematic ? 0 : 1;
        _wantedMake = _makeIndex;

        var o = options ?? new ImageTraceOptions();
        _simplifyText = Num(o.SimplifyPx);
        _minFeatureText = Num(o.MinFeaturePx2);
        _snap = o.Snap;
        _snap45 = o.Snap45;
        _snapAngleText = Num(o.SnapAngleDeg);
        _maxColoursText = o.MaxColours.ToString(CultureInfo.InvariantCulture);
        _mergeText = Num(o.MergeDeltaE);
        _edgeStrokeText = Num(o.EdgeStrokePx);
        _keepUnderlay = o.KeepUnderlay;
        var lineArt = new LineArtOptions();
        _thresholdText = Num(lineArt.K);
        _windowText = lineArt.Window.ToString(CultureInfo.InvariantCulture);

        Scale.Changed += ScheduleRecognition;
        Scale.PictureSize = () => Source is { } s ? (s.Raster.Width, s.Raster.Height) : null;
        Scale.CornersChanged += () =>
        {
            // Every pixel coordinate held so far is the other picture's.
            _photo = null;
            ScopeRect = null;
            OnPropertyChanged(nameof(Raster));
            ScheduleRecognition();
        };
        Scale.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ImageScaleViewModel.Tool)) OnPropertyChanged(nameof(Raster));
            if (e.PropertyName is nameof(ImageScaleViewModel.NeedsScale) or nameof(ImageScaleViewModel.Tool)
                or nameof(ImageScaleViewModel.ToolPrompt))
            {
                OnPropertyChanged(nameof(CanvasPrompt));
                RefreshCreate();
            }
        };
        PickGroundRequested = onPicked =>
        {
            _groundPicked = onPicked;
            CanvasPick = true;
            Status = "Click the ground copper on the picture. Escape cancels.";
        };
        Presets = new ObservableCollection<string>(LoadPresets().Select(p => p.Name));
        Overrides.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasOverrides));

        if (source is not null) Accept(source);
        EndConstruction(_defaultName ?? "", targetOwn: OwnTargetOffered);
    }

    private static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    // ── the picture (R-im5-2) ────────────────────────────────────────────────────────────────────────

    [ObservableProperty] private ImageSource? _source;

    public bool HasPicture => Source is not null;
    public bool IsEmpty => Source is null;
    /// <summary>The picture the canvas shows and every click is made on: a board photo read square on its corners, the
    /// photo as taken while its corners are being set (or when none are), else the picture.</summary>
    public RasterImage? Raster =>
        Scale.Tool != ImageMeasureTool.Corners && _photo is { Ok: true } p && ReferenceEquals(p.Corners, Scale.AppliedCorners)
            ? p.Rectified!.Raster
            : Source?.Raster;

    /// <summary>The file name, or <i>Pasted picture</i>.</summary>
    public string SourceName => Source is null ? ""
        : Source.Origin == ImageOrigin.Bytes && Source.OriginalName == ImageSource.PastedName ? "Pasted picture"
        : Source.OriginalName;

    public string SizeText => Source is null ? ""
        : $"{Source.Raster.Width.ToString(CultureInfo.InvariantCulture)} × {Source.Raster.Height.ToString(CultureInfo.InvariantCulture)} px";

    /// <summary>The formats read, for the drop zone's small line.</summary>
    public static string FormatsLine => $"Reads {RasterImage.FormatsRead}.";

    /// <summary>Set by the window: Browse… / Replace…, and the clipboard's picture read as a source (null: the clipboard
    /// holds none).</summary>
    public Func<Task<string?>>? PickPictureAsync { get; set; }
    public Func<Task<ImageSourceResult?>>? PastePictureAsync { get; set; }

    [RelayCommand]
    private async Task Browse()
    {
        if (PickPictureAsync is { } pick && await pick() is { Length: > 0 } path) LoadFile(path);
    }

    [RelayCommand]
    private async Task Paste()
    {
        if (PastePictureAsync is not { } paste) return;
        var read = await paste();
        if (read is null) Status = "The clipboard holds no picture.";
        else if (!read.Ok) Status = read.Refusal ?? "";
        else Accept(read.Source!);
    }

    /// <summary>A dropped or browsed file. A file that is not a picture is refused in the status line with IM-1's
    /// sentence, and whatever was shown stays.</summary>
    public bool LoadFile(string path)
    {
        var read = ImageSource.FromFile(path);
        if (!read.Ok) { Status = read.Refusal ?? ""; return false; }
        Accept(read.Source!);
        return true;
    }

    /// <summary>A pasted picture.</summary>
    public bool LoadBytes(byte[] bytes, string? name)
    {
        var read = ImageSource.FromBytes(bytes, name);
        if (!read.Ok) { Status = read.Refusal ?? ""; return false; }
        Accept(read.Source!);
        return true;
    }

    /// <summary>
    /// A picture handed in from outside an already-open dialog — a menu, a placed bitmap, the Project Tree
    /// (brief-img-6-entry-points.md) — with Make preset. A placed layout picture targets its own layout, as it does
    /// when the dialog opens on it (D14).
    /// </summary>
    public void Read(ImageSource source, bool makeSchematic)
    {
        MakeIndex = makeSchematic ? 0 : 1;
        Accept(source);
        TargetOwn = OwnTargetOffered;
    }

    private void Accept(ImageSource source)
    {
        CancelRecognition();
        _trace = null;
        _photo = null;
        _editedMap = null;
        _editedColours.Clear();
        _declinedPreset = null;
        ReadKind = null;
        Overlay = ImageOverlay.Empty;
        SelectedItem = null;
        LayerRows.Clear();
        ScopeRect = null;
        _schematic = null;
        _ownTargetDefaulted = false;
        Overrides.Clear();
        Scale.Reset();
        if (KindIndex != 0) KindIndex = 0;
        Source = source;

        // The name follows the picture until the user types one of their own.
        string name = ImageTraceTarget.DefaultCellName(source, _workspaceDir);
        bool follow = _defaultName is null || NewCellName.Trim() == _defaultName || NewCellName.Trim().Length == 0;
        _defaultName = name;
        if (follow) NewCellName = name;
        ScheduleRecognition();
    }

    public override bool HasSource => HasPicture;

    partial void OnSourceChanged(ImageSource? value)
    {
        OnPropertyChanged(nameof(HasSource));
        OnPropertyChanged(nameof(HasPicture));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Raster));
        OnPropertyChanged(nameof(SourceName));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(OwnTargetOffered));
        RefreshMode();
    }

    // ── It is / Make (R-im5-2, D3) ───────────────────────────────────────────────────────────────────

    /// <summary>0 Auto, 1 Schematic, 2 Layout.</summary>
    [ObservableProperty] private int _kindIndex;

    /// <summary>What the picture was read as (IM-2), or null before the first reading.</summary>
    [ObservableProperty] private ImageKindResult? _readKind;

    /// <summary>0 Schematic, 1 Layout.</summary>
    [ObservableProperty] private int _makeIndex;

    public bool MakeSchematic => MakeIndex == 0;
    public bool MakeLayout => MakeIndex == 1;

    /// <summary>The kind the dialog reads the picture as: the user's, else what was read; null while unread.</summary>
    public DrawingKind? EffectiveKind => KindIndex switch
    {
        1 => DrawingKind.Schematic,
        2 => DrawingKind.Layout,
        _ => ReadKind?.Kind,
    };

    /// <summary>The Auto segment's label says what Auto decided.</summary>
    public string AutoLabel => ReadKind is not { } k ? "Auto"
        : k.Kind switch
        {
            DrawingKind.Layout => "Auto (layout)",
            DrawingKind.Schematic => "Auto (schematic drawing)",
            DrawingKind.BoardPhoto => "Auto (board photo)",
            _ => "Auto (no drawing)",
        };

    /// <summary>The Auto segment's dot — the parts table's confidence colours.</summary>
    public IBrush AutoConfidenceBrush => new ImmutableSolidColorBrush(Color.Parse(ReadKind?.Confidence switch
    {
        >= 0.8 => "#2E9D4A",
        >= 0.65 => "#D9A21B",
        null => "#00000000",
        _ => "#C8463A",
    }));

    public bool HasReadKind => ReadKind is not null;

    /// <summary>The evidence the kind was read from, one line each.</summary>
    public string AutoTip => ReadKind is not { } k ? "What kind of drawing the picture is, read from it."
        : $"{(int)Math.Round(k.Confidence * 100)} % sure.\n" + string.Join("\n", k.Evidence.Select(e => $"{e.Name}: {Num(e.Value)} ({e.Reads})"));

    /// <summary>Make Layout is not offered for a schematic picture (D3).</summary>
    public bool MakeLayoutEnabled => EffectiveKind != DrawingKind.Schematic;

    public string? MakeLayoutTip => MakeLayoutEnabled ? "Trace the picture's copper into a layout." : MakeLayoutFromSchematicTip;

    public bool IsAutoKind { get => KindIndex == 0; set { if (value) KindIndex = 0; } }
    public bool IsSchematicKind { get => KindIndex == 1; set { if (value) KindIndex = 1; } }
    public bool IsLayoutKind { get => KindIndex == 2; set { if (value) KindIndex = 2; } }
    public bool IsMakeSchematic { get => MakeIndex == 0; set { if (value) MakeIndex = 0; } }
    public bool IsMakeLayout { get => MakeIndex == 1; set { if (value) MakeIndex = 1; } }

    partial void OnKindIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsAutoKind));
        OnPropertyChanged(nameof(IsSchematicKind));
        OnPropertyChanged(nameof(IsLayoutKind));
        RefreshMode();
        ScheduleRecognition();
    }

    partial void OnReadKindChanged(ImageKindResult? value)
    {
        OnPropertyChanged(nameof(AutoLabel));
        OnPropertyChanged(nameof(AutoConfidenceBrush));
        OnPropertyChanged(nameof(AutoTip));
        OnPropertyChanged(nameof(HasReadKind));
        RefreshMode();
    }

    partial void OnMakeIndexChanged(int value)
    {
        if (!_refreshingMode) _wantedMake = value;
        OnPropertyChanged(nameof(IsMakeSchematic));
        OnPropertyChanged(nameof(IsMakeLayout));
        RefreshMode();
        RefreshTarget();
        ScheduleRecognition();
    }

    /// <summary>R-im5-8's links: read a picture with no drawing as a schematic, or as a layout.</summary>
    [RelayCommand] private void ReadAsSchematic() => KindIndex = 1;
    [RelayCommand] private void ReadAsLayout() => KindIndex = 2;

    /// <summary>Everything that follows the kind and the output.</summary>
    private void RefreshMode()
    {
        // A schematic picture makes a schematic: Layout is disabled, so it is not left chosen — but what the user (or the
        // command) asked to make comes back as soon as the picture is read as a layout again.
        int make = EffectiveKind == DrawingKind.Schematic ? 0 : _wantedMake;
        if (MakeIndex != make)
        {
            _refreshingMode = true;
            try { MakeIndex = make; }
            finally { _refreshingMode = false; }
        }
        OnPropertyChanged(nameof(EffectiveKind));
        OnPropertyChanged(nameof(MakeSchematic));
        OnPropertyChanged(nameof(MakeLayout));
        OnPropertyChanged(nameof(MakeLayoutEnabled));
        OnPropertyChanged(nameof(MakeLayoutTip));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(OptionsApply));
        OnPropertyChanged(nameof(ArtworkOptionsApply));
        OnPropertyChanged(nameof(PictureOptionsApply));
        OnPropertyChanged(nameof(PartsApply));
        OnPropertyChanged(nameof(IsLayoutPicture));
        OnPropertyChanged(nameof(IsSchematicPicture));
        OnPropertyChanged(nameof(HasSideColumn));
        OnPropertyChanged(nameof(ShowsLineLegend));
        OnPropertyChanged(nameof(CanvasSpan));
        OnPropertyChanged(nameof(OwnTargetOffered));
        OnPropertyChanged(nameof(ReplaceConfirmText));
        Scale.IsApplicable = IsLayoutPicture;
        if (TargetOwn && !OwnTargetOffered) TargetOwn = false;
        RefreshCreate();
    }

    /// <summary>The picture is read as a layout — the scale, the layers and the technology apply.</summary>
    public bool IsLayoutPicture => HasPicture && EffectiveKind is DrawingKind.Layout or DrawingKind.BoardPhoto;

    /// <summary>The picture is read as a schematic — the line-art options apply.</summary>
    public bool IsSchematicPicture => HasPicture && EffectiveKind == DrawingKind.Schematic;

    /// <summary>Whether the right column holds anything: layers and options apply to a picture read as a drawing. A
    /// picture with no drawing gives the canvas the whole width.</summary>
    public bool HasSideColumn => IsLayoutPicture || IsSchematicPicture;

    public int CanvasSpan => HasSideColumn ? 1 : 3;

    /// <summary>The legend's Lines toggle: a layout picture made into a schematic has lines; nothing else does.</summary>
    public bool ShowsLineLegend => IsLayoutPicture && MakeSchematic;

    /// <summary>Corrections are held on the picture (R-im10-8).</summary>
    public bool HasOverrides => Overrides.Count > 0;

    public override bool OptionsApply => (IsLayoutPicture && MakeSchematic) || IsSchematicPicture;
    public override bool ArtworkOptionsApply => IsLayoutPicture;
    public override bool PictureOptionsApply => IsSchematicPicture;
    public override bool PartsApply => (IsLayoutPicture && MakeSchematic) || IsSchematicPicture;
    public override bool ShowPlacementColumns => false;

    public override string Title =>
        (MakeSchematic ? "Create Schematic from Image" : "Create Layout from Image") + (HasPicture ? $" — {SourceName}" : "");

    // ── target (D14) ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The document a placed picture sits in, over the picture (D14): its layout when a layout is made of a
    /// layout picture, its schematic when a schematic picture is read.</summary>
    public override bool OwnTargetOffered =>
        (Source?.Placement is LayoutBitmapPlacement && MakeLayout) || (Source?.Placement is SchematicBitmapPlacement && IsSchematicPicture);
    public override string OwnTargetLabel => IsSchematicPicture ? "This schematic, over the picture" : "This layout, over the picture";
    public override string OwnTargetTip => IsSchematicPicture
        ? "Draw the circuit into the schematic the picture sits in, over the picture, as one undo step."
        : "Trace into the layout the picture sits in, over the picture, as one undo step.";
    public override string NewCellTip => IsSchematicPicture
        ? "A new cell holding the schematic read from the picture, with the picture under it."
        : MakeSchematic
        ? "A new cell holding the traced layout and the schematic recognised from it, each with the picture under it."
        : "A new cell holding the traced layout, with the picture under it.";

    public override string ReplaceConfirmText => MakeSchematic && !IsSchematicPicture
        ? $"Replaces {NewCellName.Trim()}'s layout and schematic; a checkpoint is taken first."
        : IsSchematicPicture ? $"Replaces {NewCellName.Trim()}'s schematic; a checkpoint is taken first."
        : $"Replaces {NewCellName.Trim()}'s layout; a checkpoint is taken first.";

    protected override string NewCellDir(string name) => Path.Combine(_workspaceDir, name);

    protected override bool IsReplaceable(string cellDir) =>
        IsSchematicPicture ? SchematicImageTarget.IsReplaceable(cellDir)
        : MakeSchematic ? ImageRecognitionTarget.IsReplaceable(cellDir) : ImageTraceTarget.IsReplaceable(cellDir);

    protected override string NotReplaceableText(string name) =>
        $"A cell named '{name}' already exists and was not created from a picture; choose another name.";

    private bool IntoPlacedLayout => TargetOwn && OwnTargetOffered;

    // ── technology (R-im5-6, D8) ─────────────────────────────────────────────────────────────────────

    public IReadOnlyList<ImageTechnologyChoice> TechnologyChoices { get; }

    [ObservableProperty] private int _technologyIndex;

    private ImageTechnologyChoice? Technology => TechnologyIndex >= 0 && TechnologyIndex < TechnologyChoices.Count
        ? TechnologyChoices[TechnologyIndex] : null;

    partial void OnTechnologyIndexChanged(int value)
    {
        // The layer names are the technology's: an edited map naming another technology's layers means nothing here.
        _editedMap = null;
        _editedColours.Clear();
        ScheduleRecognition();
    }

    // ── scale (R-im5-4, D6) ──────────────────────────────────────────────────────────────────────────

    public ImageScaleViewModel Scale { get; } = new();

    // ── layers (R-im5-5, D7) ─────────────────────────────────────────────────────────────────────────

    public ObservableCollection<ImageLayerRowViewModel> LayerRows { get; } = [];

    /// <summary>The row under the pointer: the canvas flashes its colour.</summary>
    [ObservableProperty] private ImageLayerRowViewModel? _hoveredLayer;

    private void OnLayerEdited(ImageLayerRowViewModel row, string text)
    {
        if (_trace?.LayerMap is not { } map || _technology is not { } tech) return;
        var (role, layers) = ImageLayerRowViewModel.Parse(text);
        _editedMap = (_editedMap ?? map).WithRole(tech, row.Cluster, role, layers);
        if (!_editedColours.Contains(row.Rgb)) _editedColours.Add(row.Rgb);
        ScheduleRecognition();
    }

    private bool IsEditedColour(int rgb)
    {
        var lab = CieLab.FromRgb(rgb);
        return _editedColours.Any(c => lab.DeltaE(CieLab.FromRgb(c)) <= ImageLayerMap.MatchDeltaE);
    }

    /// <summary>The presets saved, by name.</summary>
    public ObservableCollection<string> Presets { get; }

    [ObservableProperty] private string? _selectedPreset;

    /// <summary>The preset whose colours match this picture, offered in the status line and applied only on a click.</summary>
    [ObservableProperty] private string? _offeredPreset;

    public string PresetOfferText => OfferedPreset is { } p ? $"Colours match preset \"{p}\"" : "";
    public bool HasPresetOffer => OfferedPreset is not null;

    partial void OnOfferedPresetChanged(string? value)
    {
        OnPropertyChanged(nameof(PresetOfferText));
        OnPropertyChanged(nameof(HasPresetOffer));
        OnPropertyChanged(nameof(OfferText));
        OnPropertyChanged(nameof(HasOffer));
    }

    public override string OfferText => PresetOfferText;
    public override bool HasOffer => HasPresetOffer;
    protected override void OnAcceptOffer() => ApplyOfferedPreset();
    protected override void OnDeclineOffer() => DeclinePreset();

    partial void OnSelectedPresetChanged(string? value)
    {
        if (value is not null) ApplyPreset(value);
    }

    [RelayCommand]
    private void ApplyOfferedPreset()
    {
        if (OfferedPreset is { } name) ApplyPreset(name);
    }

    [RelayCommand]
    private void DeclinePreset()
    {
        _declinedPreset = OfferedPreset;
        OfferedPreset = null;
    }

    private void ApplyPreset(string name)
    {
        if (_trace?.Clusters is not { } clusters || LoadPresets().FirstOrDefault(p => p.Name == name) is not { } preset) return;
        _editedMap = ImageTracePresets.Apply(preset, clusters);
        _editedColours.Clear();
        _editedColours.AddRange(_editedMap.Rows.Select(r => r.Rgb));
        _declinedPreset = name;
        OfferedPreset = null;
        ApplyOptions(preset.Options);
        Status = $"Preset \"{name}\" applied.";
        ScheduleRecognition();
    }

    [ObservableProperty] private string _savePresetName = "";

    [RelayCommand]
    private void SavePreset()
    {
        string name = SavePresetName.Trim();
        if (name.Length == 0) { Status = "Name the preset to save it."; return; }
        if (_trace?.LayerMap is not { } map) { Status = "There are no layers to save yet."; return; }
        try
        {
            ImageTracePresets.Save(ImageTracePresets.From(name, _editedMap is { } edited && _trace.Clusters is { } c ? edited.Rebind(c) : map,
                                                          BuildTraceOptions()), _presetDirectory);
            if (!Presets.Contains(name)) Presets.Add(name);
            _declinedPreset = name;
            Status = $"Saved preset \"{name}\".";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"The preset could not be saved: {ex.Message}";
        }
    }

    private IReadOnlyList<ImageTracePreset> LoadPresets() => ImageTracePresets.Load(_presetDirectory);

    // ── advanced (R-im5-7) ───────────────────────────────────────────────────────────────────────────

    /// <summary>Remembered per user: the window reads and writes it.</summary>
    [ObservableProperty] private bool _advancedExpanded;

    [ObservableProperty] private string _simplifyText;
    [ObservableProperty] private string _minFeatureText;
    [ObservableProperty] private bool _snap;
    [ObservableProperty] private bool _snap45;
    [ObservableProperty] private string _snapAngleText;
    [ObservableProperty] private string _maxColoursText;
    [ObservableProperty] private string _mergeText;
    [ObservableProperty] private string _edgeStrokeText;
    [ObservableProperty] private bool _keepUnderlay;

    /// <summary>Schematic pictures (IM-10): the line-art threshold (Sauvola k) and its window.</summary>
    [ObservableProperty] private string _thresholdText;
    [ObservableProperty] private string _windowText;

    partial void OnSimplifyTextChanged(string value) => ScheduleRecognition();
    partial void OnMinFeatureTextChanged(string value) => ScheduleRecognition();
    partial void OnSnapChanged(bool value) => ScheduleRecognition();
    partial void OnSnap45Changed(bool value) => ScheduleRecognition();
    partial void OnSnapAngleTextChanged(string value) => ScheduleRecognition();
    partial void OnMaxColoursTextChanged(string value) => ScheduleRecognition();
    partial void OnMergeTextChanged(string value) => ScheduleRecognition();
    partial void OnEdgeStrokeTextChanged(string value) => ScheduleRecognition();
    partial void OnKeepUnderlayChanged(bool value) => RefreshCreate();
    partial void OnThresholdTextChanged(string value) => ScheduleRecognition();
    partial void OnWindowTextChanged(string value) => ScheduleRecognition();

    private static bool TryNum(string text, out double v) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && double.IsFinite(v);

    /// <summary>Why an advanced field cannot be read, or null.</summary>
    private string? AdvancedBlocked()
    {
        if (!IsLayoutPicture) return null;
        if (!TryNum(SimplifyText, out double s) || s < 0) return "Simplify needs a tolerance in pixels, zero or more.";
        if (!TryNum(MinFeatureText, out double m) || m < 0) return "Minimum feature needs an area in square pixels, zero or more.";
        if (!TryNum(SnapAngleText, out double a) || a < 0 || a > 45) return "Snap angle needs a number of degrees from 0 to 45.";
        if (!int.TryParse(MaxColoursText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int k) || k < 1 || k > 16)
            return "Colours needs a whole number from 1 to 16.";
        if (!TryNum(MergeText, out double d) || d < 0) return "Merge needs a colour distance (ΔE), zero or more.";
        if (!TryNum(EdgeStrokeText, out double es) || es < 0) return "Edge stroke needs a line width in pixels, zero or more.";
        return null;
    }

    /// <summary>The trace options the advanced fields say — the CLI's flags, field for field.</summary>
    public ImageTraceOptions BuildTraceOptions()
    {
        var d = new ImageTraceOptions();
        return new ImageTraceOptions
        {
            SimplifyPx = TryNum(SimplifyText, out double s) && s >= 0 ? s : d.SimplifyPx,
            MinFeaturePx2 = TryNum(MinFeatureText, out double m) && m >= 0 ? m : d.MinFeaturePx2,
            Snap = Snap,
            Snap45 = Snap45,
            SnapAngleDeg = TryNum(SnapAngleText, out double a) && a >= 0 ? a : d.SnapAngleDeg,
            MaxColours = int.TryParse(MaxColoursText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int k) && k >= 1 ? k : d.MaxColours,
            MergeDeltaE = TryNum(MergeText, out double e) && e >= 0 ? e : d.MergeDeltaE,
            EdgeStrokePx = TryNum(EdgeStrokeText, out double es) && es >= 0 ? es : d.EdgeStrokePx,
            KeepUnderlay = KeepUnderlay,
        };
    }

    private void ApplyOptions(ImageTraceOptions o)
    {
        SimplifyText = Num(o.SimplifyPx);
        MinFeatureText = Num(o.MinFeaturePx2);
        Snap = o.Snap;
        Snap45 = o.Snap45;
        SnapAngleText = Num(o.SnapAngleDeg);
        MaxColoursText = o.MaxColours.ToString(CultureInfo.InvariantCulture);
        MergeText = Num(o.MergeDeltaE);
        EdgeStrokeText = Num(o.EdgeStrokePx);
    }

    // ── the canvas (R-im5-3) ─────────────────────────────────────────────────────────────────────────

    [ObservableProperty] private ImageOverlay _overlay = ImageOverlay.Empty;
    [ObservableProperty] private ImageOverlayItem? _selectedItem;

    [ObservableProperty] private bool _showCopper = true;
    [ObservableProperty] private bool _showDrills = true;
    [ObservableProperty] private bool _showIgnored = true;
    [ObservableProperty] private bool _showParts = true;
    [ObservableProperty] private bool _showLines = true;
    [ObservableProperty] private bool _showPorts = true;
    [ObservableProperty] private bool _showUnknowns = true;
    [ObservableProperty] private bool _showWires = true;
    [ObservableProperty] private bool _showText = true;

    /// <summary>Whether the legend shows a class.</summary>
    public bool Shows(ImageOverlayClass c) => c switch
    {
        ImageOverlayClass.Wire or ImageOverlayClass.Crossing => ShowWires,
        ImageOverlayClass.Text => ShowText,
        ImageOverlayClass.Copper => ShowCopper,
        ImageOverlayClass.Drill => ShowDrills,
        ImageOverlayClass.Ignored => ShowIgnored,
        ImageOverlayClass.Part => ShowParts,
        ImageOverlayClass.Line => ShowLines,
        ImageOverlayClass.Port => ShowPorts,
        _ => ShowUnknowns,
    };

    /// <summary>The part of the picture read: a rectangle dragged on the canvas, or null — the whole picture.</summary>
    [ObservableProperty] private PixelRect? _scopeRect;

    public bool HasScope => ScopeRect is not null;

    partial void OnScopeRectChanged(PixelRect? value)
    {
        OnPropertyChanged(nameof(HasScope));
        ScheduleRecognition();
    }

    [RelayCommand]
    private void WholePicture() => ScopeRect = null;

    /// <summary>The canvas is waiting for a ground click (Ground ▸ Pick on layout).</summary>
    [ObservableProperty] private bool _canvasPick;
    private Action<long, long>? _groundPicked;

    /// <summary>A canvas click while the ground pick is armed: the pixel carried to DBU through the reading's frame.</summary>
    public void PickAt(PixelPoint p)
    {
        CanvasPick = false;
        if (Overlay.Frame is not { } f) { Status = "The ground can be picked once the picture has a scale."; return; }
        var (x, y) = f.ToTarget(p.X, p.Y);
        Status = "";
        _groundPicked?.Invoke((long)Math.Round(x), (long)Math.Round(y));
    }

    public void CancelPick()
    {
        if (!CanvasPick) return;
        CanvasPick = false;
        Status = "";
    }

    /// <summary>The canvas's one prompt: a tool's instruction, else the scale it is waiting for.</summary>
    public string CanvasPrompt =>
        Scale.Tool != ImageMeasureTool.None ? Scale.ToolPrompt
        : IsLayoutPicture && Scale.NeedsScale && FailureText.Length == 0 ? ImageScaleViewModel.SetScalePrompt
        : "";

    partial void OnSelectedItemChanged(ImageOverlayItem? value)
    {
        if (_syncingSelection) return;
        _syncingSelection = true;
        try
        {
            if (value?.PartKey is { } key)
                SelectedRow = Rows.FirstOrDefault(r => string.Equals(r.BoardRefdes, key, StringComparison.OrdinalIgnoreCase));
            else if (value is not null) SelectedRow = null;
        }
        finally { _syncingSelection = false; }
    }

    protected override void OnRowSelected(PartsTableRowViewModel? row)
    {
        if (_syncingSelection) return;
        _syncingSelection = true;
        try { SelectedItem = row is null ? null : Overlay.ItemForPart(row.BoardRefdes); }
        finally { _syncingSelection = false; }
    }

    public override void SelectAnchor(RecognitionAnchor anchor) => SelectedItem = Overlay.ItemForAnchor(anchor);

    // ── failure on the picture (R-im5-8, D16) ───────────────────────────────────────────────────────

    /// <summary>The one sentence the dimmed picture carries, or empty.</summary>
    [ObservableProperty] private string _failureText = "";

    /// <summary>The picture holds no drawing: the two links that set the kind are shown.</summary>
    [ObservableProperty] private bool _failureOffersKind;

    /// <summary>The control that answers a refusal.</summary>
    [ObservableProperty] private bool _highlightLayers;
    [ObservableProperty] private bool _highlightTechnology;

    public bool HasFailure => FailureText.Length > 0;

    partial void OnFailureTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasFailure));
        OnPropertyChanged(nameof(CanvasPrompt));
    }

    private void SetFailure(string text, bool offersKind = false, bool layers = false, bool technology = false)
    {
        FailureText = text;
        FailureOffersKind = offersKind;
        HighlightLayers = layers;
        HighlightTechnology = technology;
        OnPropertyChanged(nameof(FailureOffersSchematic));
        OnPropertyChanged(nameof(FailureOffersLayout));
    }

    /// <summary>The override links: each reads the picture as a kind it is not being read as.</summary>
    public bool FailureOffersSchematic => FailureOffersKind && EffectiveKind != DrawingKind.Schematic;

    public bool FailureOffersLayout => FailureOffersKind && EffectiveKind != DrawingKind.Layout;

    /// <summary>The schematic reader's refusals that mean nothing in the picture is a circuit's line work.</summary>
    private static bool NothingToRead(string refusal) =>
        refusal is SchematicImageReport.NoWiresRefusal or SchematicImageReport.NoSymbolsRefusal
                or SchematicImageReport.CurvedLinesRefusal or SchematicImageEmit.NothingRecognisedRefusal;

    // ── the reading ─────────────────────────────────────────────────────────────────────────────────

    public override string? Blocked => HasPicture ? AdvancedBlocked() ?? base.Blocked : null;

    public override string? CreateBlocked
    {
        get
        {
            if (!HasPicture) return DropPrompt;
            if (FailureText.Length > 0) return FailureText;
            if (EffectiveKind is null) return "Reading the picture…";
            if (EffectiveKind == DrawingKind.Schematic)
                return MakeLayout ? MakeLayoutFromSchematicTip : _schematic is null ? "Reading the picture…" : base.CreateBlocked;
            if (Scale.Error.Length > 0) return Scale.Error;
            if (Scale.NeedsScale) return ImageScaleViewModel.SetScalePrompt;
            return base.CreateBlocked;
        }
    }

    /// <summary>The trace's input — the CLI's, field for field (D2). A board photo read square on its corners is traced
    /// as the CLI's --corners traces it (BoardPhoto.Apply).</summary>
    public ImageTraceInput BuildTraceInput(Technology? technology, ImageKindResult? kind)
    {
        var input = new ImageTraceInput
        {
            Source = Source!,
            Technology = technology,
            TechnologyPath = Technology?.Path,
            Scale = Scale.Statement,
            LayerMap = _editedMap,
            Scope = ScopeRect,
            Options = BuildTraceOptions(),
            Kind = kind is null ? null : kind.AsLayout(),
            TargetIsPlacedLayout = IntoPlacedLayout,
        };
        return _photo is { Ok: true } p && ReferenceEquals(p.Corners, Scale.AppliedCorners) ? BoardPhoto.Apply(input, p) : input;
    }

    /// <summary>The recognition's input over the trace's — the CLI's, field for field.</summary>
    public ImageRecognitionInput BuildRecognitionInput(ImageTraceInput trace) => new()
    {
        Trace = trace,
        Options = BuildOptions(new RecognitionOptions()),
        PartsCsvText = PartsCsvText,
    };

    private ImageKindResult? ForcedKind(ImageKindResult read) => KindIndex switch
    {
        1 => read.Force(DrawingKind.Schematic),
        2 => read.Force(DrawingKind.Layout),
        _ => read,
    };

    protected override Func<RunControl, RecognitionPreview>? BeginPreview()
    {
        if (Source is not { } source) return null;
        var cachedKind = ReadKind;
        int kindIndex = KindIndex;
        var techChoice = Technology;
        bool cached = techChoice is not null && _technologies.ContainsKey(techChoice.Path);
        var cachedTech = techChoice is not null && cached ? _technologies[techChoice.Path] : null;
        bool makeSchematic = MakeSchematic;
        var emit = BuildEmit();
        string? presets = _presetDirectory;
        // Captured now, on the UI thread, from the state as it is; the kind and the technology are filled in the
        // background once both are known.
        var probe = BuildTraceInput(null, null);
        var recognitionProbe = BuildRecognitionInput(probe);
        var schematicProbe = BuildSchematicInput(null, null);
        // A board photo is read square once per set of corners, in the background with the rest of the reading.
        var corners = Scale.AppliedCorners;
        var photoRead = _photo is { } readBefore && ReferenceEquals(readBefore.Corners, corners) ? readBefore : null;

        return control =>
        {
            var read = cachedKind ?? _runner.Classify(source.Raster);
            var kind = kindIndex switch { 1 => read.Force(DrawingKind.Schematic), 2 => read.Force(DrawingKind.Layout), _ => read };
            var empty = new RecognitionReport();
            if (kind.Kind == DrawingKind.None)
                return new RecognitionPreview(null, empty, null, new ImagePreviewDetail(read, null, null, null, null, ImageOverlay.Empty, []));
            if (kind.Kind == DrawingKind.Schematic)
            {
                var lineTech = cached ? cachedTech : techChoice is null ? null : _runner.LoadTechnology(techChoice.Path);
                var picture = _runner.ReadSchematic(schematicProbe with { Kind = kind, Technology = lineTech }, emit, control);
                control.Token.ThrowIfCancellationRequested();
                return new RecognitionPreview(picture.Parts, picture.Report, picture.Refusal,
                    new ImagePreviewDetail(read, lineTech, techChoice?.Path, null, null, ImageOverlay.BuildSchematic(picture), [], picture));
            }

            var tech = cached ? cachedTech : techChoice is null ? null : _runner.LoadTechnology(techChoice.Path);
            var input = probe with { Technology = tech, Kind = kind.AsLayout() };
            var photo = corners is null ? null : photoRead ?? BoardPhoto.Read(source, corners);
            control.Token.ThrowIfCancellationRequested();
            if (photo is { Ok: false })
                return new RecognitionPreview(null, empty, photo.Refusal,
                    new ImagePreviewDetail(read, tech, techChoice?.Path, null, null, ImageOverlay.Empty, [], Photo: photo));
            if (photo is not null && input.Photo is null) input = BoardPhoto.Apply(input, photo);
            ImageTraceResult trace;
            RecognitionResult? recognition = null;
            RecognitionPreview preview;
            if (makeSchematic)
            {
                var reading = _runner.Read(recognitionProbe with { Trace = input }, emit, control);
                trace = reading.Trace;
                recognition = reading.Recognition;
                preview = new RecognitionPreview(reading.Recognition?.Parts, reading.Report, reading.Refusal);
            }
            else
            {
                trace = _runner.Trace(input, control);
                preview = new RecognitionPreview(null, trace.Report, trace.Refusal);
            }
            control.Token.ThrowIfCancellationRequested();
            var overlay = ImageOverlay.Build(trace, recognition, tech);
            var matching = trace.Clusters is { } clusters ? ImageTracePresets.Matching(clusters, presets) : [];
            return preview with { Detail = new ImagePreviewDetail(read, tech, techChoice?.Path, trace, recognition, overlay, matching, Photo: photo) };
        };
    }

    protected override void OnPreviewApplied(RecognitionPreview preview)
    {
        if (preview.Detail is not ImagePreviewDetail d) return;
        ReadKind = d.Kind;
        if (d.TechnologyPath is { } path) _technologies[path] = d.Technology;
        _technology = d.Technology;
        _trace = d.Trace;

        var kind = EffectiveKind;
        SetFailure("");
        Status = "";
        if (kind == DrawingKind.None)
        {
            SetFailure($"No drawing found: {d.Kind.Reason}", offersKind: true);
            Overlay = ImageOverlay.Empty;
            Scale.Update(null, false);
            LayerRows.Clear();
            RefreshCreate();
            return;
        }
        if (kind == DrawingKind.Schematic)
        {
            _schematic = d.Schematic;
            // Started on a placed schematic picture, the default target is the schematic it sits in, over it (D14) —
            // decided once the picture reads as a schematic, and once per picture, so the user's own choice stands.
            if (!_ownTargetDefaulted && OwnTargetOffered) { _ownTargetDefaulted = true; TargetOwn = true; }
            Scale.Update(null, false);
            LayerRows.Clear();
            Overlay = d.Overlay;
            SelectedItem = SelectedRow is { } row ? Overlay.ItemForPart(row.BoardRefdes) : null;
            // Read as a schematic and found no circuit: perhaps it is a layout drawn in lines (R-im12-3).
            if (preview.Refusal is { } refused) { SetFailure(refused, offersKind: NothingToRead(refused)); Status = ""; }
            RefreshLearnState();
            RefreshCreate();
            return;
        }

        if (!ReferenceEquals(_photo, d.Photo))
        {
            _photo = d.Photo;
            OnPropertyChanged(nameof(Raster));
        }
        if (d.Photo is { Ok: false } refusedPhoto)
        {
            Scale.Update(null, true, refusedPhoto.Refusal);
            LayerRows.Clear();
            Overlay = ImageOverlay.Empty;
            RefreshCreate();
            return;
        }

        var trace = d.Trace!;
        bool userStated = Scale.Statement.Kind != ImageScaleStatement.StatementKind.Auto;
        bool statementRefused = userStated && trace.Refusal is not null && trace.Scale is null
                                && !trace.ScaleCandidates.Any(c => c.Kind is ImageScaleKind.Stated or ImageScaleKind.TwoPoints or ImageScaleKind.Impedance);
        Scale.Update(trace, true, statementRefused ? trace.Refusal : null);

        // Layers, marked where the user chose.
        LayerRows.Clear();
        if (trace.LayerMap is { } map && d.Technology is { } tech)
        {
            var names = tech.Layers.Select(l => l.Name).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).ToList();
            foreach (var row in map.Rows.OrderByDescending(r => r.Share))
                LayerRows.Add(new ImageLayerRowViewModel(row, names, IsEditedColour(row.Rgb), OnLayerEdited));
        }

        Overlay = d.Overlay;
        SelectedItem = SelectedRow is { } sel ? Overlay.ItemForPart(sel.BoardRefdes) : null;

        // A preset is offered, never applied (R-im5-5).
        OfferedPreset = _editedMap is null
            ? d.MatchingPresets.Select(p => p.Name).FirstOrDefault(n => n != _declinedPreset)
            : null;

        // A refusal after reading is shown on the picture, the control that answers it highlighted (R-im5-8).
        string? refusal = statementRefused ? null : trace.Refusal ?? (MakeSchematic && trace.Scale is not null ? preview.Refusal : null);
        if (refusal is not null)
        {
            SetFailure(refusal,
                       layers: refusal == ImageTrace.NoCopperRefusal,
                       technology: refusal == ImageTrace.NoTechnologyRefusal);
            Status = "";
        }
        else if (Scale.NeedsScale || statementRefused) Status = "";
        OnPropertyChanged(nameof(CanvasPrompt));
        RefreshCreate();
    }

    // ── create (R-im5-9) ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Raised on the UI thread when a layout was written, or traced into the open layout.</summary>
    public event Action<ImageTraceRun>? LayoutCreated;

    /// <summary>Raised on the UI thread when a cell with a schematic was written.</summary>
    public event Action<ImageRecognitionRun>? SchematicCreated;

    protected override CreateJob BeginCreate()
    {
        var techChoice = Technology;
        var cachedTech = techChoice is not null && _technologies.TryGetValue(techChoice.Path, out var t) ? t : null;
        var input = BuildTraceInput(cachedTech, ReadKind is { } k ? ForcedKind(k) : null);
        string name = NewCellName.Trim();
        Technology? Tech() => cachedTech ?? (techChoice is null ? null : _runner.LoadTechnology(techChoice.Path));

        if (IsSchematicPicture) return BeginSchematicCreate(Tech, name);


        if (MakeLayout)
        {
            var target = IntoPlacedLayout ? ImageTraceTarget.IntoLayout
                : IsReplace ? ImageTraceTarget.Replace(NewCellDir(name))
                : ImageTraceTarget.NewCell(_workspaceDir, name);
            return new CreateJob(IntoPlacedLayout ? "Tracing into the layout…" : $"Writing {name}…", () =>
            {
                var run = _runner.TraceRun(input with { Technology = Tech() }, target, new ImageTraceRunOptions(), null);
                return new CreateOutcome(run.Refusal, run.Report, () => LayoutCreated?.Invoke(run));
            }, "The layout could not be created");
        }

        var recognition = BuildRecognitionInput(input);
        var schematicTarget = IsReplace ? ImageRecognitionTarget.Replace(NewCellDir(name)) : ImageRecognitionTarget.NewCell(_workspaceDir, name);
        var options = new ImageRecognitionRunOptions { Emit = BuildEmit() };
        return new CreateJob($"Writing {name}…", () =>
        {
            var run = _runner.RecognitionRun(recognition with { Trace = recognition.Trace with { Technology = Tech() } },
                                             schematicTarget, options, null);
            return new CreateOutcome(run.Refusal, run.Report, () => SchematicCreated?.Invoke(run));
        }, "The schematic could not be created");
    }

    // ── a schematic picture (brief-img-10 R-im10-6 … R-im10-9) ─────────────────────────────────────────

    /// <summary>The last reading of a schematic picture, or null.</summary>
    private SchematicImageResult? _schematic;
    private bool _ownTargetDefaulted;

    /// <summary>The user's corrections — a crossing flipped, a dangling end joined to a pin — held like table edits and
    /// laid over every re-run (R-im10-8); the CLI's <c>--connect</c>/<c>--disconnect</c>, point for point.</summary>
    public ObservableCollection<ConnectionOverride> Overrides { get; } = [];

    /// <summary>Set by the window's host: the open schematic a placed picture sits in, by its path — what Create draws
    /// over the picture into (D14).</summary>
    public Func<string, SchematicEditModel?>? OpenSchematic { get; set; }

    /// <summary>Raised on the UI thread when a cell with the picture's schematic was written.</summary>
    public event Action<SchematicImageRun>? SchematicPictureCreated;

    /// <summary>Raised on the UI thread with the drawing to add over a placed picture, as one undo step.</summary>
    public event Action<SchematicImageEdit, SchematicBitmapPlacement>? SchematicPictureDrawn;

    private bool IntoPlacedSchematic => TargetOwn && OwnTargetOffered && IsSchematicPicture && Source?.Placement is SchematicBitmapPlacement;

    /// <summary>A crossing clicked on the canvas: it reads the other way from now on, through every re-run.</summary>
    public void ToggleCrossing(ImageOverlayItem item)
    {
        if (item.Class != ImageOverlayClass.Crossing || item.Circle is not { } c || _schematic?.Read is not { } read) return;
        if (!int.TryParse(item.Id[ImageOverlay.CrossingPrefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)
            || read.Wires.Crossings.FirstOrDefault(x => x.Id == id) is not { } crossing) return;
        for (int i = Overrides.Count - 1; i >= 0; i--)
            if (!Overrides[i].IsJoin && Math.Abs(Overrides[i].X - c.X) <= 3 && Math.Abs(Overrides[i].Y - c.Y) <= 3) Overrides.RemoveAt(i);
        Overrides.Add(ConnectionOverride.Crossing(Math.Round(c.X, 2), Math.Round(c.Y, 2), connect: !crossing.Connected));
        Status = crossing.Connected ? "That crossing is not connected now." : "That crossing is connected now.";
        ScheduleRecognition();
    }

    /// <summary>A dangling end dragged on the canvas to <paramref name="to"/>: joined to the nearest pin there.</summary>
    public void JoinEnd(ImageOverlayItem end, PixelPoint to)
    {
        if (!end.Id.StartsWith(ImageOverlay.EndPrefix, StringComparison.Ordinal) || end.Circle is not { } from
            || _schematic is not { Read: { } read, Symbols: { } symbols }) return;
        var pins = symbols.Symbols.SelectMany(sym => sym.Pins).Select(p => read.ToSource(p.X, p.Y)).ToList();
        double reach = Math.Max(12, 6 * read.StrokeWidth);
        var near = pins.Where(p => Math.Abs(p.X - to.X) <= reach && Math.Abs(p.Y - to.Y) <= reach)
                       .OrderBy(p => (p.X - to.X) * (p.X - to.X) + (p.Y - to.Y) * (p.Y - to.Y)).ToList();
        if (near.Count == 0) { Status = "Drop a wire end on a part's pin to connect it."; return; }
        Overrides.Add(ConnectionOverride.Join(Math.Round(from.X, 2), Math.Round(from.Y, 2), Math.Round(near[0].X, 2), Math.Round(near[0].Y, 2)));
        Status = "The wire end is joined to that pin.";
        ScheduleRecognition();
    }

    /// <summary>Forgets every correction.</summary>
    [RelayCommand]
    private void ClearOverrides()
    {
        if (Overrides.Count == 0) return;
        Overrides.Clear();
        ScheduleRecognition();
    }

    /// <summary>The schematic picture's input — the CLI's, field for field (D2): the reading's options with the
    /// corrections, the edited table, Arrange, the underlay setting.</summary>
    public SchematicImageInput BuildSchematicInput(Technology? technology, ImageKindResult? kind)
    {
        var d = new LineArtOptions();
        var lineArt = d with
        {
            K = TryNum(ThresholdText, out double k) && k > 0 ? k : d.K,
            Window = int.TryParse(WindowText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int w) && w >= 3 ? w : d.Window,
        };
        return new SchematicImageInput
        {
            Source = Source!,
            Kind = kind,
            Options = new SchematicImageOptions
            {
                LineArt = lineArt,
                Crossings = CrossingIndex switch { 0 => CrossingRule.Never, 2 => CrossingRule.Always, _ => CrossingRule.WithDot },
                Overrides = [.. Overrides],
            },
            Technology = technology,
            TechnologyPath = Technology?.Path,
            PartsCsvText = PartsCsvText,
            Arrange = ArrangeIndex == 1 ? SchematicArrange.Tidy : SchematicArrange.AsDrawn,
            KeepUnderlay = KeepUnderlay,
        };
    }

    private CreateJob BeginSchematicCreate(Func<Technology?> tech, string name)
    {
        var emit = BuildEmit();
        var kind = ReadKind is { } k ? ForcedKind(k) : null;
        var input = BuildSchematicInput(null, kind);
        if (IntoPlacedSchematic && Source!.Placement is SchematicBitmapPlacement placed)
        {
            // A snapshot of the open schematic, taken here on the UI thread: the background only reads its names.
            var open = OpenSchematic?.Invoke(placed.Document);
            var snapshot = open is null ? new SchematicEditModel() : SchematicPersistence.Deserialize(SchematicPersistence.Serialize(open)).model;
            return new CreateJob("Drawing over the picture…", () =>
            {
                var edit = _runner.IntoSchematic(input with { Technology = tech() }, snapshot, placed, emit, null);
                return new CreateOutcome(edit.Refusal, edit.Report, () => SchematicPictureDrawn?.Invoke(edit, placed));
            }, "The schematic could not be drawn");
        }
        var target = IsReplace ? SchematicImageTarget.Replace(NewCellDir(name)) : SchematicImageTarget.NewCell(_workspaceDir, name);
        return new CreateJob($"Writing {name}…", () =>
        {
            var run = _runner.SchematicRun(input with { Technology = tech() }, target, new SchematicImageRunOptions { Emit = emit }, null);
            return new CreateOutcome(run.Refusal, run.Report, () => SchematicPictureCreated?.Invoke(run));
        }, "The schematic could not be created");
    }

    // ── Learn These Glyphs, for a picture's rows (R-im10-9) ─────────────────────────────────────────────

    private ImageWord? DesignatorWordOf(PartsTableRowViewModel row) =>
        IsSchematicPicture && _schematic is { Circuit: { } circuit, Labels: { } labels }
        && circuit.SymbolOfRow.TryGetValue(row.BoardRefdes, out int symbol)
            ? labels.Symbols.FirstOrDefault(l => l.Symbol == symbol)?.DesignatorWord
            : null;

    protected override bool CanTeach(PartsTableRowViewModel row) =>
        IsSchematicPicture ? DesignatorWordOf(row) is { } word && ImageText.Lesson(word, row.RefdesText.Trim()) is not null
                           : base.CanTeach(row);

    protected override (int Added, IReadOnlyList<char> Chars)? Teach(PartsTableRowViewModel row, string directory)
    {
        if (!IsSchematicPicture) return base.Teach(row, directory);
        if (DesignatorWordOf(row) is not { } word || ImageText.Lesson(word, row.RefdesText.Trim()) is not { } lesson) return null;
        return (GlyphTemplates.Learn(directory, lesson), [.. lesson.Select(l => l.Char)]);
    }

    private void RefreshLearnState()
    {
        LearnGlyphsCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanLearn));
    }
}
