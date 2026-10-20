// The passive topology library (brief-modelcomposer-12 R-mc12-3): embedded .csch cells, pins 1…N the ports, every
// reference the schematic's ground. Each entry carries what the schematic cannot say — its analytic starting values
// from the block's analysis (R-mc12-4), and which of its non-R/L/C variables are positive quantities (a skin-effect
// coefficient, a line's impedance and length), so they get the same logarithmic coordinate and default bounds an
// element value does. Everything else about a variable (bounds, lock, tie) is a document setting, as an extrinsic
// network's is (MC-4 R-mc4-2).
//
// The resources were drawn from their netlists by NetlistSchematic (`netlist --to-schematic`), with numbered pins on
// labelled stubs added, the stock extrinsic networks' construction.

namespace CircuitRF.Design.Modeling.Passive;

/// <summary>One library topology: its resource id, title, port count, starting values and positive variables.</summary>
public sealed record PassiveTopology(
    string Id, string Title, int Ports,
    Func<PassiveAnalysisResult, IReadOnlyDictionary<string, double>> Seed,
    IReadOnlyList<string> Positive)
{
    /// <summary>The schematic, read from its resource.</summary>
    public PassiveNetwork Load() => PassiveNetwork.FromText(Id + ".csch", PassiveTopologies.Text(Id), Ports);
}

public static class PassiveTopologies
{
    private const string Prefix = "CircuitRF.Design.Modeling.Passive.Topologies.";

    // Typical magnitudes a seed falls back on when the block's asymptote has the wrong sign or is not finite.
    private const double FallbackR = 1, FallbackL = 1e-9, FallbackC = 1e-13;

