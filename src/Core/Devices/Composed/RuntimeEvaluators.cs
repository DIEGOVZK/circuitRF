namespace CircuitRF.Core.Devices.Composed;

/// <summary>
/// A table or ANN constituent's function of its inputs (R-mc2-1, R-mc2-2): value, exact gradient, and —
/// for guided extrapolation, which needs it at the boundary point — the Hessian times a direction.
///
/// <para><b>Immutable and thread-safe.</b> Every scratch buffer lives on the caller's stack, so one
/// evaluator serves every instance, every HB sample and every thread, and allocates nothing.</para>
/// </summary>
public abstract class RuntimeEvaluator
{
    /// <summary>The most inputs a constituent has: <c>vgs</c>, <c>vds</c>, <c>T</c>, <c>phi1</c>, <c>phi2</c>.</summary>
    public const int MaxInputs = 5;

    public abstract int InputCount { get; }

    /// <summary>
    /// The value at <paramref name="x"/>, its gradient into <paramref name="grad"/>, and, when
    /// <paramref name="w"/> is not empty, the Hessian applied to <paramref name="w"/> into
    /// <paramref name="hw"/>. All in the inputs' own units.
    /// </summary>
    public abstract double Evaluate(ReadOnlySpan<double> x, Span<double> grad,
                                    ReadOnlySpan<double> w, Span<double> hw);

    public double Evaluate(ReadOnlySpan<double> x, Span<double> grad) => Evaluate(x, grad, [], []);
}

/// <summary>
/// A payload read through <see cref="OutputTransform.Asinh"/>: <c>f = s·sinh(u)</c>, so <c>∇f = s·cosh(u)·∇u</c> and
/// <c>H_f w = s·cosh(u)·H_u w + s·sinh(u)·(∇u·w)·∇u</c> — exact, like the payload's own.
/// </summary>
public sealed class AsinhOutput(RuntimeEvaluator inner, double scale) : RuntimeEvaluator
{
    public override int InputCount => inner.InputCount;

    public override double Evaluate(ReadOnlySpan<double> x, Span<double> grad, ReadOnlySpan<double> w, Span<double> hw)
    {
        int d = inner.InputCount;
        double u = inner.Evaluate(x, grad, w, hw);
        double sh = scale * Math.Sinh(u), ch = scale * Math.Cosh(u);
        if (!w.IsEmpty)
        {
            double gw = 0.0;
            for (int k = 0; k < d; k++) gw += grad[k] * w[k];
            for (int k = 0; k < d; k++) hw[k] = ch * hw[k] + sh * gw * grad[k];
        }
        for (int k = 0; k < d; k++) grad[k] *= ch;
        return sh;
    }
}

/// <summary>
/// A tensor-product cubic spline over a rectilinear grid (R-mc2-1): one <see cref="CubicBSpline"/> per
/// axis, the axes allowed to be non-uniform, the coefficients fitted once when the model was written.
/// C² on every axis, with exact first (and second) derivatives from the basis itself.
/// </summary>
public sealed class TableEvaluator : RuntimeEvaluator
{
    private readonly double[][] _knots;
    private readonly double[] _lo, _hi;
    private readonly int[] _strides;
    private readonly double[] _coefficients;

    /// <summary>The grid sites of each axis, in input order.</summary>
    public IReadOnlyList<double[]> Sites { get; }

    public override int InputCount => _knots.Length;

    /// <param name="sites">Strictly increasing sites per axis, at least two each.</param>
    /// <param name="coefficients">Row-major, last axis fastest, <see cref="CubicBSpline.BasisCount"/> per axis.</param>
    public TableEvaluator(IReadOnlyList<double[]> sites, double[] coefficients)
    {
        int d = sites.Count;
        if (d is < 1 or > MaxInputs) throw new ArgumentException($"a table has 1 to {MaxInputs} axes.");
        Sites   = sites;
        _knots  = new double[d][];
        _lo     = new double[d];
        _hi     = new double[d];
        _strides = new int[d];
        int size = 1;
        for (int k = d - 1; k >= 0; k--)
        {
            _knots[k] = CubicBSpline.Knots(sites[k]);
            _lo[k] = sites[k][0];
            _hi[k] = sites[k][^1];
            _strides[k] = size;
            size *= CubicBSpline.BasisCount(sites[k].Length);
        }
        if (coefficients.Length != size)
            throw new ArgumentException($"the coefficients hold {coefficients.Length} values; the grid needs {size}.");
        _coefficients = coefficients;
    }

