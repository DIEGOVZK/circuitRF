using System;
using System.Collections.Generic;
using System.IO;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// The copy half of Duplicate Cell: the folder as it is on disk, then every view of it that is open
/// with UNSAVED edits written over its copy — so the duplicate is the cell the user is looking at.
///
/// <para><b>Why the second step exists.</b> Copying the folder alone copied the last SAVE. A
/// schematic edited for an hour and duplicated before saving came out of the copy as it had been an
/// hour earlier — every part added since was missing from the duplicate, while the original's tab
/// still showed them, so nothing on screen said the copy was stale (a field report: lines drawn
/// between the parts were gone from the duplicate). The original is left exactly as it was — still
/// open, still dirty, still the user's to save or discard; only the copy is written.</para>
///
/// <para>Framework-free, like the registries it reads, so it is testable without a window.</para>
/// </summary>
internal static class CellDuplicate
{
    /// <summary>
    /// Copies <paramref name="sourceDir"/> to <paramref name="destDir"/> (which must not exist), then
    /// writes each open, dirty schematic and layout under <paramref name="sourceDir"/> over its copy.
    /// Returns the copied files that were written from an open editor rather than from disk.
    /// </summary>
    /// <param name="schematics">The workspace's schematic sessions, one per open <c>.csch</c>.</param>
    /// <param name="layouts">Every layout session with the <c>.clay</c> it is backed by — the base
    /// frame of a layout tab is not in the layout registry, which is why this takes the list
    /// <c>WorkspaceViewModel.AllLayoutSessions</c> builds rather than the registry.</param>
    public static IReadOnlyList<string> Copy(
        string sourceDir, string destDir,
        SchematicSessionRegistry schematics,
        IReadOnlyList<(LayoutEditorViewModel Vm, string? ClayPath)> layouts)
    {
        CopyDirectoryRecursive(sourceDir, destDir);

        var dirtyLayouts = new Dictionary<string, LayoutEditorViewModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var (vm, clay) in layouts)
            if (clay is not null && vm.IsDirty)
                dirtyLayouts[Path.GetFullPath(clay)] = vm;

        var written = new List<string>();
        foreach (var src in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var key = Path.GetFullPath(src);
            var dst = Path.Combine(destDir, Path.GetRelativePath(sourceDir, src));

            if (src.EndsWith(".csch", StringComparison.OrdinalIgnoreCase)
                && schematics.IsDirty(key) && schematics.TryGet(key, out var svm) && svm is not null)
            {
                SchematicPersistence.SaveToFile(dst, svm.EditModel, Path.GetFileNameWithoutExtension(dst));
                written.Add(dst);
            }
            else if (src.EndsWith(".clay", StringComparison.OrdinalIgnoreCase)
                     && dirtyLayouts.TryGetValue(key, out var lvm))
            {
                LayoutPersistence.SaveToFile(dst, lvm.Model);
                written.Add(dst);
            }
        }
        return written;
    }

    private static void CopyDirectoryRecursive(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectoryRecursive(dir, Path.Combine(destination, Path.GetFileName(dir)));
    }
}
