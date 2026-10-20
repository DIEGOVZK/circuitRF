using CircuitRF.Core.Devices.Composed;
using CircuitRF.Design.Modeling;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// brief-modelcomposer-8 R-mc8-2 and R-mc8-5 beyond the default ANN: the Depletion template fitted to the capacitances
/// of a depletion truth — its derivatives, through EquationFit's derivative terms — recovers them; and the composer's
/// whole check runs on that fit, reading ~0 in (a) on the data's interior and in (b) and (c) because a charge equation
/// is conservative.
/// A drift fit with no currents to read is refused rather than checked at Id = 0.
/// </summary>
public sealed class ChargeMethodsTests(ITestOutputHelper output)
{
    private const double Cgs0 = 0.3e-12, Cgd0 = 0.05e-12, Cds = 0.08e-12, Phi = 0.8, Eta = -0.4, Fc = 0.8;

    /// <summary>[P1] eq. 6.11's capacitance, continued flat above Fc·φ as the template's linear charge is.</summary>
    private static double J(double v, double c0) => c0 * Math.Pow(1 - Math.Min(v, Fc * Phi) / Phi, Eta);

    private static ChargeData Data()
    {
        var x = new List<(double, double)>();
        foreach (double g in Enumerable.Range(0, 11).Select(i => -2.0 + 2.5 * i / 10))
            foreach (double d in Enumerable.Range(0, 9).Select(j => 8.0 * j / 8))
                x.Add((g, d));
        return ChargeData.Of(x,
            [.. x.Select(p => J(p.Item1, Cgs0) + J(p.Item1 - p.Item2, Cgd0))],
            [.. x.Select(p => -J(p.Item1 - p.Item2, Cgd0))],
            [.. x.Select(p => -J(p.Item1 - p.Item2, Cgd0))],
            [.. x.Select(p => J(p.Item1 - p.Item2, Cgd0) + Cds)]);
    }

    private static readonly ChargeConstituentSettings Depletion =
        new() { Method = ChargeMethod.Equation, Template = ChargeTemplates.Depletion };

    [Fact]
    public void DepletionTemplate_RecoversItsTruth_AndTheComposersCheckReadsConservative()
    {
        var data = Data();
        var fit = ChargeFit.Run(data, ChargeFitSettings.Default with { Qg = Depletion, Qd = Depletion });
        foreach (string line in fit.Record.Report) output.WriteLine(line);
        Assert.Equal(Constituent.EquationMethod, fit.Record.Qg.Method);
        foreach (var (k, e) in fit.Record.Errors)
            Assert.True(e.Relative < 1e-3, $"{k}: {e.Relative:P3} of its range");

        var program = ComposedCoreProgram.Compile(ChargeFit.Model(fit, null), "depletion");
        Span<double> gg = stackalloc double[2], gd = stackalloc double[2];
        var (qg, qd) = program.Charges([fit.Record.Anchor.Vgs, fit.Record.Anchor.Vds], gg, gd);
        Assert.True(Math.Abs(qg) < 1e-20 && Math.Abs(qd) < 1e-20, $"at the anchor {qg:E3}, {qd:E3} C");

        var r = ConservationCheck.Run(data, fit, null, new LargeSignalSettings());
        foreach (string line in r.Lines) output.WriteLine(line);
        // Check (a) on a rectilinear grid, a third of whose points lie on the hull's edge.
        Assert.True(r.Data!.GateRms < 0.05 && r.Data.DrainRms < 0.05, $"{r.Data.GateRms:P3} {r.Data.DrainRms:P3}");
        Assert.True(r.Model!.GateRms < 1e-3 && r.Model.DrainRms < 1e-3, $"{r.Model.GateRms:E3} {r.Model.DrainRms:E3}");
        Assert.True(r.LargeSignal!.Relative < 1e-6, $"{r.LargeSignal.Relative:E3}");
    }

    // Angelov's junctions: Cgs = Cgsp + Cgs0·(1 + tanh ψ1)·(1 + tanh ψ2), ψ1 = P10 + P11·vgs, ψ2 = P30 − P31·vds, and
    // Cgd = Cgdp + Cgd0·(1 + tanh ψ3)·(1 − tanh ψ4), ψ3 = P20 + P21·vgd, ψ4 = P40 + P41·vds — the published capacitances.
    private static readonly (string Name, double Value)[] AngelovGate =
    [
        ("Cgsp", 0.05e-12), ("Cgs0", 0.15e-12), ("P10", 1.5), ("P11", 2.0), ("P30", 0.2), ("P31", 0.3),
        ("Cgdp", 0.01e-12), ("Cgd0", 0.03e-12), ("P20", 1.0), ("P21", 1.5), ("P40", -0.5), ("P41", 0.4),
    ];
    private const double AngelovCds = 0.08e-12;

