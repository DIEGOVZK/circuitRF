using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Harmonica;

/// <summary>
/// Writing a harmonicaRF document to disk — the ONE implementation of it.
///
/// <para>It is a static rather than a method on <c>WorkspaceViewModel</c> (where every other
/// document kind's save lives) because harmonicaRF also runs as a standalone application, with no
/// workspace at all: <c>HarmonicaView</c>'s own File ▸ Save has to work there. And it is not left in
/// the view either, which is where it used to live, because the document TAB's Save must reach a
/// document whose view may not be realized — a background tab's content is created on demand, so a
/// route that starts at the view is a route that silently does nothing on the tab the user actually
/// right-clicked.</para>
///
/// <para>The workspace is optional for the same reason: it is only needed to put a newly-saved
/// <c>.charm</c> into the project tree, which a standalone run has nothing to do.</para>
/// </summary>
internal static class HarmonicaDocumentSave
{
    /// <summary>
    /// Saves <paramref name="doc"/>, asking for a path when it has none (or always, for
    /// <paramref name="saveAs"/>). Returns null when the document was written or the user cancelled
    /// the picker, and the failure's own message when the write itself failed — reported by the
    /// caller, because the two callers report in different places (the readout strip in the
    /// standalone window, the Messages panel in a workspace).
    /// </summary>
    internal static async Task<string?> RunAsync(
        HarmonicaDocument doc, TopLevel top, WorkspaceViewModel? workspace, bool saveAs)
    {
        string? path = saveAs ? null : doc.FilePath;
        if (path is null)
        {
            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title             = saveAs ? "Save harmonicaRF document As" : "Save harmonicaRF document",
                DefaultExtension  = "charm",
                SuggestedFileName = doc.FilePath is { } p ? Path.GetFileName(p) : "harmonica.charm",
            });
            if (file is null) return null;      // cancelled — not a failure
            path = file.Path.LocalPath;
        }

        try
        {
            await File.WriteAllTextAsync(path, doc.ViewModel.Harmonica.ToCharmJson(Path.GetDirectoryName(Path.GetFullPath(path))));
            doc.OnSavedToPath(path);

            // A .charm saved into an open workspace appears in the tree with no reload. Null
            // standalone, where there is no tree to refresh and nothing to register.
            workspace?.NotifyHarmonicaSaved(doc, path);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
