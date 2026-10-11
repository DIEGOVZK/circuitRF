using Avalonia.Controls;
using Avalonia.Input;
using CircuitRF.Ui.Controls;
using CircuitRF.Ui.Tests.RailRf;
using Xunit;

namespace CircuitRF.Ui.Tests.Schematic;

/// <summary>
/// The schematic canvas can push its tool cursor to the platform AGAIN without the tool changing.
/// Avalonia only forwards a cursor when the Cursor property changes, and on macOS AppKit can reset
/// the native cursor to the arrow on its own — field report: the wire tool stayed armed with an
/// arrow showing, and pressing W (already Wire) changed nothing.
/// </summary>
public sealed class SchematicCursorReassertTests
{
    [Fact]
    public void Reassert_ChangesTheCursorProperty_EvenWhenTheTypeIsUnchanged()
    {
        RailBoardViewTests.CursorFactoryShim.Install();
        var canvas = new SchematicCanvas();

        canvas.ApplyCursor(StandardCursorType.Cross, reassert: false);
        var first = canvas.Cursor;
        canvas.ApplyCursor(StandardCursorType.Cross, reassert: false);
        Assert.Same(first, canvas.Cursor);          // an unchanged tool does not churn the property

        int changes = 0;
        canvas.PropertyChanged += (_, e) => { if (e.Property == InputElement.CursorProperty) changes++; };
        canvas.ApplyCursor(StandardCursorType.Cross, reassert: true);
        canvas.ApplyCursor(StandardCursorType.Cross, reassert: true);

        Assert.Equal(2, changes);                   // each re-assert reaches the platform
    }
}
