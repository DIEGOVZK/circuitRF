// ================================================================
//  HarmonicaComposedDutTests.cs  —  brief-modelcomposer-23, the window's half
//
//  R-mc23-7  the DUT panel shows a composed model's file, N and switches; a switch the model cannot honour is
//            locked, with MC-21's own note as its tooltip; a toggle is a structural change.
//  R-mc23-9  Set DUT's Model Composer kind stages a .cmodel, N and switches and refuses a missing file; a dropped
//            .cmodel sets the model at its own N0.
// ================================================================

using System;
using System.IO;
using System.Linq;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;
using Xunit;

namespace CircuitRF.Ui.Tests.Harmonica;

public sealed class HarmonicaComposedDutTests
{
    private static string ExampleModel()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "examples", "Model Composer", "synthetic-gan", "gan_4x125.cmodel");
    }

    [Fact]
    public void TheDutPanel_ShowsFileNAndSwitches_LockingTheOnesTheModelLacks()
    {
        string file = ExampleModel();
        var model = HarmonicaViewModel.DefaultModel() with
        {
            Dut = HarmonicaViewModel.ComposedDutFor(file, HarmonicaViewModel.DefaultModel().Dut),
        };
        var inputs = HarmonicaInputs.Build(model);
        var loaded = ComposedModelCache.Load(file);

        Assert.Equal(file, inputs.Single(i => i.Key == HarmonicaInputs.KeyComposedFile).Text);
        Assert.Equal(loaded.N0.ToString(), inputs.Single(i => i.Key == HarmonicaInputs.KeyComposedFingers).Text);

        foreach (string sw in ComposedSwitches.Names)
        {
            var row = inputs.Single(i => i.Key == HarmonicaInputs.SwitchPrefix + sw);
            Assert.Equal(HarmonicaInputEntry.Boolean, row.Entry);
            Assert.True(row.Structural, $"{sw} rebuilds");
            string? missing = ComposedSwitches.MissingPart(sw, loaded);
            Assert.Equal(missing is not null, row.Locked);
            if (missing is not null) Assert.Equal($"'gan_4x125.cmodel' {missing}; {sw}=false changes nothing.", row.Tooltip);
        }

        var off = HarmonicaInputs.Apply(model, HarmonicaInputs.SwitchPrefix + ComposedSwitches.ChargeName, "0", out string? error);
        Assert.Null(error);
        Assert.False(off!.Dut.Switches.Charge);
        Assert.NotEqual(model.StructuralKey, off.StructuralKey);
    }

    [Fact]
    public void SetDut_StagesACmodel_AndRefusesAMissingOne()
    {
        var editor = new HarmonicaDutEditor(HarmonicaViewModel.DefaultModel().Dut);
        editor.SetKind(DutKind.Composed);
        Assert.Equal("Choose a .cmodel file before setting the DUT.", editor.Validate());

        editor.ModelFile = Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.cmodel");
        Assert.Contains("was not found", editor.Validate());

        editor.ModelFile = ExampleModel();
        editor.Fingers   = 8;
        editor.Switches  = ComposedSwitches.Default.With(ComposedSwitches.LinearCName, true);
        Assert.Null(editor.Validate());

        var dut = editor.Build();
        Assert.Equal(DutKind.Composed, dut.Kind);
        Assert.Equal(ExampleModel(), dut.ModelFile);
        Assert.Equal(8, dut.Fingers);
        Assert.True(dut.Switches.LinearC);
        Assert.Empty(dut.Parameters);
    }
}
