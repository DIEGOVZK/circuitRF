using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Ui.Commands.Schematic;

namespace CircuitRF.Ui.ViewModels;

// TLIN's Electrical / Physical entry switch (brief-artsch-2 R-as2-3), in the style of MKLOPF's: one button
// naming the form it switches TO, converting rather than resetting. The arithmetic is
// TlinEntryConversion's (src/Design); this is the button and the undoable edit. MLIN, CPWG and SLIN have the
// same button (designer feedback round 21), converting through their own model — LineEntryConversion's.
public sealed partial class ParameterEditorViewModel
{
    private IRelayCommand? _toggleTlinEntryCommand;

    public IRelayCommand ToggleTlinEntryCommand
        => _toggleTlinEntryCommand ??= new RelayCommand(ToggleTlinEntry, () => IsLineEntryTarget);

    /// <summary>True only for a placed TLIN.</summary>
    public bool IsTlinTarget => _target?.Symbol == SymbolKind.Tline;

    /// <summary>True for a line with an Electrical / Physical switch — TLIN, MLIN, CPWG and SLIN. Gates the
    /// switch in the view.</summary>
    public bool IsLineEntryTarget => IsTlinTarget || (_target is { CellRef: null } t && LineEntryConversion.Applies(t.Symbol));

    /// <summary>True when the line is stated by its physical length (L) rather than E at F.</summary>
    public bool TlinUsesPhysicalEntry => _target is not null
        && (IsTlinTarget ? TlinEntryConversion.IsPhysical(_target.Parameters) : !LineEntryConversion.IsElectrical(_target.Parameters));

    public string TlinEntryToggleLabel => TlinUsesPhysicalEntry ? "Use electrical length" : "Use physical length";

    /// <summary>What the last switch had to say (converted at 1 GHz, a value it could not read); empty
    /// otherwise, and cleared when the selection changes.</summary>
    [ObservableProperty] private string _tlinEntryNote = "";

    public bool HasTlinEntryNote => TlinEntryNote.Length > 0;

    partial void OnTlinEntryNoteChanged(string value) => OnPropertyChanged(nameof(HasTlinEntryNote));

    private void ToggleTlinEntry()
    {
        if (_target is null || _schematicVm is null || !IsLineEntryTarget) return;

        var tech = SchematicTechnology.Of(_schematicVm.EditModel);
        if (IsTlinTarget)
        {
            var (parameters, note) = TlinEntryConversion.Switch(_target.Parameters, MicrostripSubstrateInjection.LengthUnitFor(tech));
            _schematicVm.Execute(new SetParametersCommand(_schematicVm.EditModel, _target, parameters));
            TlinEntryNote = note;
            return;
        }

        var (lineParameters, lineNote) = LineEntryConversion.Switch(
            _target, tech, LineEntryConversion.ReferenceFrequencyHz(_schematicVm.EditModel));
        if (lineParameters is not null)
            _schematicVm.Execute(new SetParametersCommand(_schematicVm.EditModel, _target, lineParameters));
        TlinEntryNote = lineNote;
    }

    private void NotifyTlinState(bool targetChanged)
    {
        if (targetChanged) TlinEntryNote = "";
        OnPropertyChanged(nameof(IsTlinTarget));
        OnPropertyChanged(nameof(IsLineEntryTarget));
        OnPropertyChanged(nameof(TlinUsesPhysicalEntry));
        OnPropertyChanged(nameof(TlinEntryToggleLabel));
        ToggleTlinEntryCommand.NotifyCanExecuteChanged();
        NotifyLineSwapState();
    }
}
