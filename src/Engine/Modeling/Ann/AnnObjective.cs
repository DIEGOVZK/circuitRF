using CircuitRF.Core.Devices.Composed;
using CircuitRF.Engine.Optimization;

namespace CircuitRF.Engine.Modeling.Ann;

/// <summary>
/// One training point in the network's normalised coordinates. Its loss is
/// <c>ValueCoef·(ŷ − ValueTarget)² + Σⱼ DirCoef[j]·(DirAlpha[j]·∂ŷ/∂x̂_{Dirs[j]} − DirTarget[j])²</c>:
/// a value point has no directions, a derivative point has a zero value coefficient, and the anchor is a
/// value point of its own.
/// </summary>
internal sealed record AnnTrainingPoint(
    double[] XHat, double ValueCoef, double ValueTarget,
    int[] Dirs, double[] DirCoef, double[] DirAlpha, double[] DirTarget)
{
    public static AnnTrainingPoint Value(double[] xHat, double coef, double target)
        => new(xHat, coef, target, [], [], [], []);
}

/// <summary>
/// The training loss of an MLP over a set of points, and its exact gradient with respect to every
/// weight and bias (R-mc6-2, R-mc6-3, R-mc6-3b).
///
/// <para><b>The adjoint network.</b> The input derivatives <c>gⱼ = ∂ŷ/∂x̂ⱼ</c> are carried forward
/// through the layers with the value — the derivative network of [P1] §6.2.7: <c>u⁰ⱼ = eⱼ</c>,
/// <c>vˡⱼ = Wˡ uˡ⁻¹ⱼ</c>, <c>uˡⱼ = σ′(zˡ) ⊙ vˡⱼ</c>. The loss is then back-propagated through BOTH the
/// original and that derivative network at once ([X03] §II-E): with <c>ā</c> the adjoint of a layer's
/// activations and <c>ūⱼ</c> the adjoint of its tangents,</para>
/// <code>
///   z̄ = ā ⊙ σ′(z) + Σⱼ ūⱼ ⊙ σ″(z) ⊙ vⱼ      v̄ⱼ = ūⱼ ⊙ σ′(z)
///   ∂L/∂W += z̄ aˡ⁻¹ᵀ + Σⱼ v̄ⱼ uˡ⁻¹ⱼᵀ           ∂L/∂b += z̄
///   āˡ⁻¹ = Wᵀ z̄                               ūˡ⁻¹ⱼ = Wᵀ v̄ⱼ
/// </code>
/// <para>The three concurrent paths of [X03] eq. 17 are the first and second terms of <c>z̄</c> and
/// the tangent path <c>v̄</c>. Never a finite difference.</para>
///
/// <para><b>Fixed summation order.</b> The points are taken in fixed blocks of <see cref="BlockSize"/>;
/// each block's loss and gradient are summed in point order, and the blocks are added in block order —
/// whether the blocks ran on one thread or several. So the parallel mode adds no reduction of its own
/// (R-mc6-6).</para>
/// </summary>
internal sealed class AnnObjective
{
    public const int BlockSize = 64;

    private readonly int[] _widths;
    private readonly int[] _wOffset, _bOffset;
    private readonly AnnActivation _activation;
    private readonly AnnTrainingPoint[] _points;
    private readonly double _l2;
    private readonly bool _parallel;
    private readonly int _blocks;
    private readonly Scratch[] _scratch;
    private readonly double[] _blockLoss;

    public int ParameterCount { get; }
    public int[] Widths => _widths;

    /// <param name="widths">Inputs, each hidden layer's width, then 1.</param>
    /// <param name="l2">The weight penalty <c>λ·Σ W²</c> over the weights (not the biases); 0 for none.</param>
    public AnnObjective(int[] widths, AnnActivation activation, AnnTrainingPoint[] points, double l2, bool parallel)
    {
        _widths = widths;
        _activation = activation;
        _points = points;
        _l2 = l2;
        _parallel = parallel;
        int layers = widths.Length - 1;
        _wOffset = new int[layers];
        _bOffset = new int[layers];
        int offset = 0;
        for (int l = 0; l < layers; l++)
        {
            _wOffset[l] = offset; offset += widths[l + 1] * widths[l];
            _bOffset[l] = offset; offset += widths[l + 1];
        }
        ParameterCount = offset;
        _blocks = (points.Length + BlockSize - 1) / BlockSize;
        int maxDirs = points.Length == 0 ? 0 : points.Max(pt => pt.Dirs.Length);
        _scratch = [.. Enumerable.Range(0, _blocks).Select(_ => new Scratch(widths, maxDirs, offset))];
        _blockLoss = new double[_blocks];
    }

