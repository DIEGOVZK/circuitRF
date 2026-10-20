using RfCore.Data;

namespace CircuitRF.Core.Devices.Composed;

/// <summary>
/// The <c>arrays/*.npy</c> a table or an ANN payload names, built from plain numbers — the one place
/// that knows their names and shapes, used by whatever writes a model (MC-7/MC-8/MC-11, and the tests).
///
/// <para><b>A table <c>name</c></b> is two arrays: <c>name.values</c>, the gridded data, one axis per
/// input carrying its sites; and <c>name.coefficients</c>, the spline's coefficients, one axis per
/// input at its Greville abscissae — so a plot of either array shows something meaningful.</para>
///
/// <para>A table written outside circuitRF may instead give each axis's sites as a list <c>name.&lt;input&gt;</c>, which a
/// plain <c>.npy</c> needs, having no axis values of its own (<see cref="PlainNpy"/>).</para>
///
/// <para><b>An ANN <c>name</c></b> is <c>name.inputOffset</c> and <c>name.inputScale</c> (one value
/// per input, <c>x̂ = (x − offset)·scale</c>), <c>name.W1 … name.WL</c> and <c>name.b1 … name.bL</c>
/// (layer <c>k</c> as a <c>outputs × inputs</c> matrix and its bias; the last layer is the output),
/// and <c>name.outputOffset</c>, <c>name.outputScale</c> (<c>y = offset + scale·ŷ</c>).</para>
/// </summary>
public static class ComposedArrays
{
    public static IEnumerable<KeyValuePair<string, DataCube>> Table(
        string name, IReadOnlyList<string> inputs, IReadOnlyList<double[]> sites,
        double[] values, double[] coefficients)
    {
        var valueAxes = new Axis[inputs.Count];
        var coefAxes  = new Axis[inputs.Count];
        for (int k = 0; k < inputs.Count; k++)
        {
            valueAxes[k] = new Axis(inputs[k], sites[k]);
            coefAxes[k]  = new Axis(inputs[k], Greville(CubicBSpline.Knots(sites[k])));
        }
        yield return new($"{name}.values", new DataCube(valueAxes, values));
        yield return new($"{name}.coefficients", new DataCube(coefAxes, coefficients));
    }

    public static IEnumerable<KeyValuePair<string, DataCube>> Ann(
        string name, double[] inputOffset, double[] inputScale,
        IReadOnlyList<double[,]> weights, IReadOnlyList<double[]> biases,
        double outputOffset, double outputScale)
    {
        yield return new($"{name}.inputOffset", Vector(inputOffset));
        yield return new($"{name}.inputScale",  Vector(inputScale));
        for (int k = 0; k < weights.Count; k++)
        {
            var w = weights[k];
            int rows = w.GetLength(0), cols = w.GetLength(1);
            var flat = new double[rows * cols];
            for (int r = 0; r < rows; r++) for (int c = 0; c < cols; c++) flat[r * cols + c] = w[r, c];
            yield return new($"{name}.W{k + 1}", new DataCube(
                [new Axis("output", Index(rows)), new Axis("input", Index(cols))], flat));
            yield return new($"{name}.b{k + 1}", Vector(biases[k]));
        }
        yield return new($"{name}.outputOffset", Vector([outputOffset]));
        yield return new($"{name}.outputScale",  Vector([outputScale]));
    }

    private static DataCube Vector(double[] v) => new([new Axis("index", Index(v.Length))], v);

    private static double[] Index(int n) => [.. Enumerable.Range(0, n).Select(i => (double)i)];

    private static double[] Greville(double[] knots)
    {
        var g = new double[knots.Length - 4];
        for (int i = 0; i < g.Length; i++) g[i] = (knots[i + 1] + knots[i + 2] + knots[i + 3]) / 3.0;
        return g;
    }
}

