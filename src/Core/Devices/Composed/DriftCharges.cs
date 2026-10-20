namespace CircuitRF.Core.Devices.Composed;

/// <summary>
/// The drift-charge decomposition of [I11] eq. 3 as the core evaluates it (brief-modelcomposer-8 R-mc8-3), in [I11]'s
/// sign convention:
/// <code>
///   Qg =  QGS(vgs) + QGD(vgd) + Qdrift(vgd, Id)
///   Qd = −QGD(vgd) − (1 − λ)·Qdrift(vgd, Id) + QDS(vds)
/// </code>
/// with <c>vgd = vgs − vds</c> and <c>Id</c> the channel current the core evaluates at the same instant — <c>IdRF</c>
/// when the model is dispersive, because [I11] eq. 4's <c>gm + gDS</c> are RF conductances. Through <c>Id</c> the
/// charges depend on whatever the current depends on (temperature, trap states), by the chain rule.
///
/// <para>Both charges carry <c>Method: Drift</c> and name the same <c>QGD</c> and <c>Qdrift</c> networks; <c>Qd</c>
/// carries the partition factor as its one parameter, <c>Lambda</c>. Each piece is an ANN of its own variables,
/// continued LINEARLY beyond the interval it was trained over (charges extrapolate linearly — MC-2). Those
/// intervals are the constituent's <c>Domain.Box</c>, keyed by piece: <c>QGS</c>, <c>QGD</c>, <c>QDS</c>,
/// <c>Qdrift.vgd</c>, <c>Qdrift.Id</c>; the polygon is the data's hull, as for any charge.</para>
/// </summary>
public sealed class DriftCharges
{
    public const string Qgs = "QGS", Qgd = "QGD", Qds = "QDS", Qdrift = "Qdrift", Lambda = "Lambda";
    public const string DriftVgd = "Qdrift.vgd", DriftId = "Qdrift.Id";

    /// <summary>The payload keys of each charge, in the order a writer lists them.</summary>
    public static readonly IReadOnlyList<string> QgKeys = [Qgs, Qgd, Qdrift];
    public static readonly IReadOnlyList<string> QdKeys = [Qgd, Qdrift, Qds];

    /// <summary>The <c>Domain.Box</c> keys a drift charge carries.</summary>
    public static readonly IReadOnlyList<string> BoxKeys = [Qgs, Qgd, Qds, DriftVgd, DriftId];

    private readonly BoxContinuation _qgs, _qgd, _qds, _qdr;
    private readonly double _lambda;

    public double PartitionFactor => _lambda;

    private DriftCharges(BoxContinuation qgs, BoxContinuation qgd, BoxContinuation qds, BoxContinuation qdr, double lambda)
    {
        _qgs = qgs; _qgd = qgd; _qds = qds; _qdr = qdr; _lambda = lambda;
    }

    /// <summary>The pieces of a model whose <c>Qg</c> and <c>Qd</c> are both <c>Drift</c>; a refusal names its field.</summary>
    internal static DriftCharges Build(ComposedModel model)
    {
        var qg = model.Core.Qg;
        var qd = model.Core.Qd;
        var box = qd.Domain!.Box.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        BoxContinuation Piece(Constituent c, string name, string key, params string[] interval)
        {
            string field = $"Core.Constituents.{name}.{key}";
            var f = RuntimePayload.BuildAnn(model, c.Expression(key)!, interval.Length, c.Activation ?? AnnActivation.Tanh, field);
            return new BoxContinuation(f, [.. interval.Select(i => box[i].Lo)], [.. interval.Select(i => box[i].Hi)]);
        }
        double lambda = qd.Parameters.FirstOrDefault(p => p.Key == Lambda).Value;
        return new DriftCharges(Piece(qg, "Qg", Qgs, Qgs), Piece(qg, "Qg", Qgd, Qgd), Piece(qd, "Qd", Qds, Qds),
                                Piece(qg, "Qg", Qdrift, DriftVgd, DriftId), lambda);
    }

