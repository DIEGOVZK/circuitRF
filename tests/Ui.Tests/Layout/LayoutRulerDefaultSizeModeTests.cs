using System;
using System.IO;
using Avalonia.Input;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Theming;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout;

/// <summary>
/// Settings ▸ General ▸ Layout ▸ Default Ruler Size Mode: a placed ruler starts in the mode the
/// preference names, and in Fixed (R-rul-4) when it names none. Redirects the per-user state
/// directory, so it runs alone.
/// </summary>
[Collection(UserStateDirectoryCollection.Name)]
public sealed class LayoutRulerDefaultSizeModeTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "crf-ruler-mode-" + Guid.NewGuid().ToString("N")[..8]);

    public LayoutRulerDefaultSizeModeTests()
    {
        Directory.CreateDirectory(_root);
        AppDataRoot.RedirectTo(_root);
    }

    public void Dispose()
    {
        AppDataRoot.RedirectTo(null);
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static RulerAnnotation PlaceOne()
    {
        var model = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um };
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Ruler };
        vm.OnPointerPressed(0, 0, KeyModifiers.None, 1, 40, 1, 0);
        vm.OnPointerMoved(3_000, 0, leftDown: false, KeyModifiers.None, 40, 1, 0);
        vm.OnPointerPressed(3_000, 0, KeyModifiers.None, 1, 40, 1, 0);
        return Assert.Single(model.Rulers);
    }

    [Fact]
    public void Unset_PlacesFixed_AndScaledPreference_PlacesScaled_WithBothSizesStored()
    {
        Assert.Equal(RulerSizeMode.Fixed, PlaceOne().SizeMode);

        AppPreferencesIo.Update(p => p.DefaultRulerSizeMode = RulerSizeMode.Scaled);
        var r = PlaceOne();
        Assert.Equal(RulerSizeMode.Scaled, r.SizeMode);
        Assert.Equal(11.0, r.TextSizePt);          // a switch back to Fixed is reversible (§9B.7)
        Assert.True(r.TextHeightDbu > 0);
    }
}
