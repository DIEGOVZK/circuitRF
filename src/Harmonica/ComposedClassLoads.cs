using System.Globalization;
using System.Numerics;
using CircuitRF.Core;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Elaboration;
using CircuitRF.Engine;
using CircuitRF.Engine.Modeling;

namespace CircuitRF.Harmonica;

/// <summary>
/// A PA class's loads for a composed DUT, at the EXTRINSIC drain (brief-modelcomposer-23 R-mc23-10): the intrinsic class
/// load (<see cref="PaClassPresets.IntrinsicLoad"/>, Z0 = R_opt) with the linearised output capacitance <c>C22</c> taken
/// off it, carried through the model's own network as the drain-side two-port with both gates AC-shorted to their
/// sources. Never a solver (owner rule): one bilinear inverse per band.
///
/// <para>The Model Composer's validation report (MC-11) uses the same transform for its class sections, so the two cannot
/// disagree about where a class load sits on a composed model's chart.</para>
/// </summary>
public static class ComposedClassLoads
{
    /// <summary>The resistance a band out of a passive load's reach is written at, its reactance kept — the chart's edge.</summary>
    public const double EdgeOhms = 1e-6;

    /// <summary>
    /// The network as the drain-side two-port at each harmonic of <paramref name="f0"/>: port 1 the extrinsic drain, port 2
    /// the intrinsic drain against the intrinsic source, both gates AC-shorted to their sources. <c>Y[k−1] = (Y11, Y12,
    /// Y21, Y22)</c>. The network is the one an instance of <paramref name="n"/> fingers and multiplier <paramref name="m"/>
    /// simulates — <see cref="ComposedNetworkTemplate.Synthesize"/>'s own scaling — with the intrinsic gate tied to the
    /// intrinsic source where the core would sit.
    /// </summary>
    public static C2[] DrainTwoPort(ComposedNetworkTemplate network, int n, int n0, double m, double f0, int maxHarmonic)
    {
        const string name = "drain_two_port";
        // Synthesize places the core last; a placeholder stands in for it and is removed, and the intrinsic gate is the
        // intrinsic source from then on.
        var placeholder = new Instance(ComposedNetworkTemplate.CoreInstanceName, "R", ["gi", "si"], [new ParameterAssignment("R", "1")]);
        var cell = network.Synthesize(name, n, n0, m, placeholder);
        cell.Instances.Remove(placeholder);
        for (int i = 0; i < cell.Instances.Count; i++)
        {
            var inst = cell.Instances[i];
            if (!inst.NetBindings.Contains("gi") && inst.RefNetBinding != "gi") continue;
            cell.Instances[i] = new Instance(inst.InstanceName, inst.Reference,
                                             inst.NetBindings.Select(GateToSource), inst.Overrides)
                                { RefNetBinding = inst.RefNetBinding is { } r ? GateToSource(r) : null };
        }
        cell.Ports.Add("di");
        cell.Ports.Add("si");

        var lib = new Library("drain");
        lib.Cells.Add(cell);
        var tb = new TestBench("drain");
        tb.Instances.Add(new Instance("XN", name, ["0", "ed", "0", "id", "is"]));
        tb.Instances.Add(new Instance("P1", "Port", ["ed", "0"], [new ParameterAssignment("Num", "1"), new ParameterAssignment("Z", "50")]));
        tb.Instances.Add(new Instance("P2", "Port", ["id", "is"], [new ParameterAssignment("Num", "2"), new ParameterAssignment("Z", "50")]));

        using var nl = new Elaborator(lib).Elaborate(tb);
        double[] f = [.. Enumerable.Range(1, maxHarmonic).Select(k => k * f0)];
        var s = SParameterEngine.Run(nl, f)["S"];
        return [.. Enumerable.Range(0, f.Length).Select(k =>
            NetworkAlgebra.SToY(new C2((Complex)s[k, 0, 0], (Complex)s[k, 0, 1], (Complex)s[k, 1, 0], (Complex)s[k, 1, 1]), 50))];

        static string GateToSource(string net) => net == "gi" ? "si" : net;
    }

    /// <summary>
    /// A class's load at the extrinsic plane, band by band (1 = f0): the intrinsic preset with <paramref name="c22"/> taken
    /// off it, then the drain two-port's bilinear inverse — the termination at the extrinsic drain under which the network
    /// presents the rest at the intrinsic drain. A band at the map's pole is written at its intrinsic value and named; a
    /// band that would need a negative resistance keeps its reactance at the chart's edge (<see cref="EdgeOhms"/>), named.
    /// </summary>
    public static Complex[] ExtrinsicLoads(PaClass cls, double ropt, double f0, double c22, C2[] drain, List<string> notes)
    {
        var z = new Complex[drain.Length];
        for (int k = 1; k <= drain.Length; k++)
        {
            Complex zi = PaClassPresets.IntrinsicLoad(cls, k, ropt);
            Complex yd = 1 / zi - new Complex(0, 2 * Math.PI * f0 * k * c22);
            var y = drain[k - 1];
            Complex zExt = 1 / (y.B * y.C / (y.D - yd) - y.A);
            if (!double.IsFinite(zExt.Real) || !double.IsFinite(zExt.Imaginary))
            {
                notes.Add($"band {k} could not be carried to the extrinsic plane; its intrinsic value is used there.");
                zExt = zi;
            }
            else if (zExt.Real < 0)
            {
                // The extrinsic network's own loss puts the target out of a passive termination's reach. A negative-
                // resistance load would be an active termination, so the reactance is kept on the chart's edge, and named.
                notes.Add($"band {k} needs {Zs(zExt)} Ω at the extrinsic plane, outside the Smith chart; its reactance is used.");
                zExt = new Complex(EdgeOhms, zExt.Imaginary);
            }
            z[k - 1] = zExt;
        }
        return z;
    }

    /// <summary>
    /// harmonicaRF's preset for a composed DUT: the class's extrinsic loads to <paramref name="maxHarmonic"/>, with
    /// <c>C22</c> the device's own <c>∂Qd/∂vds</c> at the intrinsic quiescent point (the device as switched, so a model with
    /// its charge off has none) and the network at the instance's <c>N</c> and <c>m</c>. <paramref name="c22"/> is NaN, and
    /// nothing is returned, when the DC point could not be found.
    /// </summary>
    public static Complex[]? For(HarmonicaContext ctx, PaClass cls, int maxHarmonic, List<string> notes, out double c22)
    {
        c22 = double.NaN;
        if (ctx.ComposedCore is not { } core) return null;

        ctx.EnsureBiasFrozen();
        var pv = ctx.DcDevicePortVoltages;
        if (pv.Length == 0) return null;
        c22 = ctx.DutDevice.Evaluate(new PortVoltages(pv)).Dc[1, 1];

        double f0 = ctx.Model.Settings.FrequencyHz;
        var drain = DrainTwoPort(core.Loaded.Network, core.N, core.Loaded.N0, ctx.Model.Dut.Multiplicity, f0, maxHarmonic);
        return ExtrinsicLoads(cls, ctx.Model.Settings.Z0, f0, c22, drain, notes);
    }

    private static string G(double v) => v.ToString("G3", CultureInfo.InvariantCulture);
    private static string Zs(Complex z) => $"{G(z.Real)}{(z.Imaginary < 0 ? "−" : "+")}j{G(Math.Abs(z.Imaginary))}";
}
