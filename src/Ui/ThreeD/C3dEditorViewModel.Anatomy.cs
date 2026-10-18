// brief-sar-11 — Import ▸ Voxel Model… and Surface Meshes… committed in the 3D editor. Every decision is AnatomyImport's
// (src/Design); this pushes ONE undo entry per import that also carries the bytes of every file it copied, exactly as a STEP
// import's does (C3dFilesEdit), so an undo after a save cannot leave the document naming a file that is not there.

using CircuitRF.Design.ThreeD;
using CircuitRF.Design.ThreeD.Import;
using CircuitRF.Ui.Commands;

namespace CircuitRF.Ui.ThreeD;

public sealed partial class C3dEditorViewModel
{
    /// <summary>What the last anatomy import said it did, for the shell to post to the Messages panel.</summary>
    public IReadOnlyList<string> LastAnatomyNotes { get; private set; } = [];

    /// <summary>Why an anatomy import cannot run in this editor now, or null: the imported file is copied beside the design.</summary>
    public string? ImportAnatomyRefusal()
        => IsViewOnly ? "This 3D view is read-only." : IsScratch ? "Save this 3D design first: an imported model is copied beside it." : null;

    /// <summary>The materials a label or surface may name: the technology's.</summary>
    public IReadOnlyList<string> AnatomyMaterials() => [.. (Elaboration?.Technology?.ResolvedMaterials ?? []).Select(m => m.Name)];

    /// <summary>Plans a voxel import against this document (off the UI thread is fine: nothing here changes).</summary>
    public VoxelImportPlan PlanVoxelImport(string source, VoxelImportOptions options)
        => AnatomyImport.PlanVoxels(source, options, Elaboration?.Technology, Document, Document.DbuPerMicron);

    public SurfaceImportPlan PlanSurfaceImport(IReadOnlyList<string> files, string unit)
        => AnatomyImport.PlanSurfaces(files, unit, null, Elaboration?.Technology, Document);

    /// <summary>The voxel dialog's Import: the volume copied beside the design, one VoxelBody, one undo entry. Null, or why not.</summary>
    public string? AcceptVoxelImport(VoxelImportPlan plan)
        => Commit(working => AnatomyImport.ApplyVoxels(plan, working, FilePath), $"Import voxel body '{plan.Name}'");

    /// <summary>The surface dialog's Import: each file copied, one Mesh per surface in a group, one undo entry. Null, or why not.</summary>
    public string? AcceptSurfaceImport(SurfaceImportPlan plan)
        => Commit(working => AnatomyImport.ApplySurfaces(plan, working, FilePath),
                  plan.Rows.Count == 1 ? $"Import mesh '{plan.Rows[0].Name}'" : $"Import {plan.Rows.Count} meshes");

    private string? Commit(Func<C3dDocument, AnatomyImportResult> apply, string description)
    {
        if (ImportAnatomyRefusal() is { } refusal) return refusal;
        var working = C3dPersistence.Deserialize(C3dPersistence.Serialize(Document));
        AnatomyImportResult result;
        try { result = apply(working); }
        catch (LabelVolumeException e) { return e.Message; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return $"Could not copy the file: {e.Message}"; }
        foreach (var (path, _, created) in result.Files) if (created) _stepCopiesWritten.Add(path);
        var before = C3dListsEdit.Of(Document);
        var after = C3dListsEdit.Of(working);
        Viewer.SetSelection([]);
        _selectAfterAdopt = [.. result.Objects.Select(o => o.Name)];
        Push(new C3dFilesEdit(new C3dListsEdit(description, before, after, ApplyLists), [.. result.Files.Select(f => (f.Path, f.Bytes))], []));
        LastAnatomyNotes = result.Notes;
        StatusMessage = $"{description}." + (result.Notes.Count > 0 ? $" {result.Notes.Count} note{(result.Notes.Count == 1 ? "" : "s")} in Messages." : "");
        return null;
    }
}
