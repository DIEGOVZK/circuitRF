namespace CircuitRF.Core.Devices.Composed.Traps;

/// <summary>
/// The built-in <c>CaptureEmission</c> model (brief-modelcomposer-10 R-mc10-2; [P1] eqs. 6.73–6.74, [X10] eqs. 1.3–1.4),
/// with two independently switchable states:
/// <list type="bullet">
/// <item><b>gate-lag <c>phi1</c></b>: <c>dphi1/dt = (vgs − phi1)/τ_emit,1 − f1(phi1 − vgs)</c> — it relaxes towards
/// <c>vgs</c> always, and is captured downwards whenever <c>vgs &lt; phi1</c>;</item>
/// <item><b>drain-lag <c>phi2</c></b>: <c>dphi2/dt = (vds − phi2)/τ_emit,2 + f2(vds − phi2)</c> — captured upwards
/// whenever <c>vds &gt; phi2</c>.</item>
/// </list>
///
/// <para><b>The capture term</b> is diode-like in the gap <c>x</c>:
/// <c>f(x) = (x_ref/τ_capt)·(e^((x − x_ref)/Vc) − e^(−x_ref/Vc))</c>. <c>f(0) = 0</c>, so the DC state is exactly the
/// voltage; for a negative gap it is at most <c>(x_ref/τ_capt)·e^(−x_ref/Vc)</c> (0.2 V/s with the defaults), far below
/// emission's <c>|x|/τ_emit</c> beyond a millivolt; and capture slows as the gap closes — 10 ns at a 1 V gap, ~0.1 ms at
/// 0.5 V ([G19]'s fast-then-slow capture). An unshifted softplus would be <c>ε/2</c> at zero and park the DC state away
/// from the voltage. The exponential is continued by its tangent once it exceeds e^<see cref="ExpLimit"/> — a capture
/// time ~2e-9 of <c>τ_capt</c> — so a Newton start a whole drain voltage away stays finite; there the term is C¹, below it
/// C∞, and no solution ever sits there.</para>
///
/// <para><b>Emission</b> is <c>Constant</c>, or <c>ThermallyActivated</c> when the state's <c>Ea</c> is given:
/// <c>1/τ_emit(T) = (1/τ_emit)·(T/Tref)²·e^(−(Ea/k)(1/T − 1/Tref))</c> ([N22] eq. 3), T in KELVIN although the model
/// states <c>Tref</c> in °C like every other temperature. Capture stays fixed: [N22] finds its temperature dependence
/// negligible for PA prediction.</para>
/// </summary>
public sealed class CaptureEmissionTraps : TrapModelDefinition
{
    public const string ModelName = "CaptureEmission";
    public const string GateLag = "phi1", DrainLag = "phi2";

    public const double DefaultTauCapt = 1e-8, DefaultTauEmit = 1e-3, DefaultXref = 1.0, DefaultVc = 0.05, DefaultTref = 25;

    /// <summary>Boltzmann's constant in eV/K.</summary>
    public const double BoltzmannEv = 8.617333262e-5;

    /// <summary>Where the capture exponential is continued by its tangent.</summary>
    public const double ExpLimit = 20;

    public override string Name => ModelName;
    public override IReadOnlyList<string> States { get; } = [GateLag, DrainLag];

    public override IReadOnlyList<TrapParameter> Parameters { get; } =
    [
        new("TauCapt1", "s", DefaultTauCapt, "gate-lag capture time at the reference gap"),
        new("TauEmit1", "s", DefaultTauEmit, "gate-lag emission time (at Tref when thermally activated)"),
        new("Ea1", "eV", null, "gate-lag activation energy; present = thermally activated emission"),
        new("TauCapt2", "s", DefaultTauCapt, "drain-lag capture time at the reference gap"),
        new("TauEmit2", "s", DefaultTauEmit, "drain-lag emission time (at Tref when thermally activated)"),
        new("Ea2", "eV", null, "drain-lag activation energy; present = thermally activated emission"),
        new("Xref", "V", DefaultXref, "the reference gap at which capture takes TauCapt"),
        new("Vc", "V", DefaultVc, "how fast capture slows as the gap closes"),
        new("Tref", "°C", DefaultTref, "the temperature at which TauEmit holds"),
    ];

