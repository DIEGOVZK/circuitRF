using CircuitRF.Core;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Engine;

/// <summary>
/// The bias-point freeze (brief-modelcomposer-21 R-mc21-3): solves the circuit's quiescent DC operating point and hands
/// every model that evaluates part of itself there (<see cref="IBiasFrozen"/> — a composed FET with <c>LinearC=true</c>)
/// its own port voltages at that point. Intrinsic charge never affects a DC solution, so finding the point is never
/// circular.
///
/// <para><b>Every engine that reads charge calls it before its first evaluation</b> — <see cref="SParameterEngine"/>'s
/// preparation, <c>HbEngine.Run</c> and <c>HbEngine.RunSinglePoint</c> (warm-started or not, so loadpull, pursuit, the
/// parametric sweep, the optimizer and the composer's validation bench all go through it), and harmonicaRF's own HB
/// solve. <c>BiasFreezeTests</c> scans <c>src/Engine</c> and <c>src/Harmonica</c> for those entry points: a missed caller
/// would not be a wrong answer, it would be the refusal an unfrozen core raises, but the scan is what keeps it so.</para>
///
/// <para><b>Free when nothing needs it.</b> A netlist with no model asking for a bias point returns at once, with no DC
/// solve, so every design that does not use a linearised charge runs exactly as before.</para>
///
/// <para><b>Re-frozen when the DC solution moves.</b> Each call re-solves DC and each model compares the point it is
/// handed with the one it holds: a netlist whose bias sources were changed in place (harmonicaRF's <c>VdcOverride</c>,
/// an Idq search) re-freezes, and an unchanged one does nothing.</para>
/// </summary>
public static class BiasFreeze
{
    [ThreadStatic] private static int _freezeCount;

    /// <summary>How many freezes changed a frozen point, on this thread — the gates' counter.</summary>
    public static int FreezeCount => _freezeCount;

    /// <summary>Resets <see cref="FreezeCount"/>.</summary>
    public static void ResetCount() => _freezeCount = 0;

    /// <summary>Whether any component of <paramref name="netlist"/> needs a bias point.</summary>
    public static bool IsNeeded(ElaboratedNetlist netlist)
        => netlist.Components.Any(c => c.Model is IBiasFrozen { NeedsBiasPoint: true });

    /// <summary>Solves the quiescent DC point and freezes every model that needs one; nothing at all when none does.</summary>
    public static void Ensure(ElaboratedNetlist netlist, AnalysisSettings? settings = null)
    {
        if (!IsNeeded(netlist)) return;

        NonlinearDcEngine.DcResult? dc = null;
        try { dc = NonlinearDcEngine.Run(netlist, settings); }
        catch (NonlinearDcNotConvergedException) { }
        Apply(netlist, dc is { Converged: true } ? dc.NodeVoltages : null, dc?.NodeVoltages);
    }

    /// <summary>
    /// Freezes at a DC solution the caller already has (the S-parameter engine solves one of its own). Null means the
    /// solve did not converge: the point falls back to <paramref name="fallback"/>, or 0 V, and the run says so once.
    /// </summary>
    public static void Apply(ElaboratedNetlist netlist, double[]? nodeVoltages, double[]? fallback = null)
    {
        if (!IsNeeded(netlist)) return;
        if (nodeVoltages is null)
            netlist.AddWarningOnce("bias-freeze-nonconverged",
                "The DC operating point did not converge, so the linearised intrinsic charge (LinearC) is taken at " +
                (fallback is null ? "0 V" : "the last DC iterate") + " rather than at the quiescent bias.");
        var v = nodeVoltages ?? fallback;

        bool changed = false;
        foreach (var c in netlist.Components)
        {
            if (c.Model is not IBiasFrozen { NeedsBiasPoint: true } frozen) continue;
            int p = c.Model.PortCount;
            var ports = new double[p];
            for (int k = 0; k < p; k++)
                ports[k] = NodeV(v, c.Nodes.Length > 2 * k ? c.Nodes[2 * k] : 0)
                         - NodeV(v, c.Nodes.Length > 2 * k + 1 ? c.Nodes[2 * k + 1] : 0);
            changed |= frozen.FreezeAt(ports);
        }
        if (changed) _freezeCount++;
    }

    /// <summary>DC voltage of a 1-based circuit node (0 = ground).</summary>
    private static double NodeV(double[]? dc, int node)
        => dc is null || node <= 0 || node - 1 >= dc.Length ? 0.0 : dc[node - 1];
}
