using System;
using System.IO;
using Avalonia.Input;
using CircuitRF.Design.Schematic;
using CircuitRF.Render;
using CircuitRF.Ui.Controls;
using CircuitRF.Ui.Tests.RailRf;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests.ImportSchematic;

/// <summary>
/// The Import Schematic dialog's preview is the schematic editor's own canvas with no edit session, so it pans and
/// zooms as the editor does. With no session the canvas answers Z (zoom box) itself, and claims Escape only while
/// the box is armed — otherwise Escape is the dialog's Cancel.
/// </summary>
public sealed class ImportSchematicPreviewNavigationTests
{
    private static KeyEventArgs Key(Key key) => new() { RoutedEvent = InputElement.KeyDownEvent, Key = key };

    [Fact]
    public void Z_ArmsTheZoomBox_AndEscapeIsClaimedOnlyWhileItIsArmed()
    {
        RailBoardViewTests.CursorFactoryShim.Install();
        var canvas = new SchematicCanvas { Model = new SchematicEditModel().BuildRenderModel().Model };

        var idleEscape = Key(Avalonia.Input.Key.Escape);
        canvas.RaiseEvent(idleEscape);
        Assert.False(idleEscape.Handled);

        var z = Key(Avalonia.Input.Key.Z);
        canvas.RaiseEvent(z);
        Assert.True(z.Handled);

        var armedEscape = Key(Avalonia.Input.Key.Escape);
        canvas.RaiseEvent(armedEscape);
        Assert.True(armedEscape.Handled);

        var again = Key(Avalonia.Input.Key.Escape);
        canvas.RaiseEvent(again);
        Assert.False(again.Handled);
    }

    /// <summary>The zoom box draws its band on <see cref="SchematicOverlay.Empty"/>, which was built before the empty sets
    /// it reads and so threw in the renderer on every frame of the drag.</summary>
    [Fact]
    public void TheEmptyOverlay_DrawsARubberBand()
    {
        var (model, index) = new SchematicEditModel().BuildRenderModel();
        var theme = SchematicRenderTheme.FromTheme(ColorTheme.BuiltIn, ColorVariant.Light);
        using var bitmap = new SKBitmap(100, 100);
        using var canvas = new SKCanvas(bitmap);
        var overlay = SchematicOverlay.Empty with { RubberBand = (10, 10, 50, 50) };

        SchematicRenderer.Draw(canvas, (100, 100), model, index, 0, 0, 1, theme, overlay, excludeGrid: true);

        Assert.NotEqual(theme.Background, bitmap.GetPixel(30, 30));
    }

    [Fact]
    public void ThePreview_IsTheSchematicCanvas()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "circuitRF.slnx"))) dir = Path.GetDirectoryName(dir);
        string xaml = File.ReadAllText(Path.Combine(dir!, "src/Ui/Views/Dialogs/ImportSchematicDialog.axaml"));
        Assert.Contains("<ctrl:SchematicCanvas x:Name=\"PreviewCanvas\"", xaml);
        Assert.DoesNotContain("<Image x:Name=\"PreviewImage\"", xaml);
    }
}