/// <summary>
/// A table or ANN payload's arrays turned into its evaluator — and the validator's check of them, so
/// a payload <c>check</c> passes is one the core can build. Every refusal names its field.
/// </summary>
internal static class RuntimePayload
{
    public static RuntimeEvaluator Build(ComposedModel model, Constituent c, string key, string field)
    {
        string name = c.Expression(key)!;
        RuntimeEvaluator f = c.Method == Constituent.TableMethod
            ? BuildTable(model, c, name, field)
            : BuildAnn(model, c, name, field);
        return c.Transform is { } t ? new AsinhOutput(f, t.Scale) : f;
    }

    private static TableEvaluator BuildTable(ComposedModel model, Constituent c, string name, string field)
    {
        var values = Array(model, $"{name}.values", field);
        var coefs  = Array(model, $"{name}.coefficients", field);
        int d = c.Inputs.Count;
        if (values.Rank != d)
            throw new CmodelFormatException(field, $"'{name}.values' has {values.Rank} axes; the inputs are {d}.");

        // An axis's sites are the array <name>.<input> when there is one — a plain .npy carries no axis values — and
        // otherwise the values axis, which then must be named after its input.
        var sites = new double[d][];
        for (int k = 0; k < d; k++)
        {
            var axis = values.Axes[k];
            string input = c.Inputs[k];
            double[] s;
            if (model.Arrays.TryGetValue($"{name}.{input}", out var siteCube))
            {
                s = Finite(siteCube, $"{name}.{input}", field);
                if (!IsList(siteCube) || s.Length != axis.Length)
                    throw new CmodelFormatException(field,
                        $"'{name}.{input}' is not a list of the {axis.Length} sites of axis {k + 1} of '{name}.values'.");
            }
            else if (axis.Name != input)
                throw new CmodelFormatException(field,
                    $"axis {k + 1} of '{name}.values' is '{axis.Name}'; the inputs are {string.Join(", ", c.Inputs)}, in that " +
                    $"order. Name the axis '{input}', or give its sites as the array '{name}.{input}'.");
            else s = axis.Values;
            if (s.Length < 2)
                throw new CmodelFormatException(field, $"the '{input}' axis of '{name}.values' has fewer than two sites.");
            for (int i = 0; i < s.Length; i++)
                if (!double.IsFinite(s[i]) || (i > 0 && s[i] <= s[i - 1]))
                    throw new CmodelFormatException(field, $"the '{input}' sites of '{name}' are not strictly increasing.");
            sites[k] = s;
            int m = CubicBSpline.BasisCount(s.Length);
            if (coefs.Rank != d || coefs.Axes[k].Length != m)
                throw new CmodelFormatException(field,
                    $"'{name}.coefficients' does not match the grid: the '{input}' axis needs {m} coefficients.");
        }
        Finite(values, $"{name}.values", field);
        var coefficients = Finite(coefs, $"{name}.coefficients", field);

        if (c.Domain is { } domain)
        {
            int iG = IndexOf(c.Inputs, ComposedCoreProgram.Vgs), iD = IndexOf(c.Inputs, ComposedCoreProgram.Vds);
            foreach (var (vgs, vds) in domain.Polygon)
                if (!Within(vgs, sites[iG]) || !Within(vds, sites[iD]))
                    throw new CmodelFormatException($"{field.Remove(field.LastIndexOf('.'))}.Domain.Polygon",
                        $"the vertex ({vgs:R}, {vds:R}) lies outside the table's grid.");
            foreach (var (input, (lo, hi)) in domain.Box)
            {
                int k = IndexOf(c.Inputs, input);
                if (!Within(lo, sites[k]) || !Within(hi, sites[k]))
                    throw new CmodelFormatException($"{field.Remove(field.LastIndexOf('.'))}.Domain.Box.{input}",
                        "the interval reaches outside the table's grid.");
            }
        }

        return new TableEvaluator(sites, coefficients);
    }

