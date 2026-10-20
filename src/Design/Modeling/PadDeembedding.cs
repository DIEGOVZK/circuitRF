// Pad (launch) removal — brief-modelcomposer-4 R-mc4-1; overview D5: the launch is never part of the model.
//
//   OpenShort  open and short dummy S-files: Y_dev = ((Y_m − Y_o)⁻¹ − (Y_s − Y_o)⁻¹)⁻¹, the standard parallel-then-series
//              subtraction.
//   TwoPorts   a Touchstone 2-port per side, port 1 at the probe and port 2 facing the device; removed by cascade
//              inversion, T_dev = T_gate⁻¹ · T_meas · reverse(T_drain)⁻¹ (the drain pad is met device side first).
//   Network    a two-pin .csch per side (pin 1 probe, pin 2 device), solved by the linear engine as a 2-port and then
//              removed as TwoPorts are. Its series resistance also drops DC (R-mc4-4 step 1).
//
// A pad whose frequency axis does not cover the data's is a refusal naming both ranges — never an extrapolation.
// Within the axis a pad is interpolated linearly in S, sample to sample; an exact sample is used as stored.

using System.Globalization;
using CircuitRF.Engine;
using CircuitRF.Engine.Modeling;
using RfCore;

namespace CircuitRF.Design.Modeling;

/// <summary>Loaded pads, ready to remove from any data set.</summary>
public sealed class PadDeembedding
{
    private sealed record Sampled(string Name, double[] Freqs, C2[] Values);

    private readonly Sampled? _open, _short, _gateFile, _drainFile;
    private readonly PadNetwork? _gateNet, _drainNet;

    public PadsKind Kind { get; }

    private PadDeembedding(PadsKind kind, Sampled? open = null, Sampled? shrt = null, Sampled? gate = null, Sampled? drain = null,
                           PadNetwork? gateNet = null, PadNetwork? drainNet = null)
    {
        Kind = kind; _open = open; _short = shrt; _gateFile = gate; _drainFile = drain; _gateNet = gateNet; _drainNet = drainNet;
    }

    public static PadDeembedding None { get; } = new(PadsKind.None);

    /// <summary>Reads the pads a document names, resolving each path against the <c>.cmc</c>'s folder.</summary>
    public static PadDeembedding Load(PadsSettings p, string cmcDirectory) => p.Kind switch
    {
        PadsKind.None => None,
        // Open and short are kept as Y; the two launches as ABCD — the forms their removal works in.
        PadsKind.OpenShort => new(p.Kind, open: ReadS(cmcDirectory, p.Open!, ToY), shrt: ReadS(cmcDirectory, p.Short!, ToY)),
        PadsKind.TwoPorts => new(p.Kind, gate: ReadS(cmcDirectory, p.Gate!, NetworkAlgebra.SToAbcd),
                                         drain: ReadS(cmcDirectory, p.Drain!, NetworkAlgebra.SToAbcd)),
        PadsKind.Network => new(p.Kind, gateNet: PadNetwork.Load(DataImport.ResolveSource(cmcDirectory, p.Gate!)),
                                        drainNet: PadNetwork.Load(DataImport.ResolveSource(cmcDirectory, p.Drain!))),
        _ => throw new ArgumentOutOfRangeException(nameof(p)),
    };

    private static C2 ToY(C2 s, double z0) => NetworkAlgebra.SToY(s, z0);

