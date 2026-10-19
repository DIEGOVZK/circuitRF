using CircuitRF.Core.Design;
using CircuitRF.Design.Layout.Recognition;
using CircuitRF.Design.Schematic;

namespace CircuitRF.Ui.Commands.Schematic;

/// <summary>
/// Puts a whole drawn schematic into an EMPTY one open in an editor, as one undo step — Update Schematic from Layout
/// on a layout of artwork with no placed parts, which reads the artwork as a circuit (designer feedback round 21).
/// <para>The target must draw nothing (<see cref="RecognitionTarget.IsEmptyDrawing"/>): Undo returns it to exactly
/// that, by removing the objects this added and restoring the few settings it took from the drawing — its grid, its
/// technology reference, its provenance and its tuning block — so nothing of the user's is ever in the way.</para>
/// </summary>
internal sealed class FillEmptySchematicCommand : IUiCommand
{
    private readonly SchematicEditModel _model;
    private readonly SchematicEditModel _drawn;
    private readonly Settings _before;
    private readonly Settings _after;

    public string Description { get; }

    public FillEmptySchematicCommand(SchematicEditModel model, SchematicEditModel drawn, string description)
    {
        if (!RecognitionTarget.IsEmptyDrawing(model))
            throw new InvalidOperationException("A schematic is filled only while it draws nothing.");
        _model = model;
        _drawn = drawn;
        _before = Settings.Of(model);
        _after = Settings.Of(drawn);
        Description = description;
    }

    public void Execute()
    {
        _model.Components.AddRange(_drawn.Components);
        _model.Wires.AddRange(_drawn.Wires);
        _model.NetLabels.AddRange(_drawn.NetLabels);
        _model.Dots.AddRange(_drawn.Dots);
        _model.CanvasObjects.AddRange(_drawn.CanvasObjects);
        _model.Analyses.AddRange(_drawn.Analyses);
        _model.Measurements.AddRange(_drawn.Measurements);
        _after.ApplyTo(_model);
        _model.NotifyChanged();
    }

    public void Undo()
    {
        _model.Components.Clear();
        _model.Wires.Clear();
        _model.NetLabels.Clear();
        _model.Dots.Clear();
        _model.CanvasObjects.Clear();
        _model.Analyses.Clear();
        _model.Measurements.Clear();
        _before.ApplyTo(_model);
        _model.NotifyChanged();
    }

    private sealed record Settings(
        double GridSize, bool GridSnap, int AuthorGridDivisor, TuningSetup? Tuning, string? TechRef, ArtworkProvenance? ArtworkSource)
    {
        public static Settings Of(SchematicEditModel m) =>
            new(m.GridSize, m.GridSnap, m.AuthorGridDivisor, m.Tuning, m.TechRef, m.ArtworkSource);

        public void ApplyTo(SchematicEditModel m)
        {
            m.GridSize = GridSize;
            m.GridSnap = GridSnap;
            m.AuthorGridDivisor = AuthorGridDivisor;
            m.Tuning = Tuning;
            m.TechRef = TechRef;
            m.ArtworkSource = ArtworkSource;
        }
    }
}