    /// <summary>The library, one-ports first, in the order a chooser lists them.</summary>
    public static IReadOnlyList<PassiveTopology> All { get; } =
    [
        new("series-rlc", "Series RLC", 1, a =>
        {
            double c = Pos(a.LowShuntC(0), FallbackC);
            double l = a.SeriesResonanceHz is { } f0 ? 1 / (Sq(2 * Math.PI * f0) * c) : Pos(a.HighL, FallbackL);
            return V(("Rs", Pos(a.RAtMinSeries, FallbackR)), ("Ls", l), ("Cs", c));
        }, []),
        new("parallel-rlc", "Parallel RLC", 1, a =>
            V(("Rp", Pos(a.RAtMaxSeries, 1e3)), ("Lp", Pos(a.LowL, FallbackL)), ("Cp", Pos(a.HighShuntC(0), FallbackC))), []),
        new("inductor-skin", "Inductor with self-resonance and skin-effect loss", 1, a =>
        {
            double l = Pos(a.LowL, FallbackL), r0 = Pos(a.LowR, 0.1);
            double cp = a.ParallelResonanceHz is { } fp ? 1 / (Sq(2 * Math.PI * fp) * l) : Pos(a.HighShuntC(0), 1e-15);
            return V(("R0", r0), ("Rsk", 0.1 * r0 / Math.Sqrt(a.F[^1])), ("Ls", l), ("Cp", cp));
        }, ["R0", "Rsk"]),
        new("capacitor-esr-esl", "Capacitor with ESR, ESL and pad capacitance", 1, a =>
        {
            double c = Pos(a.LowShuntC(0), FallbackC);
            double l = a.SeriesResonanceHz is { } f0 ? 1 / (Sq(2 * Math.PI * f0) * c) : Pos(a.HighL, 1e-10);
            return V(("ESR", Pos(a.RAtMinSeries, 0.1)), ("ESL", l), ("Cs", c), ("Cpad", 0.01 * c));
        }, []),
        new("resistor-parasitics", "Resistor with parasitics", 1, a =>
            V(("Res", Pos(a.LowR, 50)), ("Ls", Pos(a.LowL, 1e-10)), ("Cp", Pos(a.HighShuntC(0), 1e-14))), []),

        new("pi", "Series element with shunt pad capacitors (π)", 2, a =>
            V(("Rs", Pos(a.LowR, FallbackR)), ("Ls", Pos(a.LowL, Pos(a.HighL, FallbackL))),
              ("Cp1", Pos(a.LowShuntC(0), FallbackC)), ("Cp2", Pos(a.LowShuntC(1), FallbackC))), []),
        new("tee", "T network", 2, a =>
        {
            double l = Pos(a.LowL, Pos(a.HighL, FallbackL)), r = Pos(a.LowR, FallbackR);
            return V(("Ra", r / 2), ("La", l / 2), ("Cm", Pos(a.LowShuntC(0) + a.LowShuntC(1), FallbackC)),
                     ("Lb", l / 2), ("Rb", r / 2));
        }, []),
        new("bond-wire", "Bond wire", 2, a =>
        {
            double r0 = Pos(a.LowR, 0.05);
            return V(("R0", r0), ("Rsk", 0.1 * r0 / Math.Sqrt(a.F[^1])), ("Lw", Pos(a.LowL, Pos(a.HighL, FallbackL))));
        }, ["R0", "Rsk"]),
        new("via", "Via", 2, a =>
            V(("Rv", Pos(a.LowR, 0.05)), ("Lv", Pos(a.LowL, Pos(a.HighL, 1e-10))),
              ("Cv", Pos(a.LowShuntC(0) + a.LowShuntC(1), FallbackC))), []),
        new("spiral", "Spiral inductor", 2, a =>
        {
            double l = Pos(a.LowL, Pos(a.HighL, FallbackL));
            double c1 = Pos(a.LowShuntC(0), FallbackC), c2 = Pos(a.LowShuntC(1), FallbackC);
            double cs = a.ParallelResonanceHz is { } fp ? 1 / (Sq(2 * Math.PI * fp) * l) : 0.05 * (c1 + c2);
            double r1 = Pos(1 / a.Shunt(a.Last, 0).Real, 100), r2 = Pos(1 / a.Shunt(a.Last, 1).Real, 100);
            return V(("Rs", Pos(a.LowR, FallbackR)), ("Ls", l), ("Cs", cs),
                     ("Cox1", c1), ("Rsub1", r1), ("Csub1", c1 / 4), ("Cox2", c2), ("Rsub2", r2), ("Csub2", c2 / 4));
        }, []),
        new("mim", "MIM capacitor", 2, a =>
        {
            double c = Pos(a.LowSeriesC, FallbackC);
            double l = a.SeriesResonanceHz is { } f0 ? 1 / (Sq(2 * Math.PI * f0) * c) : 1e-11;
            return V(("Rs", Pos(a.RAtMinSeries, 0.5)), ("Ls", l), ("Cmim", c),
                     ("Cp1", Pos(a.LowShuntC(0), 0.05 * c)), ("Cp2", Pos(a.LowShuntC(1), 0.05 * c)));
        }, []),
        new("tline-pads", "Transmission-line segment with pad capacitors", 2, a =>
        {
            var (z0, theta) = a.LineEstimate(0);
            return V(("Zt", Pos(z0, 50)), ("Et", Pos(theta * 1e9 / a.F[0], 0.5)), ("Cp1", 1e-15), ("Cp2", 1e-15));
        }, ["Zt", "Et"]),
    ];

    public static PassiveTopology Find(string id) =>
        All.FirstOrDefault(t => t.Id == id)
        ?? throw new ArgumentException($"'{id}' is not a passive topology; the topologies are {string.Join(", ", All.Select(t => t.Id))}.", nameof(id));

    /// <summary>The resource's text.</summary>
    public static string Text(string id)
    {
        using var s = typeof(PassiveTopologies).Assembly.GetManifestResourceStream(Prefix + id + ".csch")
                      ?? throw new InvalidOperationException($"The passive topology '{id}' is missing from this build.");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    private static IReadOnlyDictionary<string, double> V(params (string Name, double Value)[] v) =>
        v.ToDictionary(x => x.Name, x => x.Value, StringComparer.Ordinal);

    private static double Pos(double v, double fallback) => double.IsFinite(v) && v > 0 ? v : fallback;
    private static double Sq(double x) => x * x;
}
