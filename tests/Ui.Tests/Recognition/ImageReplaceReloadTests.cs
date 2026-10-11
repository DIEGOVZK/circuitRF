// A replace from Create from Image rewrites a cell's layout in place; a tab already open on it held the model it had read
// and showed the old shapes. The written files go through the reload a history restore uses, as the restore's own
// workspace-relative paths — this holds that they reach the open tab.

using System.IO;
using System.Linq;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests.Recognition;

public sealed class ImageReplaceReloadTests
{
    [Fact]
    public void TheReplacedLayout_ReachesItsOpenTab()
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "crf-imgreload"));
        string clay = Path.Combine(root, "board", "layout", "board.clay");
        string other = Path.Combine(root, "amp", "layout", "amp.clay");

        var changed = WorkspaceViewModel.WrittenAsRestoredPaths(root, [clay, null]);
        // Resolved as ReloadChangedDocuments resolves a restored path.
        var abs = changed.Select(c => Path.GetFullPath(Path.Combine(root, c.RelativePath.Replace('/', Path.DirectorySeparatorChar)))).ToList();

        Assert.Equal([clay], WorkspaceViewModel.OpenDocumentsAffectedBy([clay, other], abs));
    }
}
