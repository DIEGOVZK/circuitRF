// brief-ptu-2-toolbar-and-help.md R-ptu2-3: the dialog's Help opens create-from-image.html at the section for what is
// on screen — asserted through the pure anchor function, not through the launcher.

using CircuitRF.Design.Imaging;
using CircuitRF.Ui.Recognition;
using Xunit;

namespace CircuitRF.Ui.Tests.PlotRecognition;

public sealed class RecognitionDialogHelpTests
{
    [Theory]
    [InlineData(DrawingKind.Plot, false, "plots")]
    [InlineData(DrawingKind.Schematic, false, "schematic")]
    [InlineData(DrawingKind.BoardPhoto, false, "photos")]
    [InlineData(DrawingKind.Layout, false, "dialog")]
    public void HelpAnchor_IsTheSectionForWhatIsOnScreen(DrawingKind kind, bool photoCorners, string anchor)
        => Assert.Equal(anchor, ImageSourceViewModel.HelpAnchor(kind, photoCorners));
}