    /// <summary>The loss at <paramref name="p"/>; its gradient into <paramref name="grad"/> when not null.</summary>
    public double Evaluate(double[] p, double[]? grad)
    {
        if (_parallel && _blocks > 1)
            Parallel.For(0, _blocks, b => _blockLoss[b] = Block(b, p, grad is not null));
        else
            for (int b = 0; b < _blocks; b++) _blockLoss[b] = Block(b, p, grad is not null);

        double loss = 0.0;
        if (grad is not null) Array.Clear(grad);
        for (int b = 0; b < _blocks; b++)
        {
            loss += _blockLoss[b];
            if (grad is null) continue;
            var gb = _scratch[b].Grad;
            for (int i = 0; i < grad.Length; i++) grad[i] += gb[i];
        }

        if (_l2 > 0)
            for (int l = 0; l < _wOffset.Length; l++)
                for (int i = _wOffset[l]; i < _bOffset[l]; i++)
                {
                    loss += _l2 * p[i] * p[i];
                    if (grad is not null) grad[i] += 2.0 * _l2 * p[i];
                }
        return loss;
    }

    /// <summary>The runtime network of MC-2 for the parameters <paramref name="p"/> and the given normalisation.</summary>
    public AnnEvaluator ToEvaluator(double[] p, double[] inOffset, double[] inScale, double outOffset, double outScale)
    {
        var (weights, biases) = Unpack(p);
        return new AnnEvaluator(inOffset, inScale, [.. weights.Select(Flatten)], biases, outOffset, outScale, _activation);
    }

    /// <summary>The parameter vector as per-layer <c>outputs × inputs</c> matrices and biases.</summary>
    public (double[][,] Weights, double[][] Biases) Unpack(double[] p)
    {
        int layers = _wOffset.Length;
        var weights = new double[layers][,];
        var biases = new double[layers][];
        for (int l = 0; l < layers; l++)
        {
            int rows = _widths[l + 1], cols = _widths[l];
            var w = new double[rows, cols];
            for (int r = 0; r < rows; r++) for (int c = 0; c < cols; c++) w[r, c] = p[_wOffset[l] + r * cols + c];
            weights[l] = w;
            biases[l] = p.AsSpan(_bOffset[l], rows).ToArray();
        }
        return (weights, biases);
    }

    /// <summary>Glorot-uniform weights (limit <c>√(6/(fan_in + fan_out))</c>) and zero biases, from <paramref name="rng"/>.</summary>
    public double[] Initial(SplitMix64 rng)
    {
        var p = new double[ParameterCount];
        for (int l = 0; l < _wOffset.Length; l++)
        {
            double limit = Math.Sqrt(6.0 / (_widths[l] + _widths[l + 1]));
            for (int i = _wOffset[l]; i < _bOffset[l]; i++) p[i] = (2.0 * rng.NextDouble() - 1.0) * limit;
        }
        return p;
    }

    private static double[] Flatten(double[,] w)
    {
        int rows = w.GetLength(0), cols = w.GetLength(1);
        var flat = new double[rows * cols];
        for (int r = 0; r < rows; r++) for (int c = 0; c < cols; c++) flat[r * cols + c] = w[r, c];
        return flat;
    }

    private double Block(int b, double[] p, bool wantGrad)
    {
        var s = _scratch[b];
        if (wantGrad) Array.Clear(s.Grad);
        double loss = 0.0;
        int end = Math.Min(_points.Length, (b + 1) * BlockSize);
        for (int k = b * BlockSize; k < end; k++) loss += Point(_points[k], p, s, wantGrad);
        return loss;
    }

