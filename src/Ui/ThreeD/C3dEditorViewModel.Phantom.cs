// brief-sar-3 R-sar3-2 — a layered phantom in the 3D editor: ONE object (one row, one Inspector page, one undo entry per edit)
// whose layers are listed under its row and whose solids, "<phantom>/<layer>", select it from the view. Add Phantom places one
// of the five shapes at the origin; Flatten turns it into its layers as ordinary objects (C3dPhantoms.Flatten, the function
// the Design layer's own test holds to the same solids).

using CircuitRF.Design.ThreeD;
using CircuitRF.Render.Scene3D;
using CircuitRF.Ui.Viewer3D;

namespace CircuitRF.Ui.ThreeD;

public sealed partial class C3dEditorViewModel
{
    /// <summary>A phantom layer's tree row, beneath its phantom's.</summary>
    public const string PhantomLayerKind = "Phantom layer";

    /// <summary>Adds a phantom of <paramref name="shape"/> at the origin, named phantom1, phantom2 …: one undo entry.</summary>
    public void AddPhantom(C3dPhantomShape shape)
    {
        if (IsViewOnly) return;
        var p = C3dPhantom.New(shape, NextName("phantom"), default, Document.DbuPerMicron);
        Push(new C3dEdit($"Add phantom {p.Name}", [new C3dEditSlot(false, Document.Objects.Count, null, C3dPersistence.SerializeObject(p))], ApplySlots));
        StatusMessage = $"Added phantom \"{p.Name}\" ({shape}): its layers' materials are chosen in the Inspector.";
    }

    /// <summary>Flatten: the phantom at <paramref name="index"/> becomes its layers, ordinary objects in a group named after it.
    /// One undo entry; null, or why not.</summary>
    public string? FlattenPhantom(int index)
    {
        if (index < 0 || index >= Document.Objects.Count || Document.Objects[index] is not C3dPhantom p) return "Select a phantom to flatten.";
        string? made = null;
        var why = EditNames($"Flatten phantom {p.Name}", (doc, _) =>
        {
            var (objects, refusal) = C3dPhantoms.Flatten(doc, index);
            made = string.Join(", ", objects.Select(o => o.Name));
            return refusal;
        });
        if (why is null) StatusMessage = $"Flattened \"{p.Name}\" into {made}.";
        return why;
    }

    /// <summary>Sets layer <paramref name="layer"/>'s material of the phantom at <paramref name="index"/>: one undo entry.</summary>
    public string? SetPhantomLayerMaterial(int index, int layer, string? material)
    {
        if (index < 0 || index >= Document.Objects.Count || Document.Objects[index] is not C3dPhantom p || layer < 0 || layer >= p.Layers.Count)
            return "There is no such phantom layer.";
        if (p.Layers[layer].Material == material) return null;
        ChangeObjects($"Material of {p.Name}/{p.Layers[layer].Name}", [index], o => ((C3dPhantom)o).Layers[layer].Material = material);
        return null;
    }

    /// <summary>The phantom (or brief-sar-11's voxel body) a scene object is a layer or tissue of (its provenance's top object),
    /// or −1.</summary>
    private int PhantomIndexOf(Scene3DObject o)
        => Elaboration?.Provenance.TryGetValue(o.Name, out var p) == true && p.InstancePath.Length == 0 && p.TopObject is { } top
           && Document.Objects.FindIndex(d => d.Name == top) is >= 0 and var i && Document.Objects[i] is C3dPhantom or C3dVoxelBody ? i : -1;

    /// <summary>The phantom the canvas's selection is, wholly, or −1.</summary>
    private int SelectedPhantom()
    {
        var indices = Viewer.SelectedObjects().Select(PhantomIndexOf).Distinct().ToList();
        return indices is [>= 0 and var i] && Document.Objects[i] is C3dPhantom ? i : -1;
    }

    /// <summary>A phantom's layer rows beneath its row: each layer's solid (so selecting it selects the phantom) and material —
    /// and brief-sar-11's voxel body's tissue rows the same way.</summary>
    private void AddPhantomLayers(C3dTreeItem row, C3dObject o)
    {
        if (o is C3dVoxelBody body)
        {
            foreach (var label in body.Labels.Where(l => !l.Air))
                row.Children.Add(new C3dTreeItem(this, C3dPhantoms.LayerName(body.Name, label.Name), PhantomLayerKind,
                                                 label.Material ?? NoMaterialHeader, -1, -1, !body.Hidden)
                                 { IsReadOnly = true, IsModelled = body.Model, DisplayName = $"{label.Name} ({label.Label})" });
            return;
        }
        if (o is not C3dPhantom p) return;
        foreach (var layer in p.Layers)
            row.Children.Add(new C3dTreeItem(this, C3dPhantoms.LayerName(p.Name, layer.Name), PhantomLayerKind,
                                             layer.Material ?? NoMaterialHeader, -1, -1, !p.Hidden)
                             { IsReadOnly = true, IsModelled = p.Model, DisplayName = layer.Name });
    }

    /// <summary>brief-sar-11 — sets the material of row <paramref name="row"/> of the voxel body at <paramref name="index"/>: one
    /// undo entry.</summary>
    public string? SetVoxelLabelMaterial(int index, int row, string? material)
    {
        if (index < 0 || index >= Document.Objects.Count || Document.Objects[index] is not C3dVoxelBody b || row < 0 || row >= b.Labels.Count)
            return "There is no such voxel label.";
        if (b.Labels[row].Material == material && !b.Labels[row].Air) return null;
        ChangeObjects($"Material of {b.Name}/{b.Labels[row].Name}", [index], o =>
        {
            var l = ((C3dVoxelBody)o).Labels[row];
            l.Material = material;
            l.Air = false;
        });
        return null;
    }

    /// <summary>The canvas's and the tree's Flatten entry for a phantom.</summary>
    private Viewer3DMenuItem FlattenPhantomItem(int index)
        => new("Flatten Phantom", () => Report(FlattenPhantom(index)),
               Tip: "Its layers become ordinary objects, in a group named after it.");
}
