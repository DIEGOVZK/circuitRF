using System.Numerics;
using CircuitRF.Design.Modeling.Passive;

namespace CircuitRF.Ui.Tests.ModelComposer.Passive;

/// <summary>Passive blocks for the MC-12 gates: S of a library topology at known values, solved by the engine, and the
/// closed-form S of a series impedance between two ports.</summary>
internal static class SyntheticPassiveData
{
    public static double[] Log(double lo, double hi, int n) =>
        [.. Enumerable.Range(0, n).Select(k => lo * Math.Pow(hi / lo, (double)k / (n - 1)))];

    public static PassiveBlock FromTopology(string id, IReadOnlyDictionary<string, double> values, double[] f, string? name = null,
                                            double geometry = double.NaN) =>
        new(name ?? id, f, 50, PassiveTopologies.Find(id).Load().S(f, 50, values)) { Geometry = geometry };

    /// <summary>A series impedance Z(f) between port 1 and port 2: S11 = S22 = Z/(Z + 2Z0), S21 = S12 = 2Z0/(Z + 2Z0).</summary>
    public static PassiveBlock Series(Func<double, Complex> z, double[] f, double z0 = 50) =>
        new("series", f, z0, [.. f.Select(x =>
        {
            var zz = z(x);
            var s11 = zz / (zz + 2 * z0);
            var s21 = 2 * z0 / (zz + 2 * z0);
            return new Complex[,] { { s11, s21 }, { s21, s11 } };
        })]);
}
