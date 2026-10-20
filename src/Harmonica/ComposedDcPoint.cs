using CircuitRF.Core;
using CircuitRF.Core.Devices.Composed;

namespace CircuitRF.Harmonica;

/// <summary>
/// brief-modelcomposer-23 R-mc23-5 — a composed core's DC operating point at given intrinsic port voltages: what
/// <see cref="DcivFamily"/> evaluates for a Model Composer DUT.
///
/// <para><b>Why a DC point needs more than <c>(vgs, vds)</c>.</b> The core carries state ports past its two intrinsic
/// ones — the dispersion node <c>x</c>, the trap states, the thermal node — and at DC each sits at its own equilibrium:
/// <c>x = IdDC − IdRF</c>, each trap state at its DC value, and the thermal rise at <c>ΔT = Rth,DC · P(vgs, vds, ΔT)</c>.
/// Evaluating the core with them at zero would draw a family of a different device. They are solved together by a small
/// Newton on the core's own currents and Jacobian — each state node's KCL, and the thermal node's against
/// <c>Rth,DC</c> — which is exactly what the circuit's DC solve does at those nodes, since nothing else attaches to them
/// at DC. Trap states start from <see cref="ComposedCoreProgram.DcSeeds"/>'s values, so the Newton starts at their answer
/// and the one coupling it actually resolves is the temperature.</para>
/// </summary>
public static class ComposedDcPoint
{
    private const int MaxIterations = 60;

    /// <summary>
    /// <c>Rth,DC</c>, K/W: the sum of the model's Foster ladder at the run's ambient (clamped to the model's range as the
    /// elaborator clamps it), scaled to <c>N</c> fingers. Zero for a model that does not self-heat.
    /// </summary>
    public static double ThermalResistance(ComposedCoreModel core, double ambientC)
    {
        var model = core.Loaded.Model;
        if (model.Thermal is not { } thermal) return 0.0;
        var t = model.Temperature;
        double ambient = t.Mode == TemperatureMode.Range ? Math.Clamp(ambientC, t.Tmin, t.Tmax) : ambientC;
        return thermal.RthAt(ambient) * PeripheryScaling.Factor(PeripheryScaling.PerFingerInverse, core.N, core.Loaded.N0);
    }

    /// <summary>
    /// The core's whole port vector at DC with <c>vgs</c>, <c>vds</c> held: the state and thermal ports at their
    /// equilibrium. <paramref name="rthDc"/> is <see cref="ThermalResistance"/>; with <c>SelfHeating=false</c> the rise is
    /// zero. Reads no charge.
    /// </summary>
    public static double[] Solve(ComposedCoreModel core, double vgs, double vds, double rthDc)
    {
        int p = core.PortCount;
        var v = new double[p];
        v[0] = vgs;
        v[1] = vds;

        int states = core.InternalStateNodeCount;
        int th     = core.ThermalPort;
        bool heats = th >= 0 && core.Switches.SelfHeating && rthDc > 0;

        // Trap states from their DC values at the ambient — the seed DcSeeds gives the composer's own plots.
        if (core.Loaded.Program.Traps is { } traps)
        {
            var names = core.TerminalNames;
            for (int j = 0; j < states; j++)
            {
                int k = IndexOf(traps.States, names[2 * (2 + j)]);
                if (k >= 0) v[2 + j] = traps.DcValue(k, vgs, vds, core.TemperatureC);
            }
        }

        var unknowns = new List<int>(states + 1);
        for (int j = 0; j < states; j++) unknowns.Add(2 + j);
        if (heats) unknowns.Add(th);
        if (unknowns.Count == 0) return v;

        int n = unknowns.Count;
        var a = new double[n, n];
        var r = new double[n];
        using (ChargeReading.NotRead())
        {
            for (int it = 0; it < MaxIterations; it++)
            {
                var res = core.Evaluate(new PortVoltages(v));
                for (int i = 0; i < n; i++)
                {
                    int u = unknowns[i];
                    r[i] = res.I[u] + (u == th ? v[th] / rthDc : 0.0);
                    for (int j = 0; j < n; j++)
                        a[i, j] = res.Dg[u, unknowns[j]] + (u == th && unknowns[j] == th ? 1.0 / rthDc : 0.0);
                }

                var step = SolveDense(a, r);
                if (step is null) break;

                double worst = 0;
                for (int i = 0; i < n; i++)
                {
                    double d = -step[i];
                    // A thermal step is bounded so a far seed cannot throw the rise past the model's validity in one go.
                    if (unknowns[i] == th) d = Math.Clamp(d, -50.0, 50.0);
                    v[unknowns[i]] += d;
                    worst = Math.Max(worst, Math.Abs(d) / (1.0 + Math.Abs(v[unknowns[i]])));
                }
                if (worst < 1e-14) break;
            }
        }
        return v;
    }

    private static int IndexOf(IReadOnlyList<string> names, string name)
    {
        for (int k = 0; k < names.Count; k++)
            if (string.Equals(names[k], name, StringComparison.Ordinal)) return k;
        return -1;
    }

    /// <summary>Gauss elimination with partial pivoting on a copy; null when singular.</summary>
    private static double[]? SolveDense(double[,] a0, double[] b0)
    {
        int n = b0.Length;
        var a = (double[,])a0.Clone();
        var b = (double[])b0.Clone();
        for (int c = 0; c < n; c++)
        {
            int piv = c;
            for (int i = c + 1; i < n; i++) if (Math.Abs(a[i, c]) > Math.Abs(a[piv, c])) piv = i;
            if (a[piv, c] == 0) return null;
            if (piv != c)
            {
                for (int j = 0; j < n; j++) (a[c, j], a[piv, j]) = (a[piv, j], a[c, j]);
                (b[c], b[piv]) = (b[piv], b[c]);
            }
            for (int i = c + 1; i < n; i++)
            {
                double f = a[i, c] / a[c, c];
                if (f == 0) continue;
                for (int j = c; j < n; j++) a[i, j] -= f * a[c, j];
                b[i] -= f * b[c];
            }
        }
        var x = new double[n];
        for (int i = n - 1; i >= 0; i--)
        {
            double s = b[i];
            for (int j = i + 1; j < n; j++) s -= a[i, j] * x[j];
            x[i] = s / a[i, i];
        }
        return x;
    }
}
