// Each layout's Layers list in the per-user `.cwsuser` (brief-layout-layers-panel.md R-lyr-4).
//
// Keyed by FULL path in memory and by workspace-relative path on disk, beside the 3D cameras and
// drawing planes. A layout opened with no workspace has no `.cwsuser`, so its entry lives in memory
// for the session only — there is deliberately no second store for it.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.ViewModels;

public partial class WorkspaceViewModel
{
    private Dictionary<string, CwsLayoutLayerView>? _layoutLayerViews;

    /// <summary>
    /// View ▸ Layers List — opens or closes the active layout's Layers list. The same toggle as the
    /// layout toolbar's Layers button and the L key (<c>LayoutEditorView.TryToggleLayerList</c>);
    /// the menu is where the key is written down.
    /// </summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand(CanExecute = nameof(IsLayoutDocumentActive))]
    private void ToggleLayoutLayerList()
    {
        if (ActiveLayoutDocument?.ActiveViewModel is { } vm) vm.ShowLayerList = !vm.ShowLayerList;
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Every layout's stored layer view, by full path — read from the <c>.cwsuser</c> once per
    /// workspace, then kept current by <see cref="RememberLayoutLayerView"/>.</summary>
    private Dictionary<string, CwsLayoutLayerView> LayoutLayerViews
    {
        get
        {
            if (_layoutLayerViews is not null) return _layoutLayerViews;
            _layoutLayerViews = new Dictionary<string, CwsLayoutLayerView>(PathComparer);
            if (CurrentWorkspacePath is { } cws && WorkspaceRootDir is { } root
                && TryLoadCws(cws).LayoutLayerViews is { } stored)
                foreach (var (rel, view) in stored)
                    _layoutLayerViews[Path.GetFullPath(Path.Combine(root, rel))] = view;
            return _layoutLayerViews;
        }
    }

    /// <summary>Seeds a freshly built layout session from what was remembered for its path, and
    /// subscribes so its later changes are remembered too. Called before its technology is applied, so
    /// the default drawing layer is chosen from layers this view can see.</summary>
    private void AttachLayoutLayerView(LayoutEditorViewModel vm, string absClayPath)
    {
        LayoutLayerViews.TryGetValue(Path.GetFullPath(absClayPath), out var stored);
        vm.ApplyLayerView(stored);
        vm.LayerViewChanged += RememberLayoutLayerView;
    }

    /// <summary>The view as it is now; a view with nothing off is no entry at all (D4).</summary>
    private void RememberLayoutLayerView(LayoutEditorViewModel vm)
    {
        if (vm.CurrentLayoutPath is not { Length: > 0 } path) return;
        string key = Path.GetFullPath(path);
        if (vm.LayerViewToPersist() is { } view) LayoutLayerViews[key] = view;
        else LayoutLayerViews.Remove(key);
    }

    /// <summary>
    /// Carries a layout's entry to its new path after a rename or move, so its layers stay as they were
    /// set rather than returning to all-on.
    /// </summary>
    private void MoveLayoutLayerViews(string oldPath, string newPath)
    {
        string from = Path.GetFullPath(oldPath), to = Path.GetFullPath(newPath);
        bool folder = Directory.Exists(to);
        foreach (var key in LayoutLayerViews.Keys.ToList())
        {
            string? moved = PathComparer.Equals(key, from) ? to
                : folder && key.StartsWith(from + Path.DirectorySeparatorChar, PathComparer == StringComparer.Ordinal
                        ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)
                    ? to + key[from.Length..]
                    : null;
            if (moved is null) continue;
            LayoutLayerViews[moved] = LayoutLayerViews[key];
            LayoutLayerViews.Remove(key);
        }
    }

    /// <summary>The workspace's layouts' layer views for the <c>.cwsuser</c>, workspace-relative — null
    /// when none has anything off.</summary>
    private Dictionary<string, CwsLayoutLayerView>? LayoutLayerViewsToPersist()
    {
        if (WorkspaceRootDir is not { } root) return null;
        foreach (var (vm, _) in AllLayoutSessions()) RememberLayoutLayerView(vm);

        string rootFull = Path.GetFullPath(root);
        var result = new Dictionary<string, CwsLayoutLayerView>(StringComparer.Ordinal);
        foreach (var (path, view) in LayoutLayerViews.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            string rel = Path.GetRelativePath(rootFull, path);
            if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel)) continue;
            result[rel.Replace('\\', '/')] = view;
        }
        return result.Count > 0 ? result : null;
    }
}