    public override TrapDynamics Build(ModelTraps traps, string label)
    {
        double Positive(string name, string what)
        {
            double v = ValueOf(traps, name);
            return v > 0 ? v : throw new CmodelFormatException($"Traps.Parameters.{name}", $"it is not a positive {what}.");
        }
        double? Ea(string name)
        {
            if (traps.Parameter(name) is not { } v) return null;
            return v >= 0 ? v : throw new CmodelFormatException($"Traps.Parameters.{name}", "an activation energy is not negative.");
        }

        double xref = Positive("Xref", "gap in volts"), vc = Positive("Vc", "voltage");
        double trefK = ValueOf(traps, "Tref") + 273.15;
        if (!(trefK > 0)) throw new CmodelFormatException("Traps.Parameters.Tref", "it is below absolute zero.");
        var states = new State[traps.States.Count];
        for (int k = 0; k < states.Length; k++)
        {
            bool gate = traps.States[k] == GateLag;
            string n = gate ? "1" : "2";
            states[k] = new State(gate, Positive("TauCapt" + n, "time in seconds"), Positive("TauEmit" + n, "time in seconds"),
                                  Ea("Ea" + n));
        }
        return new Dynamics(traps.States, states, xref, vc, trefK);
    }

    private sealed record State(bool Gate, double TauCapt, double TauEmit, double? Ea);

    private sealed class Dynamics(IReadOnlyList<string> names, State[] states, double xref, double vc, double trefK)
        : RateTrapDynamics(names)
    {
        private readonly double _floor = Math.Exp(-xref / vc);

        /// <summary><c>f(x)</c> and <c>f′(x)</c> for one state.</summary>
        private (double F, double D) Capture(State s, double x)
        {
            double u = (x - xref) / vc, e, de;
            if (u <= ExpLimit) { e = Math.Exp(u); de = e; }
            else
            {
                double top = Math.Exp(ExpLimit);
                e = top * (1 + (u - ExpLimit));
                de = top;
            }
            double k = xref / s.TauCapt;
            return (k * (e - _floor), k * de / vc);
        }

        /// <summary><c>1/τ_emit</c> at <paramref name="tC"/> °C and its derivative by T.</summary>
        private (double R, double D) Emission(State s, double tC)
        {
            double r0 = 1 / s.TauEmit;
            if (s.Ea is not { } ea) return (r0, 0);
            double tk = tC + 273.15;
            double r = r0 * (tk / trefK) * (tk / trefK) * Math.Exp(-ea / BoltzmannEv * (1 / tk - 1 / trefK));
            return (r, r * (2 / tk + ea / (BoltzmannEv * tk * tk)));
        }

        public override double Rate(int state, double phi, double vgs, double vds, double t, Span<double> grad)
        {
            var s = states[state];
            var (re, dre) = Emission(s, t);
            if (s.Gate)
            {
                // Emission towards vgs; capture downwards through the gap phi1 − vgs.
                var (f, df) = Capture(s, phi - vgs);
                grad[0] = -re - df;
                grad[1] = re + df;
                grad[2] = 0;
                grad[3] = (vgs - phi) * dre;
                return (vgs - phi) * re - f;
            }
            else
            {
                // Emission towards vds; capture upwards through the gap vds − phi2.
                var (f, df) = Capture(s, vds - phi);
                grad[0] = -re - df;
                grad[1] = 0;
                grad[2] = re + df;
                grad[3] = (vds - phi) * dre;
                return (vds - phi) * re + f;
            }
        }

        public override double DcValue(int state, double vgs, double vds, double t) => states[state].Gate ? vgs : vds;

        public override int CaptureSign(int state) => states[state].Gate ? -1 : +1;

        public override double? EmissionTime(int state, double t) => 1 / Emission(states[state], t).R;
    }
}
