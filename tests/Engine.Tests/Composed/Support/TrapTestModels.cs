using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Devices.Composed.Traps;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Core.Tests.Composed;
using CircuitRF.Engine.HarmonicBalance;

namespace CircuitRF.Engine.Tests.Composed;

/// <summary>
/// The trapped device the brief-modelcomposer-10 engine gates share: <see cref="ComposedTestModels"/>' device whose drain
/// current takes a drain-lag state through a saturating threshold shift ([P21]'s back-gate view),
/// <c>vgs → vgs − At·tanh((phi2 − vds)/Ct)</c> — zero at DC, where <c>phi2 = vds</c>.
/// </summary>
public static class TrapTestModels
{
    public const string IdTrapEq =
        "beta*(ln(1+exp((vgs-At*tanh((phi2-vds)/Ct)-Vto)/Vs))*Vs)^2*(1+lambda*vds)*tanh(alpha*vds)";

    public static readonly (string Name, double Value)[] IdTrapParams = [.. ComposedTestModels.IdParams, ("At", 0.3), ("Ct", 5.0)];

    /// <summary>The device with a <c>CaptureEmission</c> drain-lag state at its defaults, plus <paramref name="parameters"/>.</summary>
    public static ComposedModel DrainLag(ModelTemperature? temperature = null, params (string Name, double Value)[] parameters)
        => With(new ModelTraps
        {
            Model = CaptureEmissionTraps.ModelName, States = [CaptureEmissionTraps.DrainLag],
            Parameters = [.. parameters.Select(p => new KeyValuePair<string, double>(p.Name, p.Value))],
        }, temperature);

    /// <summary>The device with the given trap block.</summary>
    public static ComposedModel With(ModelTraps traps, ModelTemperature? temperature = null)
    {
        var m = ComposedTestModels.Model(temperature);
        return m with
        {
            Traps = traps,
            Core = m.Core with { Id = Constituent.Equation(IdTrapEq, ["vgs", "vds", "phi2"], IdTrapParams) },
        };
    }

    /// <summary>The device with no traps and its drain-lag state frozen at <paramref name="phi2"/> volts.</summary>
    public static ComposedModel Frozen(double phi2)
    {
        var m = ComposedTestModels.Model();
        return m with
        {
            Core = m.Core with
            {
                Id = Constituent.Equation(IdTrapEq, ["vgs", "vds"], [.. IdTrapParams, ("phi2", phi2)]),
            },
        };
    }

    /// <summary>The minted node of trap state <paramref name="k"/> of instance X1.</summary>
    public static int StateNode(ElaboratedNetlist nl, int k = 0)
    {
        for (int i = 1; i < nl.Nodes.Count; i++)
            if (nl.Nodes.NameOf(i) is { } n && n.StartsWith("__composed_X1", StringComparison.Ordinal) && n.EndsWith($"_x{k}", StringComparison.Ordinal))
                return i;
        throw new InvalidOperationException("no trap state node");
    }

    /// <summary>A DC solve: every node's voltage by name, the minted ones included.</summary>
    public static Dictionary<string, double> Dc(string cnl) => ComposedFetEquivalenceTests.Dc(cnl);

    /// <summary>The 1 MHz drain-driven bench: the gate held at <paramref name="vg"/>, the drain swung ±<paramref name="swing"/>
    /// about <paramref name="vd"/>.</summary>
    public static string DrainDrive(double vg, double vd, double swing, double f, int harmonics) => $"""
        Vdc:VG g 0 V={vg:R}
        V_1Tone:VD d 0 Vdc={vd:R} V={swing:R} Freq={f:R} Phase=0
        analysis HB1 type=hb Tone={f:R} MaxHarm={harmonics} Tol=1e-9

        """;

    /// <summary>An HB solve: its full interface spectrum by node name — the minted state nodes included, which the V cube
    /// leaves out — in the engine's full-amplitude convention.</summary>
    public static Dictionary<string, Complex[]> Hb(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        var hba = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        var r = new HbEngine(nl, tb).Run(HbEngine.Resolve(hba, nl.ResolvedGlobals));
        if (!r.Converged)
        {
            var w = new StringWriter();
            r.Trace?.Print(w);
            throw new InvalidOperationException("HB did not converge\n" + w);
        }
        int[] nodes = [.. nl.NonlinearNodes.Where(n => n > 0).Distinct().Order()];
        var v = r.InterfaceV!;
        var map = new Dictionary<string, Complex[]>(StringComparer.Ordinal);
        for (int i = 0; i < nodes.Length; i++)
        {
            var h = new Complex[v.GetLength(1)];
            for (int k = 0; k < h.Length; k++) h[k] = v[i, k];
            map[nl.Nodes.NameOf(nodes[i])!] = h;
        }
        int state = StateNode(nl);
        map["phi2"] = map[nl.Nodes.NameOf(state)!];
        return map;
    }

    /// <summary>A full-amplitude spectrum at phase <paramref name="theta"/>: <c>V0 + Σ Re{Vk·e^{jkθ}}</c>.</summary>
    public static double At(Complex[] h, double theta)
    {
        double v = h[0].Real;
        for (int k = 1; k < h.Length; k++) v += (h[k] * Complex.Exp(new Complex(0, k * theta))).Real;
        return v;
    }
}