    private static AnnEvaluator BuildAnn(ComposedModel model, Constituent c, string name, string field)
        => BuildAnn(model, name, c.Inputs.Count, c.Activation ?? AnnActivation.Tanh, field);

    /// <summary>The network named <paramref name="name"/> over <paramref name="d"/> inputs — a drift charge's pieces
    /// (MC-8) read their own variables, not the constituent's.</summary>
    public static AnnEvaluator BuildAnn(ComposedModel model, string name, int d, AnnActivation activation, string field)
    {
        double[] Vec(string suffix, int length)
        {
            var cube = Array(model, $"{name}.{suffix}", field);
            var v = Finite(cube, $"{name}.{suffix}", field);
            if (!IsList(cube) || v.Length != length)
                throw new CmodelFormatException(field, $"'{name}.{suffix}' is not a list of {length}.");
            return v;
        }

        var inOffset = Vec("inputOffset", d);
        var inScale  = Vec("inputScale", d);
        if (inScale.Any(s => s == 0.0))
            throw new CmodelFormatException(field, $"'{name}.inputScale' has a zero scale.");

        var weights = new List<double[]>();
        var biases  = new List<double[]>();
        int width = d;
        for (int k = 1; model.Arrays.ContainsKey($"{name}.W{k}"); k++)
        {
            var wc = model.Arrays[$"{name}.W{k}"];
            var w  = Finite(wc, $"{name}.W{k}", field);
            if (wc.Rank != 2 || wc.Axes[1].Length != width)
                throw new CmodelFormatException(field, $"'{name}.W{k}' is not a matrix with {width} columns.");
            int rows = wc.Axes[0].Length;
            weights.Add(w);
            biases.Add(Vec($"b{k}", rows));
            width = rows;
        }

        int hidden = weights.Count - 1;
        if (hidden is < 1 or > AnnEvaluator.MaxHiddenLayers)
            throw new CmodelFormatException(field,
                $"the network has {Math.Max(hidden, 0)} hidden layers; it may have 1 to {AnnEvaluator.MaxHiddenLayers}.");
        for (int k = 0; k < hidden; k++)
            if (biases[k].Length > AnnEvaluator.MaxNeurons)
                throw new CmodelFormatException(field,
                    $"hidden layer {k + 1} has {biases[k].Length} neurons; a layer has at most {AnnEvaluator.MaxNeurons}.");
        if (width != 1)
            throw new CmodelFormatException(field, $"the output layer '{name}.W{weights.Count}' has {width} neurons, not one.");

        double outOffset = Vec("outputOffset", 1)[0], outScale = Vec("outputScale", 1)[0];
        return new AnnEvaluator(inOffset, inScale, weights, biases, outOffset, outScale, activation);
    }

    private static DataCube Array(ComposedModel model, string array, string field)
        => model.Arrays.TryGetValue(array, out var cube) ? cube
            : throw new CmodelFormatException(field, $"the array '{array}' is missing from arrays/.");

    private static double[] Finite(DataCube cube, string array, string field)
    {
        if (cube.DataKind != DataKind.Real)
            throw new CmodelFormatException(field, $"'{array}' is not real.");
        var v = cube.RealValues;
        if (v.Any(x => !double.IsFinite(x)))
            throw new CmodelFormatException(field, $"'{array}' holds a value that is not finite.");
        return v;
    }

    /// <summary>A list: one axis, or several of which at most one is longer than one — a MATLAB vector is 1 × n.</summary>
    private static bool IsList(DataCube cube) => cube.Axes.Count(a => a.Length > 1) <= 1;

    private static bool Within(double x, double[] sites)
    {
        double tol = 1e-12 * (sites[^1] - sites[0]);
        return x >= sites[0] - tol && x <= sites[^1] + tol;
    }

    private static int IndexOf(IReadOnlyList<string> inputs, string name)
    {
        for (int k = 0; k < inputs.Count; k++) if (inputs[k] == name) return k;
        return -1;
    }
}
