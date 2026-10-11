// The phase a magnitude implies — brief-img-14-sparameter-plots-to-touchstone.md R-im14-10.
//
// A minimum-phase response's phase is the Hilbert transform of its log magnitude. It is computed here by the cepstral
// method: ln|S| on a uniform grid, mirrored to a real even sequence, inverse transformed (the real cepstrum), folded
// onto positive quefrency (doubled there, zeroed at negative), and transformed back; the imaginary part of the result
// is the minimum phase. The FFT is an in-house radix-2 one: the harmonic-balance FFT carries a frozen amplitude
// convention for real time signals that this does not want.
//
// THE GRID IS THE BILINEAR MAP OF THE WHOLE AXIS, f = f_top·tan(θ/2) for θ in [0, π]: the band is the first half of the
// DFT circle and everything above it, out to infinity, the second. The bilinear map takes a minimum-phase response to a
// minimum-phase one with the SAME phase at corresponding frequencies, so the discrete transform's answer is the
// continuous one's, with no tail left out. A uniform grid in f cut off at a multiple of the band instead loses the
// Hilbert integral's tail above the cut, which comes back as a phase error growing linearly with f — 4.5° at 90 % of
// the band for a two-pole low-pass with the cut at 32× the top (src/Design/RESOLVED.md).
//
// THE BAND IS EXTENDED, because the transform sees the whole axis and a picture shows a band. Below the first point the
// first value is held down to DC. Above the last point the magnitude is continued at its own end slope on log-log axes
// — the roll-off a physical response keeps — never rising above where it ended (a passive response does not climb out
// of band). Holding the top value instead would read a two-pole roll-off as flat and put ~75° of error at 90 % of the
// band; the measured slope keeps it within about a degree.
//
// The estimate is still worst at the band edges, where the extension is a guess: the report states the middle
// TrustedShare of the band as where it is trustworthy, and every value written from it is labelled estimated.

using System.Numerics;

namespace CircuitRF.Design.Imaging.Plots;

/// <summary>A minimum-phase estimate and the band it is trusted over.</summary>
/// <param name="Phase">Radians, at each input frequency.</param>
public sealed record MinimumPhaseResult(double[] Phase, double TrustedFromHz, double TrustedToHz);

public static class MinimumPhase
{
    /// <summary>The middle share of the band reported as trustworthy (R-im14-10).</summary>
    public const double TrustedShare = 0.8;

    /// <summary>The transform's length: 2^18 points, half of them across the band itself.</summary>
    public const int Points = 1 << 18;

    /// <summary>The steepest end slope continued, nepers per neper (±120 dB/decade).</summary>
    public const double MaxSlope = 6.0;

    /// <summary>The minimum phase of the magnitude <paramref name="magnitude"/> at the ascending frequencies
    /// <paramref name="frequencyHz"/>. Pure.</summary>
    public static MinimumPhaseResult FromMagnitude(IReadOnlyList<double> frequencyHz, IReadOnlyList<double> magnitude)
    {
        ArgumentNullException.ThrowIfNull(frequencyHz);
        ArgumentNullException.ThrowIfNull(magnitude);
        int m = frequencyHz.Count;
        if (m != magnitude.Count) throw new ArgumentException("One magnitude per frequency.");
        if (m < 2) return new MinimumPhaseResult(new double[m], frequencyHz.FirstOrDefault(), frequencyHz.FirstOrDefault());
        double f0 = frequencyHz[0], f1 = frequencyHz[^1];
        var lnMag = magnitude.Select(v => Math.Log(Math.Max(v, 1e-12))).ToArray();

        // The end slope on log-log axes, over the top tenth of the band.
        int top = m - 1, from = top;
        while (from > 0 && frequencyHz[from - 1] >= f1 - 0.1 * (f1 - f0)) from--;
        if (from == top) from = Math.Max(0, top - 1);
        double slope = 0;
        if (frequencyHz[from] > 0 && f1 > frequencyHz[from])
            slope = (lnMag[top] - lnMag[from]) / Math.Log(f1 / frequencyHz[from]);
        slope = Math.Clamp(slope, -MaxSlope, MaxSlope);

        int n = Points, half = n / 2;
        double dTheta = Math.PI / half;
        var spectrum = new Complex[n];
        int j = 0;
        for (int k = 0; k <= half; k++)
        {
            double f = f1 * Math.Tan(k * dTheta / 2), v;
            if (f <= f0) v = lnMag[0];
            else if (f <= f1)
            {
                while (j + 1 < m - 1 && frequencyHz[j + 1] < f) j++;
                double u = (f - frequencyHz[j]) / (frequencyHz[j + 1] - frequencyHz[j]);
                v = lnMag[j] + u * (lnMag[j + 1] - lnMag[j]);
            }
            else v = Math.Min(lnMag[top] + slope * Math.Log(f / f1), Math.Max(lnMag[top], 0));
            spectrum[k] = v;
            if (k > 0 && k < half) spectrum[n - k] = v;
        }

        // Real cepstrum, folded to positive quefrency, back to the spectrum.
        Fft(spectrum, inverse: true);
        var c = new Complex[n];
        c[0] = spectrum[0].Real;
        for (int k = 1; k < half; k++) c[k] = 2 * spectrum[k].Real;
        c[half] = spectrum[half].Real;
        Fft(c, inverse: false);

        var phase = new double[m];
        for (int i = 0; i < m; i++)
        {
            double pos = 2 * Math.Atan(frequencyHz[i] / f1) / dTheta;
            int k = Math.Min(half - 1, (int)Math.Floor(pos));
            double u = pos - k;
            phase[i] = c[k].Imaginary * (1 - u) + c[k + 1].Imaginary * u;
        }
        double edge = (1 - TrustedShare) / 2 * (f1 - f0);
        return new MinimumPhaseResult(phase, f0 + edge, f1 - edge);
    }

    /// <summary>In-place radix-2 FFT; the inverse is scaled by 1/n. The length must be a power of two.</summary>
    internal static void Fft(Complex[] a, bool inverse)
    {
        int n = a.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (a[i], a[j]) = (a[j], a[i]);
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = 2 * Math.PI / len * (inverse ? 1 : -1);
            var wl = new Complex(Math.Cos(ang), Math.Sin(ang));
            for (int i = 0; i < n; i += len)
            {
                var w = Complex.One;
                for (int k = 0; k < len / 2; k++)
                {
                    var u = a[i + k];
                    var v = a[i + k + len / 2] * w;
                    a[i + k] = u + v;
                    a[i + k + len / 2] = u - v;
                    w *= wl;
                }
            }
        }
        if (inverse)
            for (int i = 0; i < n; i++) a[i] /= n;
    }
}
