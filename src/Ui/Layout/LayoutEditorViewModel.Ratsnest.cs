// The ratsnest overlay (brief-dfr22-3-layout-round-trip.md R-dfr22-3-5; layout-view.md §9 step 4): thin straight
// lines between pins that share a net and that copper has not yet joined. An OVERLAY — never a shape, never saved,
// never exported — which is the lesson of R-L5h-3, where the first ratsnest was written as artwork.
//
// The pins and their copper groups are read once per edit (Model.Changed marks them stale) because reading them
// flattens the design and partitions its copper; a pointer move only offsets the dragged parts' pins.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Design.Layout.Extraction;
using CircuitRF.Design.Layout.Pdn;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.Layout;

public sealed partial class LayoutEditorViewModel
{
    /// <summary>View ▸ Show Ratsnest. On after Update Layout from Schematic (<see cref="ShowRatsnestFrom"/>).</summary>
    [ObservableProperty] private bool _showRatsnest;

    partial void OnShowRatsnestChanged(bool value)
    {
        _ratsnestStale = true;
        RebuildOverlay();
    }

    /// <summary>The schematic's nets by placement, once read — from memory after Update Layout, else from the
    /// schematic beside this layout on disk the first time the ratsnest is drawn.</summary>
    private PdnSchematicNets? _ratsnestNets;

    private bool _ratsnestStale = true;

    /// <summary>Every pin with a net, its copper group, and the instance it belongs to.</summary>
    private List<(RatsnestPin Pin, int Instance)> _ratsnestPins = [];

    /// <summary>How many times the pins were re-read — once per edit, never per pointer move.</summary>
    internal int RatsnestRebuildCount { get; private set; }

    /// <summary>Update Layout from Schematic: the nets the run just used, and the ratsnest switched on.</summary>
    internal void ShowRatsnestFrom(PdnSchematicNets nets)
    {
        _ratsnestNets = nets;
        _ratsnestStale = true;
        if (ShowRatsnest) RebuildOverlay();
        else ShowRatsnest = true;
    }

    /// <summary>The lines to draw now — empty when the ratsnest is off. A part being dragged has its pins drawn
    /// where it is being dragged to.</summary>
    internal IReadOnlyList<RatsnestLine> RatsnestLines()
    {
        if (!ShowRatsnest) return [];
        if (_ratsnestStale) RebuildRatsnestPins();
        if (_ratsnestPins.Count == 0) return [];

        bool dragging = _selectDragKind == SelectDragKind.Move && (_moveLiveDx != 0 || _moveLiveDy != 0)
                        && _selectedInstanceIndices.Count > 0 && !DuplicateDragArmed;
        if (!dragging) return Ratsnest.Build([.. _ratsnestPins.Select(p => p.Pin)]);

        // A dragged pin keeps its copper group only when everything in that group is dragged with it: the copper it
        // was joined to otherwise stays behind, and the line has to show.
        var moving = new HashSet<int>(_selectedInstanceIndices);
        var anchored = _ratsnestPins.Where(p => !moving.Contains(p.Instance)).Select(p => p.Pin.Group).ToHashSet();
        int fresh = int.MinValue;
        var pins = new List<RatsnestPin>(_ratsnestPins.Count);
        foreach (var (pin, inst) in _ratsnestPins)
        {
            if (!moving.Contains(inst)) { pins.Add(pin); continue; }
            pins.Add(pin with
            {
                X = pin.X + _moveLiveDx, Y = pin.Y + _moveLiveDy,
                Group = anchored.Contains(pin.Group) ? fresh++ : pin.Group,
            });
        }
        return Ratsnest.Build(pins);
    }

    private void RebuildRatsnestPins()
    {
        _ratsnestStale = false;
        _ratsnestPins = [];
        RatsnestRebuildCount++;
        if (Technology is not { } tech || Model.Instances.Count == 0 || InstanceBaseDir is not { Length: > 0 } layoutDir) return;

        // PlacedPins and the schematic walk read the DIRECTORY of this path; the name is only what a note would say.
        string clayPath = Path.Combine(layoutDir, "layout.clay");
        _ratsnestNets ??= PdnSchematicNets.Resolve(clayPath);

        var flat = LayoutDesignFlatten.Flatten(Model, CurrentCellDir ?? "", tech, ResolveTechAt, resolvedCrossTechMappings: null);
        if (flat.ExceedsCeiling) return;
        var copper = CopperPieces.Build(flat.Shapes, tech, Model.Shapes);

        var origins = new List<PlacedPinOrigin>();
        var nets = _ratsnestNets;
        var placed = PlacedPins.Of(Model, clayPath, tech, PinNaming.SchematicThenArtwork,
            portNamesOf: id => nets.For(id)?.PortNames ?? [],
            portNetsOf: id => nets.For(id)?.Nets ?? [],
            stamped: copper, origins: origins, scope: PlacementScope.EveryPlacement);

        int unjoined = -1;
        for (int i = 0; i < placed.Count && i < origins.Count; i++)
        {
            if (placed[i].Net is not { Length: > 0 } net) continue;
            int group = copper.PieceAt(placed[i].X, placed[i].Y, origins[i].Layer);
            _ratsnestPins.Add((new RatsnestPin(net, placed[i].X, placed[i].Y, group >= 0 ? group : unjoined--),
                               origins[i].Instance));
        }
    }
}