    public override double Evaluate(ReadOnlySpan<double> x, Span<double> grad,
                                    ReadOnlySpan<double> w, Span<double> hw)
    {
        int d = _knots.Length;
        bool wantH = !w.IsEmpty;

        // Per axis: the first coefficient index of its four live basis functions, and their value,
        // first and second derivative (12 numbers).
        Span<int> first = stackalloc int[MaxInputs];
        Span<double> basis = stackalloc double[MaxInputs * 12];
        for (int k = 0; k < d; k++)
        {
            double xk = Math.Clamp(x[k], _lo[k], _hi[k]);
            int span = CubicBSpline.Span(_knots[k], xk);
            first[k] = span - CubicBSpline.Degree;
            CubicBSpline.BasisDerivatives(_knots[k], span, xk, basis.Slice(k * 12, 12));
        }

        grad[..d].Clear();
        if (wantH) hw[..d].Clear();

        double value = 0.0;
        Span<int> j = stackalloc int[MaxInputs];
        j[..d].Clear();
        int terms = 1 << (2 * d);
        for (int term = 0; term < terms; term++)
        {
            int idx = 0;
            for (int k = 0; k < d; k++) idx += (first[k] + j[k]) * _strides[k];
            double c = _coefficients[idx];

            if (c != 0.0)
            {
                value += c * Product(basis, j, d, -1, -1);
                for (int k = 0; k < d; k++)
                {
                    double b1k = basis[k * 12 + 4 + j[k]];
                    grad[k] += c * b1k * Product(basis, j, d, k, -1);
                    if (!wantH) continue;
                    double h = basis[k * 12 + 8 + j[k]] * Product(basis, j, d, k, -1) * w[k];
                    for (int l = 0; l < d; l++)
                        if (l != k) h += b1k * basis[l * 12 + 4 + j[l]] * Product(basis, j, d, k, l) * w[l];
                    hw[k] += c * h;
                }
            }

            for (int k = d - 1; k >= 0; k--)
            {
                if (++j[k] < 4) break;
                j[k] = 0;
            }
        }
        return value;
    }

    /// <summary>The product of the basis VALUES over every axis but <paramref name="skipA"/> and <paramref name="skipB"/>.</summary>
    private static double Product(ReadOnlySpan<double> basis, ReadOnlySpan<int> j, int d, int skipA, int skipB)
    {
        double p = 1.0;
        for (int m = 0; m < d; m++)
            if (m != skipA && m != skipB) p *= basis[m * 12 + j[m]];
        return p;
    }
}

/// <summary>The activation of an ANN's hidden layers ([P1] Fig. 6.10).</summary>
public enum AnnActivation { Tanh, Sigmoid }

/// <summary>
/// A multilayer perceptron (R-mc2-2): affine input normalisation, one to four hidden layers of one to
/// 64 neurons, a linear output and its de-normalisation. The gradient with respect to the inputs is
/// carried forward through the layers alongside the value — the derivative network of [P1] §6.2.7 —
/// and, for extrapolation, the Hessian along one direction with it.
/// </summary>
public sealed class AnnEvaluator : RuntimeEvaluator
{
    public const int MaxHiddenLayers = 4;
    public const int MaxNeurons      = 64;

    private readonly double[] _inOffset, _inScale;
    private readonly double[][] _weights, _biases;
    private readonly int[] _widths;            // _widths[0] = inputs, then each layer's outputs
    private readonly double _outOffset, _outScale;

    public AnnActivation Activation { get; }

    public override int InputCount => _inOffset.Length;

    /// <summary>The hidden-layer widths.</summary>
    public IEnumerable<int> Hidden => _widths.Skip(1).SkipLast(1);

