// The intrinsic small-signal quantities and the frequency-flatness metric (brief-modelcomposer-4 R-mc4-5).
//
// From Y_int per bias and frequency, with ω = 2πf and the book's sign convention ([P1] eqs. 6.22, 6.24):
//   Gm = Re Y21    Gds = Re Y22    Cij = Im Yij / ω
//   Cgs = C11 + C12    Cgd = −C12    Cds = C22 + C12    Cm = C21 − C12
//   τ = −Cm / Gm   the transconductance delay Cm represents to first order ([P1] eqs. 5.18, 5.21) — a readout only;
//                  the model carries it as Cm in the charges (D4).
//
// A correct extrinsic network leaves every one of these flat in frequency ([P1] §5.6.1–5.6.3): the intrinsic model is
// frequency-independent. The flatness metric — spread across a band ÷ mean — is what MC-5 minimises.

using CircuitRF.Engine.Modeling;

namespace CircuitRF.Design.Modeling;

public static class IntrinsicQuantities
{
    /// <summary>The quantities, in the order the results' cubes and the flatness axis list them.</summary>
    public static readonly IReadOnlyList<string> Names =
        ["Gm", "Gds", "C11", "C12", "C21", "C22", "Cgs", "Cgd", "Cds", "Cm", "Tau"];

    /// <summary>Every quantity of <see cref="Names"/> from one intrinsic admittance at <paramref name="freqHz"/>.</summary>
    public static double[] Of(C2 y, double freqHz)
    {
        double w = 2 * Math.PI * freqHz;
        double c11 = y.A.Imaginary / w, c12 = y.B.Imaginary / w, c21 = y.C.Imaginary / w, c22 = y.D.Imaginary / w;
        double gm = y.C.Real, gds = y.D.Real, cm = c21 - c12;
        return [gm, gds, c11, c12, c21, c22, c11 + c12, -c12, c22 + c12, cm, -cm / gm];
    }

    /// <summary>The default flatness band: above 1/10 of the data's top frequency and below 1/2 of it (R-mc4-5).</summary>
    public static (double Lo, double Hi) DefaultBand(IReadOnlyList<double> freqsHz)
    {
        double top = freqsHz.Max();
        return (top / 10, top / 2);
    }

    /// <summary>
    /// Spread ÷ mean, <c>(max − min) / |mean|</c>, over the frequencies inside <paramref name="band"/> that are not
    /// flagged. NaN when fewer than two remain, or the mean is zero.
    /// </summary>
    public static double Flatness(IReadOnlyList<double> values, IReadOnlyList<double> freqsHz, (double Lo, double Hi) band,
                                  IReadOnlyList<bool>? flagged = null)
    {
        double min = double.PositiveInfinity, max = double.NegativeInfinity, sum = 0;
        int n = 0;
        for (int k = 0; k < values.Count; k++)
        {
            if (freqsHz[k] < band.Lo || freqsHz[k] > band.Hi || flagged?[k] == true || !double.IsFinite(values[k])) continue;
            min = Math.Min(min, values[k]);
            max = Math.Max(max, values[k]);
            sum += values[k];
            n++;
        }
        double mean = sum / n;
        return n < 2 || mean == 0 ? double.NaN : (max - min) / Math.Abs(mean);
    }
}
