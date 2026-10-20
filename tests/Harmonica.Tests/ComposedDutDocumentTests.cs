using System.Numerics;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Harmonica.Tests.Support;

namespace CircuitRF.Harmonica.Tests;

/// <summary>
/// brief-modelcomposer-23 R-mc23-9/10 — a composed DUT in a <c>.charm</c> (by reference, with its <c>N</c> and switches),
/// its class presets (the Model Composer report's transform) and its intrinsic drag (§6.6's inverse solve).
/// </summary>
public sealed class ComposedDutDocumentTests
{
    // ── .charm ───────────────────────────────────────────────────────────────

    [Fact]
    public void ACharm_StoresTheModelRelative_KeepsNAndTheSwitches_AndOffersARePointWhenItIsMissing()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"mc23-charm-{Guid.NewGuid():N}");
        string models = Path.Combine(folder, "models");
        Directory.CreateDirectory(models);
        string cmodel = Path.Combine(models, "gan.cmodel");
        File.Copy(ComposedFixtures.ExampleModel, cmodel);

        var switches = ComposedSwitches.Default.With(ComposedSwitches.LinearCdgName, true).With(ComposedSwitches.DrainLagName, false);
        var model = ComposedFixtures.Model(file: cmodel, switches: switches, fingers: 6, m: 2);

        string json = CharmIo.Write(model, folder);
        Assert.Contains("\"ModelFile\": \"models/gan.cmodel\"", json);

        var back = CharmIo.Read(json, folder, out var unresolved);
        Assert.Empty(unresolved);
        Assert.Equal(DutKind.Composed, back.Dut.Kind);
        Assert.Equal(Path.GetFullPath(cmodel), back.Dut.ModelFile);
        Assert.Equal(6, back.Dut.Fingers);
        Assert.Equal(2.0, back.Dut.Multiplicity);
        Assert.Equal(switches, back.Dut.Switches);
        Assert.Equal(model.StructuralKey, back.StructuralKey);

        // Outside the .charm's folder the path is kept absolute.
        string elsewhere = Path.Combine(Path.GetTempPath(), $"mc23-other-{Guid.NewGuid():N}");
        Assert.Contains(Path.GetFullPath(cmodel).Replace("\\", "\\\\"), CharmIo.Write(model, elsewhere));

        // Opened from where the model is not, the document still loads and names the file for a re-point.
        CharmIo.Read(json, elsewhere, out var missing);
        var m = Assert.Single(missing);
        Assert.Equal("model", m.Kind);
        Assert.Equal("models/gan.cmodel", m.Reference);
    }

    [Fact]
    public void ADefaultComposedDut_WritesNoSwitchesOrFingers()
    {
        string json = CharmIo.Write(ComposedFixtures.Model());
        Assert.DoesNotContain("\"Switches\"", json);
        Assert.DoesNotContain("\"Fingers\"", json);
    }

    // ── class presets ─────────────────────────────────────────────────────────

    /// <summary>
    /// The preset's extrinsic load, terminated through the model's own network at the instance's N, presents the class's
    /// intrinsic target beside the C22 the device has at its intrinsic quiescent point.
    /// </summary>
    [Fact]
    public void AClassPreset_PresentsTheIntrinsicTarget_ThroughTheNetwork_AtTheBiasPointsC22()
    {
        var ctx = HarmonicaContext.Create(ComposedFixtures.Model(fingers: 8));
        var notes = new List<string>();
        var loads = ComposedClassLoads.For(ctx, PaClass.J, 3, notes, out double c22);
        Assert.NotNull(loads);
        Assert.True(c22 > 0 && double.IsFinite(c22));

        var core = (ComposedCoreModel)ctx.DutDevice.Model;
        var drain = ComposedClassLoads.DrainTwoPort(core.Loaded.Network, core.N, core.Loaded.N0, 1.0, ComposedFixtures.F0, 3);
        var y = drain[0];
        Complex yd = y.D - y.B * y.C / (y.A + 1 / loads![0]);
        Complex zi = 1 / (yd + new Complex(0, 2 * Math.PI * ComposedFixtures.F0 * c22));
        Complex want = PaClassPresets.IntrinsicLoad(PaClass.J, 1, ctx.Model.Settings.Z0);
        Assert.True((zi - want).Magnitude <= 1e-9 * want.Magnitude, $"{zi} vs {want}");
    }

    // ── the intrinsic drag ───────────────────────────────────────────────────

    /// <summary>
    /// A composed DUT has no per-side ABCD, so the glyph is dragged through §6.6's inverse solve: a reachable load-side
    /// target converges onto the glyph, and an unreachable one moves nothing.
    /// </summary>
    [Fact]
    public void ALoadSideDrag_Converges_AndAFailedOneMovesNothing()
    {
        var model = ComposedFixtures.Model();
        Assert.False(CircuitModel.IntrinsicDragAllowed(model, out string reason));
        Assert.Contains("no per-side ABCD", reason);
        Assert.True(CircuitModel.InverseDragOffered(model));

        var ctx = HarmonicaContext.Create(model);
        var terms = new TerminationSet(3);
        terms.Set(TerminationSide.Load, 1, new Complex(30, 10));
        double z0 = model.Settings.Z0;
        var bands = new[] { new InverseBand(TerminationSide.Load, 1) };
        var start = new[] { HarmonicaDataSet.GammaOf(terms.Z(TerminationSide.Load, 1), z0) };
        var options = new InverseSolveOptions { PavlDbm = 0.0, Z0 = z0 };

        var solver = new InverseSolver(terms, bands, start, options);
        Assert.Equal(InverseFailure.None, solver.Begin(ctx));
        var glyph = solver.Evaluate(ctx, solver.Current)![0];

        var target = glyph + new Complex(0.03, -0.02);
        var step = solver.Step(ctx, [target]);
        Assert.True(step.Converged, step.Failure.ToString());
        var reached = solver.Evaluate(ctx, solver.Current)![0];
        Assert.True((reached - target).Magnitude <= 1e-3, $"{reached} vs {target}");

        // Far outside anything a passive load reaches: the solve fails and the committed vector is unchanged.
        var before = solver.Current.ToArray();
        var failed = solver.Step(ctx, [new Complex(-40.0, 55.0)]);
        Assert.False(failed.Converged);
        Assert.Equal(before, solver.Current.ToArray());
    }
}