    /// <param name="weights">Per layer, row-major <c>outputs × inputs</c>; the last layer has one output.</param>
    public AnnEvaluator(double[] inputOffset, double[] inputScale,
                        IReadOnlyList<double[]> weights, IReadOnlyList<double[]> biases,
                        double outputOffset, double outputScale, AnnActivation activation)
    {
        int d = inputOffset.Length;
        if (d is < 1 or > MaxInputs || inputScale.Length != d)
            throw new ArgumentException($"an ANN has 1 to {MaxInputs} inputs, with one offset and one scale each.");
        if (weights.Count != biases.Count || weights.Count is < 2 or > MaxHiddenLayers + 1)
            throw new ArgumentException($"an ANN has 1 to {MaxHiddenLayers} hidden layers and an output layer.");

        _widths = new int[weights.Count + 1];
        _widths[0] = d;
        for (int k = 0; k < weights.Count; k++)
        {
            int outputs = biases[k].Length;
            if (outputs < 1 || weights[k].Length != outputs * _widths[k])
                throw new ArgumentException($"layer {k + 1}'s weights are not {outputs} × {_widths[k]}.");
            _widths[k + 1] = outputs;
        }
        if (_widths[^1] != 1) throw new ArgumentException("an ANN's output layer has one neuron.");

        _inOffset = inputOffset; _inScale = inputScale;
        _weights = [.. weights]; _biases = [.. biases];
        _outOffset = outputOffset; _outScale = outputScale;
        Activation = activation;
    }

    public override double Evaluate(ReadOnlySpan<double> x, Span<double> grad,
                                    ReadOnlySpan<double> w, Span<double> hw)
    {
        int d = _inOffset.Length;
        bool wantH = !w.IsEmpty;
        const int Width = MaxNeurons;
        const int Block = Width * MaxInputs;

        // a: activations; J = ∂a/∂x (rows × d); del = J·w; K = (∂J/∂x)·w. Two copies, current and next.
        Span<double> a0 = stackalloc double[Width], a1 = stackalloc double[Width];
        Span<double> j0 = stackalloc double[Block], j1 = stackalloc double[Block];
        Span<double> e0 = stackalloc double[Width], e1 = stackalloc double[Width];
        Span<double> k0 = stackalloc double[Block], k1 = stackalloc double[Block];

        for (int i = 0; i < d; i++)
        {
            a0[i] = (x[i] - _inOffset[i]) * _inScale[i];
            for (int c = 0; c < d; c++) j0[i * d + c] = i == c ? _inScale[i] : 0.0;
            if (!wantH) continue;
            e0[i] = _inScale[i] * w[i];
            for (int c = 0; c < d; c++) k0[i * d + c] = 0.0;
        }

        int layers = _weights.Length;
        for (int layer = 0; layer < layers; layer++)
        {
            int nIn = _widths[layer], nOut = _widths[layer + 1];
            double[] W = _weights[layer], b = _biases[layer];
            bool hidden = layer < layers - 1;

            for (int r = 0; r < nOut; r++)
            {
                int row = r * nIn;
                double z = b[r], dz = 0.0;
                for (int c = 0; c < nIn; c++) z += W[row + c] * a0[c];
                for (int g = 0; g < d; g++)
                {
                    double s = 0.0, t = 0.0;
                    for (int c = 0; c < nIn; c++)
                    {
                        s += W[row + c] * j0[c * d + g];
                        if (wantH) t += W[row + c] * k0[c * d + g];
                    }
                    j1[r * d + g] = s;
                    if (wantH) k1[r * d + g] = t;
                }
                if (wantH)
                    for (int c = 0; c < nIn; c++) dz += W[row + c] * e0[c];

                if (!hidden) { a1[r] = z; e1[r] = dz; continue; }

                // σ, σ′, σ″ at z.
                double f, f1, f2;
                if (Activation == AnnActivation.Tanh)
                {
                    f = Math.Tanh(z); f1 = 1.0 - f * f; f2 = -2.0 * f * f1;
                }
                else
                {
                    f = 1.0 / (1.0 + Math.Exp(-z)); f1 = f * (1.0 - f); f2 = f1 * (1.0 - 2.0 * f);
                }
                a1[r] = f;
                for (int g = 0; g < d; g++)
                {
                    double jz = j1[r * d + g];
                    j1[r * d + g] = f1 * jz;
                    if (wantH) k1[r * d + g] = f2 * dz * jz + f1 * k1[r * d + g];
                }
                e1[r] = f1 * dz;
            }

            var ta = a0; a0 = a1; a1 = ta;
            var tj = j0; j0 = j1; j1 = tj;
            var te = e0; e0 = e1; e1 = te;
            var tk = k0; k0 = k1; k1 = tk;
        }

        for (int g = 0; g < d; g++)
        {
            grad[g] = _outScale * j0[g];
            if (wantH) hw[g] = _outScale * k0[g];
        }
        return _outOffset + _outScale * a0[0];
    }
}
