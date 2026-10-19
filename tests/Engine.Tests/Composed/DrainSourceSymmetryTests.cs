using CircuitRF.Core;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Tests.Composed;

namespace CircuitRF.Engine.Tests.Composed;

/// <summary>
/// brief-modelcomposer-2 R-mc2-4: with <c>Symmetry: "Exchange"</c> the constituents are read for
/// <c>vds ≥ 0</c> only and a negative <c>vds</c> is the same device with drain and source exchanged
/// ([P1] §6.5) — so swapping the two terminals in a circuit changes nothing.
/// </summary>
public sealed class DrainSourceSymmetryTests
{
    // Trained for vds ≥ 0 only, as an Exchange model is.
    private static readonly ComposedModel Model =
        RuntimeTestModels.AnnModel(TrainingDomain.Rectangle(-4, 2, 0, 15), DrainSourceSymmetry.Exchange);

    [Fact]
    public void AnExchangeModel_SatisfiesTheSymmetryRelations_InBothHalfSpaces()
    {
        // [P1] eq. 6.55 for the channel, with eqs. 6.63/6.64 for the gate branches and the charges, at
        // the terminals: with p' = (vgs − vds, −vds), the image of p,
        //   Ig(p) = Ig(p'),  Id(p) = −Ig(p') − Id(p') (the image's source current),
        //   Qg(p) = Qg(p'),  Qd(p) = −Qg(p') − Qd(p').
        var core = RuntimeTestModels.Core(Model);
        var rng = new Random(20261019);
        for (int n = 0; n < 40; n++)
        {
            double vgs = -3 + 4 * rng.NextDouble(), vds = -8 + 16 * rng.NextDouble();
            var p = core.Evaluate(new PortVoltages([vgs, vds]));
            var img = core.Evaluate(new PortVoltages([vgs - vds, -vds]));

            Same(p.I[0], img.I[0], "Ig");
            Same(p.I[1], -img.I[0] - img.I[1], "Id");
            Same(p.Q[0], img.Q[0], "Qg");
            Same(p.Q[1], -img.Q[0] - img.Q[1], "Qd");

            // The Jacobian is the derivative of what was returned, in the image half-space too.
            const double h = 1e-6;
            for (int col = 0; col < 2; col++)
            {
                double[] up = [vgs, vds], dn = [vgs, vds];
                up[col] += h; dn[col] -= h;
                var a = core.Evaluate(new PortVoltages(up));
                var b = core.Evaluate(new PortVoltages(dn));
                for (int row = 0; row < 2; row++)
                {
                    Near((a.I[row] - b.I[row]) / (2 * h), p.Dg[row, col], Math.Abs(p.Dg[row, 0]) + Math.Abs(p.Dg[row, 1]));
                    Near((a.Q[row] - b.Q[row]) / (2 * h), p.Dc[row, col], Math.Abs(p.Dc[row, 0]) + Math.Abs(p.Dc[row, 1]));
                }
            }

            void Same(double x, double y, string what)
                => Assert.True(Math.Abs(x - y) <= 1e-12 * Math.Max(Math.Abs(x), Math.Abs(y)) + 1e-30,
                               $"{what} at ({vgs:G4}, {vds:G4}): {x:R} vs {y:R}");
            static void Near(double fd, double exact, double scale)
                => Assert.True(Math.Abs(fd - exact) <= 1e-6 * scale + 1e-30, $"{exact:R} vs finite difference {fd:R}");
        }
    }

    [Fact]
    public void SwappingDrainAndSource_GivesIdenticalTerminalCurrents()
    {
        // [P1] Fig. 6.28's two configurations: one device, its drain and source terminals exchanged in
        // the circuit. The first sees vds < 0 (the image path), the second vds > 0 (the direct one);
        // identical node voltages behind the sense resistors mean identical terminal currents.
        string path = ComposedTestModels.WriteTemp(Model);
        string Circuit(string nets) => $"""
            Vdc:VG vg 0 V=-0.8
            R:RG vg g R=10
            Vdc:VD vd 0 V=-2
            R:RD vd d R=10
            Vdc:VS vs 0 V=0.3
            R:RS vs s R=10
            ComposedCore:X1 {nets} File="{path}" N=4

            """;

        var tight = new AnalysisSettings { NonlinearAbsTol = 1e-12 };
        var a = ComposedFetEquivalenceTests.Dc(Circuit("g d s"), tight);
        var b = ComposedFetEquivalenceTests.Dc(Circuit("g s d"), tight);
        // A 1e-12 A residual across 10 Ω leaves each node good to ~1e-11 V.
        foreach (string node in (string[])["g", "d", "s"])
            Assert.True(Math.Abs(a[node] - b[node]) <= 1e-10 * Math.Max(Math.Abs(a[node]), 1.0), $"{node}: {a[node]:R} vs {b[node]:R}");

        // Not vacuous: the channel conducts — a real drop across the drain-side resistor.
        Assert.True(Math.Abs(a["d"] - -2.0) > 1e-3);
    }
}
