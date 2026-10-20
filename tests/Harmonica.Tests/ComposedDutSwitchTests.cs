using System.Numerics;
using CircuitRF.Core;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Engine;
using CircuitRF.Engine.HarmonicBalance;
using CircuitRF.Harmonica.Tests.Support;
using Xunit.Abstractions;

namespace CircuitRF.Harmonica.Tests;

/// <summary>
/// brief-modelcomposer-23 R-mc23-7/8 — MC-21/22's switches on harmonicaRF's DUT: a toggle is structural, a re-bias
/// re-freezes, and with the charge off and no network capacitor the load glyph is the marker seen through the leads.
/// </summary>
public sealed class ComposedDutSwitchTests(ITestOutputHelper output)
{
    [Fact]
    public void TogglingCharge_MovesTheStructuralKey_AndRebuilds()
    {
        var model = ComposedFixtures.Model();
        var off = model with { Dut = model.Dut with { Switches = model.Dut.Switches.With(ComposedSwitches.ChargeName, false) } };
        Assert.NotEqual(model.StructuralKey, off.StructuralKey);

        var ctx = HarmonicaContext.Create(model);
        int before = ctx.RebuildCount;
        Assert.True(ctx.Apply(off));
        Assert.Equal(before + 1, ctx.RebuildCount);
        Assert.False(((ComposedCoreModel)ctx.DutDevice.Model).Switches.Charge);
        Assert.Contains("Charge=false", ctx.NetlistText);
    }

    /// <summary>
    /// An Idq search moves the gate supply in place; the linearised charge must then be frozen at the NEW quiescent point.
    /// It is: the freeze count rises, and at that point the frozen core's charge and 2×2 capacitance — what its small-signal
    /// S-parameters are made of — equal the nonlinear model's to 1e-12.
    /// </summary>
    [Fact]
    public void AnIdqSearch_ReFreezes_AtTheNewBias()
    {
        var model = ComposedFixtures.Model(switches: ComposedSwitches.Default.With(ComposedSwitches.LinearCName, true));
        var ctx = HarmonicaContext.Create(model);
        double idq0 = ctx.DcDrainCurrentAmps;

        BiasFreeze.ResetCount();
        ctx.Apply(ctx.Model with { Bias = ctx.Model.Bias with { Idq = 1.5 * idq0 } });
        Assert.True(BiasFreeze.FreezeCount >= 1, "the Idq search re-froze");
        Assert.NotEqual(model.Bias.Vgs, ctx.Model.Bias.Vgs);

        var core = (ComposedCoreModel)ctx.DutDevice.Model;
        Assert.True(core.IsFrozen);
        var pv = ctx.DcDevicePortVoltages;
        var nonlinear = new ComposedCoreModel(core.Loaded, core.N, ctx.Netlist.AmbientC);
        var lin = core.Evaluate(new PortVoltages(pv));
        var nl  = nonlinear.Evaluate(new PortVoltages(pv));

        double scaleC = Math.Max(Math.Abs(nl.Dc[0, 0]), Math.Abs(nl.Dc[1, 1]));
        for (int p = 0; p < 2; p++)
        {
            Assert.True(Math.Abs(lin.Q[p] - nl.Q[p]) <= 1e-12 * Math.Abs(nl.Q[p]), $"q[{p}]");
            for (int j = 0; j < 2; j++)
                Assert.True(Math.Abs(lin.Dc[p, j] - nl.Dc[p, j]) <= 1e-12 * scaleC, $"C[{p},{j}]: {lin.Dc[p, j]} vs {nl.Dc[p, j]}");
        }

        // And a solve after it reads the frozen charge without re-freezing.
        BiasFreeze.ResetCount();
        Assert.True(ctx.Solve(new TerminationSet(3), 0.0).Converged);
        Assert.Equal(0, BiasFreeze.FreezeCount);
    }

    /// <summary>
    /// §4.5 consequence 1 on a composed DUT: with <c>Charge=false</c> and a network of series leads only, conduction
    /// current is terminal current, so the load glyph is the marker seen through the leads —
    /// <c>Z_L,intr = Z_term + Z_d + Z_s·(1 + I_g/I_d)</c>, the source lead carrying the gate's current as well as the
    /// drain's — with nothing of the core's own in it.
    /// </summary>
    [Fact]
    public void WithChargeOff_AndSeriesLeadsOnly_TheGlyphIsTheMarkerThroughTheLeads()
    {
        // Driven to Newton's floor: the relation is exact KVL/KCL, so it holds to whatever the solve does.
        var model = ComposedFixtures.Model(file: ComposedFixtures.SeriesOnlyModel(), comparable: true,
                                           switches: ComposedSwitches.Default.With(ComposedSwitches.ChargeName, false));
        int k = model.Settings.HarmonicCount;
        int gridN = HbFft.GridSize(k, model.Settings.FftOverSample);
        double w0 = 2 * Math.PI * ComposedFixtures.F0;
        var terms = ComposedDutTests.Terminations(k);

        var ctx = HarmonicaContext.Create(model, ComposedFixtures.Comparable);
        var point = ctx.Solve(terms, 6.0);
        Assert.True(point.Residual < 1e-7, $"‖F‖ {point.Residual:E2}");
        var spectra = IntrinsicPlane.Evaluate(ctx.DutDevice, point.V, ctx.Interface.DeviceNodes, k, gridN, ComposedFixtures.F0);
        var z = HarmonicaDataSet.Intrinsic(ctx, point, spectra, includeSource: false).Z;

        double worst = 0;
        for (int h = 1; h <= k; h++)
        {
            Complex jw = new(0, h * w0);
            // The termination as the plane sees it: the marker behind its DC block, beside the bias choke.
            Complex zt = 1 / (1 / (terms.Z(TerminationSide.Load, h) + 1 / (jw * model.Settings.DcBlockFarads))
                              + 1 / (jw * model.Settings.BiasChokeHenries));
            Complex zd = ComposedFixtures.Rd + jw * ComposedFixtures.Ld;
            Complex zs = ComposedFixtures.Rs + jw * ComposedFixtures.Ls;
            Complex want = zt + zd + zs * (1 + spectra.portCurrents[0, h] / spectra.portCurrents[1, h]);
            double rel = (z[(int)TerminationSide.Load, h] - want).Magnitude / want.Magnitude;
            output.WriteLine($"h={h}  glyph {z[(int)TerminationSide.Load, h]:G6}  marker through leads {want:G6}  rel {rel:E2}");
            worst = Math.Max(worst, rel);
        }
        Assert.True(worst <= 1e-9, $"{worst:E3}");
    }
}