    private static Sampled ReadS(string dir, string source, Func<C2, double, C2> convert)
    {
        string path = DataImport.ResolveSource(dir, source);
        if (!File.Exists(path)) throw new ModelingException(source, "the file does not exist.");
        SNP snp;
        try { snp = TouchstoneIO.ReadFile(path); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { throw new ModelingException(source, $"it is not a readable Touchstone file ({ex.Message})."); }
        if (snp.IsEmpty) throw new ModelingException(source, "it holds no frequencies.");
        if (snp.Type != MatrixType.S) throw new ModelingException(source, $"it holds {snp.Type}-parameters; a pad file is read as S.");
        if (snp.Ports != 2) throw new ModelingException(source, $"it is a {snp.Ports}-port; a pad is a 2-port.");
        if (snp.Z0.Imaginary != 0 || snp.Z0PerPort is not null)
            throw new ModelingException(source, "its reference impedance is not one real value.");
        double z0 = snp.Z0.Real;
        var values = snp.Matrices.Select(m => convert(new C2(m[0, 0], m[0, 1], m[1, 0], m[1, 1]), z0)).ToArray();
        return new Sampled(source, snp.Frequencies, values);
    }

    /// <summary>
    /// <paramref name="data"/>'s S-parameters moved from the probe plane to the device plane. A set without S is
    /// returned unchanged — pads carry DC only through <see cref="DcDrop"/>.
    /// </summary>
    public NormalisedData Remove(NormalisedData data, string setName, RunControl? rc = null)
    {
        if (Kind == PadsKind.None || data.S is null) return data;
        if (data.Ports != 2) throw new ModelingException(setName, $"it is a {data.Ports}-port; pads are removed from a FET's 2-port.");
        double[] f = data.FrequenciesHz!;
        Func<int, C2, C2> remove;
        switch (Kind)
        {
            case PadsKind.OpenShort:
            {
                var yo = At(_open!, f, setName); var ys = At(_short!, f, setName);
                remove = (k, s) => NetworkAlgebra.YToS(NetworkAlgebra.OpenShort(NetworkAlgebra.SToY(s, data.Z0), yo[k], ys[k]), data.Z0);
                break;
            }
            case PadsKind.TwoPorts:
            {
                var g = At(_gateFile!, f, setName); var d = At(_drainFile!, f, setName);
                remove = Cascade(g, d, data.Z0);
                break;
            }
            default:
            {
                var g = _gateNet!.Abcd(f, rc); var d = _drainNet!.Abcd(f, rc);
                remove = Cascade(g, d, data.Z0);
                break;
            }
        }

        var s = (System.Numerics.Complex[])data.S.Clone();
        for (int p = 0; p < data.Count; p++)
        {
            rc?.ThrowIfCancellationRequested();
            for (int k = 0; k < f.Length; k++)
            {
                int o = (p * f.Length + k) * 4;
                var r = remove(k, new C2(s[o], s[o + 1], s[o + 2], s[o + 3]));
                (s[o], s[o + 1], s[o + 2], s[o + 3]) = (r.A, r.B, r.C, r.D);
            }
        }
        return new NormalisedData { Points = data.Points, Ports = 2, FrequenciesHz = f, S = s, Z0 = data.Z0, C = data.C };
    }

    /// <summary>
    /// The inverse of <see cref="Remove"/> (MC-11): <paramref name="data"/>'s S moved from the device plane back to the probe
    /// plane through the same pads, so a model's S is compared with the data where it was measured.
    /// </summary>
    public NormalisedData Embed(NormalisedData data, string setName, RunControl? rc = null)
    {
        if (Kind == PadsKind.None || data.S is null) return data;
        double[] f = data.FrequenciesHz!;
        Func<int, C2, C2> embed;
        switch (Kind)
        {
            case PadsKind.OpenShort:
            {
                var yo = At(_open!, f, setName); var ys = At(_short!, f, setName);
                embed = (k, s) => NetworkAlgebra.YToS(
                    yo[k] + (NetworkAlgebra.SToY(s, data.Z0).Inverse() + (ys[k] - yo[k]).Inverse()).Inverse(), data.Z0);
                break;
            }
            case PadsKind.TwoPorts:
                embed = Uncascade(At(_gateFile!, f, setName), At(_drainFile!, f, setName), data.Z0);
                break;
            default:
                embed = Uncascade(_gateNet!.Abcd(f, rc), _drainNet!.Abcd(f, rc), data.Z0);
                break;
        }
        var s = (System.Numerics.Complex[])data.S.Clone();
        for (int p = 0; p < data.Count; p++)
            for (int k = 0; k < f.Length; k++)
            {
                int o = (p * f.Length + k) * 4;
                var r = embed(k, new C2(s[o], s[o + 1], s[o + 2], s[o + 3]));
                (s[o], s[o + 1], s[o + 2], s[o + 3]) = (r.A, r.B, r.C, r.D);
            }
        return new NormalisedData { Points = data.Points, Ports = 2, FrequenciesHz = f, S = s, Z0 = data.Z0, C = data.C };
    }

    private static Func<int, C2, C2> Uncascade(C2[] gate, C2[] drain, double z0)
    {
        var drainRev = drain.Select(NetworkAlgebra.Reverse).ToArray();
        return (k, s) => NetworkAlgebra.AbcdToS(gate[k] * NetworkAlgebra.SToAbcd(s, z0) * drainRev[k], z0);
    }

    private static Func<int, C2, C2> Cascade(C2[] gate, C2[] drain, double z0)
    {
        var drainInv = drain.Select(t => NetworkAlgebra.Reverse(t).Inverse()).ToArray();
        var gateInv = gate.Select(t => t.Inverse()).ToArray();
        return (k, s) => NetworkAlgebra.AbcdToS(gateInv[k] * NetworkAlgebra.SToAbcd(s, z0) * drainInv[k], z0);
    }

    /// <summary>The pad at every data frequency, or a refusal naming both ranges when it does not cover them.</summary>
    private static C2[] At(Sampled pad, double[] f, string setName)
    {
        double lo = f.Min(), hi = f.Max(), plo = pad.Freqs[0], phi = pad.Freqs[^1];
        double tol = 1e-9 * Math.Max(Math.Abs(phi), 1);
        if (lo < plo - tol || hi > phi + tol)
            throw new ModelingException(pad.Name,
                $"it spans {Ghz(plo)} to {Ghz(phi)} GHz and {setName} spans {Ghz(lo)} to {Ghz(hi)} GHz; a pad is never extrapolated.");
        return [.. f.Select(x => NetworkAlgebra.Interpolate(pad.Freqs, pad.Values, Math.Clamp(x, plo, phi)))];
    }

    private static string Ghz(double hz) => (hz / 1e9).ToString("G6", CultureInfo.InvariantCulture);

    /// <summary>
    /// The DC move from the probe plane to the device plane (R-mc4-4 step 1): a network pad's series resistance drops
    /// volts with the terminal current; Touchstone pads carry no DC. The source returns through ground.
    /// </summary>
    public Func<double, double, double, double, (double Vgs, double Vds)> DcDrop()
    {
        if (Kind != PadsKind.Network) return (vgs, vds, _, _) => (vgs, vds);
        var (ga, gb) = _gateNet!.Dc();
        var (da, db) = _drainNet!.Dc();
        return (vgs, vds, ig, id) => (ga * vgs + gb * ig, da * vds + db * id);
    }
}