    /// <summary>The capacitance matrix of Angelov's charges, by hand: each junction's charge is its capacitance integrated
    /// along its own voltage, so the off-diagonals pick up the vds switches' derivatives (Ks, Kd).</summary>
    private static ChargeData AngelovData()
    {
        var p = AngelovGate.ToDictionary(t => t.Name, t => t.Value);
        static double Sech2(double x) => 1 / Math.Pow(Math.Cosh(x), 2);
        var x = new List<(double, double)>();
        foreach (double g in Enumerable.Range(0, 11).Select(i => -2.0 + 2.5 * i / 10))
            foreach (double d in Enumerable.Range(0, 9).Select(j => 8.0 * j / 8))
                x.Add((g, d));
        var c = new double[4][];
        for (int i = 0; i < 4; i++) c[i] = new double[x.Count];
        for (int k = 0; k < x.Count; k++)
        {
            var (vgs, vds) = x[k];
            double u = vgs - vds;
            double psi1 = p["P10"] + p["P11"] * vgs, psi2 = p["P30"] - p["P31"] * vds;
            double psi3 = p["P20"] + p["P21"] * u, psi4 = p["P40"] + p["P41"] * vds;
            double cgs = p["Cgsp"] + p["Cgs0"] * (1 + Math.Tanh(psi1)) * (1 + Math.Tanh(psi2));
            double cgd = p["Cgdp"] + p["Cgd0"] * (1 + Math.Tanh(psi3)) * (1 - Math.Tanh(psi4));
            double ks = p["Cgs0"] * (vgs + Math.Log(Math.Cosh(psi1)) / p["P11"]) * -p["P31"] * Sech2(psi2);
            double kd = p["Cgd0"] * (u + Math.Log(Math.Cosh(psi3)) / p["P21"]) * -p["P41"] * Sech2(psi4);
            (c[0][k], c[1][k], c[2][k], c[3][k]) = (cgs + cgd, ks - cgd + kd, -cgd, cgd - kd + AngelovCds);
        }
        return ChargeData.Of(x, c[0], c[1], c[2], c[3]);
    }

    [Fact]
    public void AngelovTemplate_RecoversAngelovsCapacitances()
    {
        // Levenberg–Marquardt from the template's own start, without the global search: the start is what is under test,
        // and CMA-ES first takes the test past the routine tier's 5 s.
        var angelov = new ChargeConstituentSettings
        {
            Method = ChargeMethod.Equation, Template = ChargeTemplates.Angelov, Algorithm = CircuitRF.Engine.Optimization.LevenbergMarquardt.AlgorithmId,
        };
        var fit = ChargeFit.Run(AngelovData(), ChargeFitSettings.Default with { Qg = angelov, Qd = angelov });
        foreach (string line in fit.Record.Report) output.WriteLine(line);
        foreach (var (k, e) in fit.Record.Errors)
            Assert.True(e.Relative < 1e-3, $"{k}: {e.Relative:P3} of its range");

        // Qg carries all twelve; Qd, fitted on its own row, the gate–drain six and Cds.
        var truth = AngelovGate.ToDictionary(t => t.Name, t => t.Value);
        truth["Cds"] = AngelovCds;
        foreach (var (name, constituent) in new[] { ("Qg", fit.Record.Qg), ("Qd", fit.Record.Qd) })
            foreach (var (key, value) in constituent.Parameters.Where(kv => truth.ContainsKey(kv.Key)))
            {
                double error = Math.Abs(value / truth[key] - 1);
                output.WriteLine($"{name}.{key}: {value:G6} against {truth[key]:G6} ({error:P3})");
                Assert.True(error < 0.01, $"{name}.{key} {value:G6} against {truth[key]:G6}");
            }
    }

    [Fact]
    public void DriftChargesWithoutCurrents_AreRefusedByTheCheck()
    {
        var domain = ChargeData.Of([(0, 0), (1, 0), (0, 1)], [1, 1, 1], [0, 0, 0], [0, 0, 0], [1, 1, 1]).Domain;
        var record = new ChargeFitRecord { Qg = Constituent.Drift(true, domain), Qd = Constituent.Drift(false, domain) };
        var fit = new ChargeFitResult(ChargeFitSettings.Default, record, new Dictionary<string, RfCore.Data.DataCube>());
        var ex = Assert.Throws<ModelingException>(() => ConservationCheck.Run(Data(), fit, null, new LargeSignalSettings()));
        Assert.Contains("fit the currents first", ex.Message);
    }
}