    /// <summary>
    /// Both charges and their gradients over the core's seed lanes (<c>vgs</c>, <c>vds</c>, then <c>T</c> when
    /// <paramref name="width"/> is 3), from the channel current <paramref name="id"/> and its gradient over the same
    /// lanes.
    /// </summary>
    public void Evaluate(double vgs, double vds, double id, ReadOnlySpan<double> gId, int width,
                         out double qg, Span<double> gQg, out double qd, Span<double> gQd)
    {
        Span<double> x = stackalloc double[2];
        Span<double> g = stackalloc double[2];

        x[0] = vgs;
        double qgs = _qgs.Evaluate(x[..1], g[..1]), dQgs = g[0];
        x[0] = vgs - vds;
        double qgd = _qgd.Evaluate(x[..1], g[..1]), dQgd = g[0];
        x[0] = vds;
        double qds = _qds.Evaluate(x[..1], g[..1]), dQds = g[0];
        x[0] = vgs - vds;
        x[1] = id;
        double qdr = _qdr.Evaluate(x, g);
        double dVgd = g[0], tau = g[1];

        double k = 1 - _lambda;
        qg = qgs + qgd + qdr;
        qd = -qgd - k * qdr + qds;

        // Qdrift through vgd (lanes vgs, vds) and through Id (every lane).
        double d0 = dVgd + tau * gId[0], d1 = -dVgd + tau * gId[1];
        gQg[0] = dQgs + dQgd + d0;
        gQg[1] = -dQgd + d1;
        gQd[0] = -dQgd - k * d0;
        gQd[1] = dQgd - k * d1 + dQds;
        for (int lane = 2; lane < width; lane++)
        {
            gQg[lane] = tau * gId[lane];
            gQd[lane] = -k * tau * gId[lane];
        }
    }

    /// <summary>One piece by its payload key, for the composer's report: its value and gradient.</summary>
    public double Piece(string key, ReadOnlySpan<double> x, Span<double> grad) => key switch
    {
        Qgs => _qgs.Evaluate(x, grad),
        Qgd => _qgd.Evaluate(x, grad),
        Qds => _qds.Evaluate(x, grad),
        Qdrift => _qdr.Evaluate(x, grad),
        _ => throw new ArgumentException($"'{key}' is not a piece of a drift charge.", nameof(key)),
    };
}

/// <summary>
/// A function continued to first order beyond a box (MC-2's linear continuation, for inputs that are not
/// <c>(vgs, vds)</c>): <c>F(x) = f(p) + ∇f(p)·(x − p)</c> with <c>p</c> the nearest point of the box. Value and gradient
/// are continuous across the box's faces; along an axis that is not clamped the gradient picks up
/// <c>(H(p)·(x − p))_j</c>, which is why the evaluators return the Hessian along a direction.
/// </summary>
internal sealed class BoxContinuation(RuntimeEvaluator f, double[] lo, double[] hi)
{
    public double Evaluate(ReadOnlySpan<double> x, Span<double> grad)
    {
        int d = lo.Length;
        Span<double> p = stackalloc double[RuntimeEvaluator.MaxInputs];
        Span<double> w = stackalloc double[RuntimeEvaluator.MaxInputs];
        Span<double> hw = stackalloc double[RuntimeEvaluator.MaxInputs];
        bool outside = false;
        for (int k = 0; k < d; k++)
        {
            p[k] = Math.Clamp(x[k], lo[k], hi[k]);
            w[k] = x[k] - p[k];
            outside |= w[k] != 0;
        }
        if (!outside) return f.Evaluate(x[..d], grad[..d]);

        double v = f.Evaluate(p[..d], grad[..d], w[..d], hw[..d]);
        for (int k = 0; k < d; k++) v += grad[k] * w[k];
        for (int k = 0; k < d; k++) if (w[k] == 0) grad[k] += hw[k];
        return v;
    }
}