    private double Point(AnnTrainingPoint pt, double[] p, Scratch s, bool wantGrad)
    {
        int layers = _wOffset.Length;
        int m = pt.Dirs.Length;
        bool tanh = _activation == AnnActivation.Tanh;

        // Forward: values and tangents.
        var a0 = s.A[0];
        for (int i = 0; i < _widths[0]; i++) a0[i] = pt.XHat[i];
        for (int j = 0; j < m; j++)
        {
            var u0 = s.U[0][j];
            Array.Clear(u0, 0, _widths[0]);
            u0[pt.Dirs[j]] = 1.0;
        }

        for (int l = 0; l < layers; l++)
        {
            int nIn = _widths[l], nOut = _widths[l + 1];
            int wo = _wOffset[l], bo = _bOffset[l];
            var aIn = s.A[l]; var aOut = s.A[l + 1]; var z = s.Z[l + 1];
            var d1 = s.D1[l + 1]; var d2 = s.D2[l + 1];
            bool hidden = l < layers - 1;
            for (int r = 0; r < nOut; r++)
            {
                int row = wo + r * nIn;
                double zr = p[bo + r];
                for (int c = 0; c < nIn; c++) zr += p[row + c] * aIn[c];
                z[r] = zr;
                double f1 = 1.0, f2 = 0.0;
                if (hidden)
                {
                    double f;
                    if (tanh) { f = DeterministicMath.Tanh(zr); f1 = 1.0 - f * f; f2 = -2.0 * f * f1; }
                    else      { f = DeterministicMath.Sigmoid(zr); f1 = f * (1.0 - f); f2 = f1 * (1.0 - 2.0 * f); }
                    aOut[r] = f;
                }
                else aOut[r] = zr;
                d1[r] = f1; d2[r] = f2;

                for (int j = 0; j < m; j++)
                {
                    var uIn = s.U[l][j];
                    double vr = 0.0;
                    for (int c = 0; c < nIn; c++) vr += p[row + c] * uIn[c];
                    s.V[l + 1][j][r] = vr;
                    s.U[l + 1][j][r] = f1 * vr;
                }
            }
        }

        // Loss and its derivative with respect to ŷ and to each gⱼ.
        double y = s.A[layers][0];
        double loss = 0.0;
        var abar = s.ABar; var ubar = s.UBar;
        double ry = y - pt.ValueTarget;
        loss += pt.ValueCoef * ry * ry;
        abar[0] = 2.0 * pt.ValueCoef * ry;
        for (int j = 0; j < m; j++)
        {
            double gj = s.U[layers][j][0];
            double rj = pt.DirAlpha[j] * gj - pt.DirTarget[j];
            loss += pt.DirCoef[j] * rj * rj;
            ubar[j][0] = 2.0 * pt.DirCoef[j] * rj * pt.DirAlpha[j];
        }
        if (!wantGrad) return loss;

        // Backward through the original and the derivative network together.
        var grad = s.Grad;
        for (int l = layers - 1; l >= 0; l--)
        {
            int nIn = _widths[l], nOut = _widths[l + 1];
            int wo = _wOffset[l], bo = _bOffset[l];
            var aIn = s.A[l];
            var d1 = s.D1[l + 1]; var d2 = s.D2[l + 1];
            var zbar = s.ZBar; var vbar = s.VBar;

            for (int r = 0; r < nOut; r++)
            {
                double zb = abar[r] * d1[r];
                for (int j = 0; j < m; j++)
                {
                    zb += ubar[j][r] * d2[r] * s.V[l + 1][j][r];
                    vbar[j][r] = ubar[j][r] * d1[r];
                }
                zbar[r] = zb;

                int row = wo + r * nIn;
                grad[bo + r] += zb;
                for (int c = 0; c < nIn; c++)
                {
                    double gw = zb * aIn[c];
                    for (int j = 0; j < m; j++) gw += vbar[j][r] * s.U[l][j][c];
                    grad[row + c] += gw;
                }
            }

            if (l == 0) break;
            var abarNext = s.ABarNext; var ubarNext = s.UBarNext;
            for (int c = 0; c < nIn; c++)
            {
                double ab = 0.0;
                for (int r = 0; r < nOut; r++) ab += p[wo + r * nIn + c] * zbar[r];
                abarNext[c] = ab;
                for (int j = 0; j < m; j++)
                {
                    double ub = 0.0;
                    for (int r = 0; r < nOut; r++) ub += p[wo + r * nIn + c] * vbar[j][r];
                    ubarNext[j][c] = ub;
                }
            }
            (abar, abarNext) = (abarNext, abar);
            (ubar, ubarNext) = (ubarNext, ubar);
            (s.ABar, s.ABarNext, s.UBar, s.UBarNext) = (abar, abarNext, ubar, ubarNext);
        }
        return loss;
    }

    /// <summary>One block's buffers: per layer the activations, pre-activations, σ′, σ″ and per direction
    /// the tangents; the adjoints of the current and next layer; and the block's gradient.</summary>
    private sealed class Scratch
    {
        public readonly double[][] A, Z, D1, D2;
        public readonly double[][][] V, U;
        public double[] ABar, ABarNext;
        public double[][] UBar, UBarNext;
        public readonly double[] ZBar;
        public readonly double[][] VBar;
        public readonly double[] Grad;

        public Scratch(int[] widths, int maxDirs, int parameters)
        {
            int wide = widths.Max();
            A  = [.. widths.Select(w => new double[w])];
            Z  = [.. widths.Select(w => new double[w])];
            D1 = [.. widths.Select(w => new double[w])];
            D2 = [.. widths.Select(w => new double[w])];
            V  = [.. widths.Select(w => Enumerable.Range(0, maxDirs).Select(_ => new double[w]).ToArray())];
            U  = [.. widths.Select(w => Enumerable.Range(0, maxDirs).Select(_ => new double[w]).ToArray())];
            ABar = new double[wide]; ABarNext = new double[wide];
            UBar     = [.. Enumerable.Range(0, maxDirs).Select(_ => new double[wide])];
            UBarNext = [.. Enumerable.Range(0, maxDirs).Select(_ => new double[wide])];
            ZBar = new double[wide];
            VBar = [.. Enumerable.Range(0, maxDirs).Select(_ => new double[wide])];
            Grad = new double[parameters];
        }
    }
}
