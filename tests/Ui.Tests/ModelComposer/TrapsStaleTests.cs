using CircuitRF.Core.Devices.Composed.Traps;
using CircuitRF.Design.Modeling;
using Xunit;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// Model Composer review 4: data moving under a trap fit stales the Traps step only while a trap model is chosen. With None
/// the old result is read by nothing and the step has nothing to run, so a stale mark there could never be cleared.
/// </summary>
public sealed class TrapsStaleTests
{
    [Fact]
    public void DataMovingUnderTheTrapFit_StalesTraps_OnlyWhileATrapModelIsChosen()
    {
        ComposerFixture.Folder(out string cmc);
        ModelCommands.New(cmc, CmcKind.Active, "gan-two-shell");
        var doc = CmcFile.Read(cmc);
        var modelled = CmcStamps.Stamp(
            TrapFit.WithSettings(doc, TrapFit.Settings(doc) with { Model = CaptureEmissionTraps.ModelName }),
            TrapFit.StepName, "an older digest");
        Assert.Contains(TrapFit.StepName, CmcStamps.StaleSteps(cmc, modelled));

        var none = TrapFit.WithSettings(modelled, TrapFit.Settings(modelled) with { Model = ModelTraps.None });
        Assert.DoesNotContain(TrapFit.StepName, CmcStamps.StaleSteps(cmc, none));
    }
}
