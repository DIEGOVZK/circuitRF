using System.Numerics;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Modeling;
using RfCore.Data;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// The large-signal side of the series acceptance (R-mc16-4): one class-AB stage at 2 GHz, 50 Ω at the fundamental and
/// shorted harmonics on both sides, run once with the exported model and once with <see cref="SyntheticGan"/>'s truth in
/// its place — the benches the Validate step itself generates (<see cref="ValidationBench"/>), so the comparison is the
/// one a user sees.
/// </summary>
public static class AcceptanceBenches
{
    public const double F0 = 2e9, Vgs = -0.8, Vds = 8, Spacing = 10e6;

    public static LargeSignalConditions Conditions(LargeSignalMeasurement m) => new()
    {
        Measurement = m, FrequencyHz = F0, SpacingHz = m == LargeSignalMeasurement.TwoTone ? Spacing : null,
        Vgs = Vgs, Vds = Vds, SourceZ = 50, LoadZ = 50,
    };

    /// <summary>The model's device line, or the truth's at <paramref name="n"/> fingers with its definitions to prepend.</summary>
    public static (string Prefix, string Device) Model(string cmodel, int n) => ("", ValidationBench.Device(cmodel, n, false, "gx", "dx"));
    public static (string Prefix, string Device) Truth(int n) => (SyntheticGan.Globals(null) + SyntheticGan.Device(n), "gan:X1 gx dx 0\n");

    public sealed record Sweep(double[] Pin, double[] PoutDbm, double[] Pae, double[] GainDb);

    public static Sweep PowerSweep((string Prefix, string Device) d, double[] pin, string dir)
    {
        string cnl = d.Prefix + ValidationBench.PowerSweep("acceptance", d.Device, 25, Conditions(LargeSignalMeasurement.PowerSweep), pin, 5);
        var (pout, pinDel, _, _, pdc) = ModelValidation.Powers(Run(cnl, dir), 1);
        double[] dbm = [.. pout.Select(ModelValidation.Dbm)];
        return new Sweep(pin, dbm, [.. pout.Select((p, k) => 100 * (p - pinDel[k]) / pdc[k])], [.. dbm.Select((p, k) => p - pin[k])]);
    }

    /// <summary>The carrier and the lower IM3 at the load, dBm, per drive level.</summary>
    public static (double[] Carrier, double[] Im3) TwoTone((string Prefix, string Device) d, double[] pin, string dir)
    {
        var c = Conditions(LargeSignalMeasurement.TwoTone);
        string cnl = d.Prefix + ValidationBench.TwoTone("acceptance", d.Device, 25, c, pin, 5);
        var ds = Run(cnl, dir);
        double f1 = F0 - Spacing / 2, f2 = F0 + Spacing / 2;
        int Mix(double hz)
        {
            var v = ds["V"];
            int node = v.Axes.ToList().FindIndex(a => a.Name == "node");
            var mix = v.Axes[node + 1].Values;
            for (int m = 0; m < mix.Length; m++) if (Math.Abs(mix[m] - hz) < 1) return m;
            throw new InvalidOperationException($"no {hz} Hz in the spectrum");
        }
        return ([.. ModelValidation.Powers(ds, Mix(f1)).Pout.Select(ModelValidation.Dbm)],
                [.. ModelValidation.Powers(ds, Mix(2 * f1 - f2)).Pout.Select(ModelValidation.Dbm)]);
    }

    private static DataSet Run(string cnl, string dir)
    {
        var r = CircuitEvaluation.Evaluate(PreparedCircuit.FromText(cnl, dir, dir), new CircuitEvaluationRequest { Measurements = false });
        if (r.Status != RunStatus.Success) throw new InvalidOperationException($"{r.Status}: {r.StatusMessage}\n{cnl}");
        return r.Results.First(x => x.Data.Contains("V")).Data;
    }

    /// <summary>The first drive at which the gain has fallen <paramref name="db"/> below its first value; the last drive
    /// when it never does.</summary>
    public static int CompressionIndex(Sweep s, double db)
    {
        for (int k = 0; k < s.Pin.Length; k++) if (s.GainDb[0] - s.GainDb[k] >= db) return k;
        return s.Pin.Length - 1;
    }

    /// <summary>The 95th percentile of |ΔS| between a validation item's model and data.</summary>
    public static double S95(DataSet ds)
    {
        Complex[] meas = ds["S.meas"].ComplexValues, model = ds["S.model"].ComplexValues;
        var d = meas.Select((m, k) => Complex.Abs(m - model[k])).Where(double.IsFinite).Order().ToList();
        return d[(int)Math.Ceiling(0.95 * d.Count) - 1];
    }

    /// <summary>A DC validation item's largest |ΔId| as a fraction of the data's drain-current range.</summary>
    public static double DcMaxRelative(DataSet ds)
    {
        double[] meas = ds["Id.meas"].RealValues, model = ds["Id.model"].RealValues;
        double range = meas.Max() - meas.Min();
        return meas.Select((m, k) => Math.Abs(m - model[k])).Max() / range;
    }
}
