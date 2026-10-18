// brief-sar-2 R-sar2-1 — the multi-term Cole-Cole dispersion of a lossy dielectric, the form Gabriel, Lau and Gabriel
// fit body tissues with (Phys. Med. Biol. 41 (1996) 2271–2293, their eq. 3):
//
//     ε̂(ω) = ε∞ + Σₙ Δεₙ / (1 + (jωτₙ)^(1−αₙ)) + σᵢ / (jωε₀)
//
// with the e^{jωt} convention, so a lossy medium has Im ε̂ < 0. What a solver takes is the pair the medium shows at one
// frequency: εr = Re ε̂ and σ = −ωε₀·Im ε̂, the total conductivity (ionic plus every dispersion's loss) — so a solver
// given this σ must be given no loss tangent beside it. Numbers only: the technology record that holds the parameters
// is a design type and stays in src/Design.

using System.Numerics;
using CircuitRF.Engine.Mom;

namespace CircuitRF.Engine.Em3d;

/// <summary>One Cole-Cole term: its strength Δε, its relaxation time τ in seconds and its broadening α (0 is Debye).</summary>
public readonly record struct ColeColeTerm(double DeltaEps, double TauSeconds, double Alpha);

/// <summary>The Cole-Cole dispersion, evaluated (brief-sar-2 R-sar2-1).</summary>
public static class ColeCole
{
    /// <summary>The complex relative permittivity ε̂ at <paramref name="frequencyHz"/> (&gt; 0).</summary>
    public static Complex Permittivity(double frequencyHz, double epsInf, double sigmaIonicSm, IReadOnlyList<ColeColeTerm> terms)
    {
        if (!(frequencyHz > 0) || !double.IsFinite(frequencyHz))
            throw new ArgumentOutOfRangeException(nameof(frequencyHz), frequencyHz, "A dispersion is evaluated at a positive frequency.");
        double w = 2 * Math.PI * frequencyHz;
        Complex eps = epsInf;
        foreach (var t in terms)
        {
            if (t.DeltaEps == 0) continue;
            eps += t.DeltaEps / (1 + Complex.Pow(new Complex(0, w * t.TauSeconds), 1 - t.Alpha));
        }
        return eps + sigmaIonicSm / new Complex(0, w * EmConstants.Eps0);
    }

    /// <summary>The relative permittivity and the total conductivity (S/m) at <paramref name="frequencyHz"/>.</summary>
    public static (double Epsr, double SigmaSm) EpsrSigma(double frequencyHz, double epsInf, double sigmaIonicSm,
                                                          IReadOnlyList<ColeColeTerm> terms)
    {
        var eps = Permittivity(frequencyHz, epsInf, sigmaIonicSm, terms);
        return (eps.Real, -2 * Math.PI * frequencyHz * EmConstants.Eps0 * eps.Imaginary);
    }
}
