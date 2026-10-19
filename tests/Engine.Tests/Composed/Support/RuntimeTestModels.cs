using System.Globalization;
using System.Text;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Tests.Composed;
using CircuitRF.Engine.Modeling;
using RfCore.Data;

namespace CircuitRF.Engine.Tests.Composed;

/// <summary>
/// A hand-set MLP, as arrays for a <c>.cmodel</c> and as the expression it computes — so an ANN model
/// can be compared against an EQUATION model of exactly the same function (brief-modelcomposer-2 §4).
/// </summary>
public sealed record TestNet(
    double[] InOffset, double[] InScale, double[][,] W, double[][] B,
    double YOffset, double YScale, AnnActivation Activation = AnnActivation.Tanh)
{
    public IEnumerable<KeyValuePair<string, DataCube>> Arrays(string name)
        => ComposedArrays.Ann(name, InOffset, InScale, W, B, YOffset, YScale);

    public AnnEvaluator Evaluator()
        => new(InOffset, InScale,
               [.. W.Select(w => { int r = w.GetLength(0), c = w.GetLength(1); var f = new double[r * c];
                                   for (int i = 0; i < r; i++) for (int j = 0; j < c; j++) f[i * c + j] = w[i, j];
                                   return f; })],
               B, YOffset, YScale, Activation);

    /// <summary>The network written out as one expression over <paramref name="inputs"/>.</summary>
    public string Expression(IReadOnlyList<string> inputs)
    {
        var layer = inputs.Select((x, i) => $"(({x}-{N(InOffset[i])})*{N(InScale[i])})").ToArray();
        for (int k = 0; k < W.Length; k++)
        {
            var w = W[k];
            var next = new string[w.GetLength(0)];
            for (int r = 0; r < next.Length; r++)
            {
                var z = new StringBuilder(N(B[k][r]));
                for (int c = 0; c < w.GetLength(1); c++) z.Append($"+{N(w[r, c])}*{layer[c]}");
                bool hidden = k < W.Length - 1;
                next[r] = !hidden ? $"({z})"
                        : Activation == AnnActivation.Tanh ? $"tanh({z})" : $"(1/(1+exp(-({z}))))";
            }
            layer = next;
        }
        return $"{N(YOffset)}+{N(YScale)}*{layer[0]}";
    }

    private static string N(double v) => $"({v.ToString("R", CultureInfo.InvariantCulture)})";
}

/// <summary>The models the brief-modelcomposer-2 gates share.</summary>
public static class RuntimeTestModels
{
    public static readonly string[] Inputs = ["vgs", "vds"];

    // Every weight that feeds the output is positive and every activation increasing, so each
    // current rises with its own branch voltage: one DC operating point, no negative resistance.
    public static readonly TestNet IdNet = new(
        [-0.5, 5.0], [0.8, 0.25],
        [new double[,] { { 1.6, 0.0 }, { 0.0, 1.2 }, { 1.6, 1.2 } }, new double[,] { { 0.6, 0.4, 0.5 }, { 0.3, 0.8, 0.2 } },
         new double[,] { { 1.0, 0.7 } }],
        [[1.6, -1.0, 0.2], [0.1, -0.2], [0.0]],
        0.03, 0.02);

    public static readonly TestNet IgGsNet = new(
        [0.0, 0.0], [1.0, 1.0], [new double[,] { { 5.0, 0.0 } }, new double[,] { { 1.0 } }], [[-4.0], [0.0]],
        0.0, 1e-4, AnnActivation.Sigmoid);

    public static readonly TestNet IgGdNet = new(
        [0.0, 0.0], [1.0, 1.0], [new double[,] { { 5.0, -5.0 } }, new double[,] { { 1.0 } }], [[-4.0], [0.0]],
        0.0, 1e-4, AnnActivation.Sigmoid);

    public static readonly TestNet QgNet = new(
        [-0.5, 5.0], [0.8, 0.25],
        [new double[,] { { 1.0, 0.0 }, { 0.75, -3.0 } }, new double[,] { { 3.0, 0.5 } }],
        [[0.1, 0.4], [0.0]],
        0.0, 1e-13);

    public static readonly TestNet QdNet = new(
        [-0.5, 5.0], [0.8, 0.25],
        [new double[,] { { 0.75, -3.0 }, { 0.0, 1.2 }, { 1.0, 0.8 } }, new double[,] { { -0.5, 1.0, 0.4 } }],
        [[0.4, 0.5, 0.3], [0.0]],
        0.0, 1e-13);

    /// <summary>A domain wide enough that every DC, S-parameter and HB point the gates visit is inside.</summary>
    public static TrainingDomain Wide => TrainingDomain.Rectangle(-6, 4, -5, 25);

    /// <summary>Every constituent an ANN, in the GaN network.</summary>
    public static ComposedModel AnnModel(TrainingDomain domain, DrainSourceSymmetry symmetry = DrainSourceSymmetry.None)
    {
        var arrays = new SortedDictionary<string, DataCube>(StringComparer.Ordinal);
        foreach (var (net, name) in new[] { (IdNet, "Id"), (IgGsNet, "IgGs"), (IgGdNet, "IgGd"), (QgNet, "Qg"), (QdNet, "Qd") })
            foreach (var kv in net.Arrays(name)) arrays[kv.Key] = kv.Value;

        return ComposedTestModels.Model() with
        {
            Core = new CoreConstituents(
                Constituent.Ann("Id", Inputs, domain),
                Constituent.Gate(Constituent.AnnMethod, "IgGs", "IgGd", Inputs, domain) with { Activation = AnnActivation.Sigmoid },
                Constituent.Ann("Qg", Inputs, domain),
                Constituent.Ann("Qd", Inputs, domain)) { Symmetry = symmetry },
            Extrapolation = new ModelExtrapolation(new Dictionary<string, double> { ["Id"] = 1.0, ["IgGs"] = 0.1, ["IgGd"] = 0.1 }),
            Arrays = arrays,
        };
    }

    /// <summary>The same functions as equations: the twin <see cref="AnnModel"/> is compared against.</summary>
    public static ComposedModel EquationTwin() => ComposedTestModels.Model() with
    {
        Core = new CoreConstituents(
            Constituent.Equation(IdNet.Expression(Inputs), Inputs),
            Constituent.GateEquations(IgGsNet.Expression(Inputs), IgGdNet.Expression(Inputs), Inputs),
            Constituent.Equation(QgNet.Expression(Inputs), Inputs),
            Constituent.Equation(QdNet.Expression(Inputs), Inputs)),
    };

    // ── A table-and-ANN model over a non-rectangular domain ─────────────────

    /// <summary>A convex pentagon (one oblique edge), inside the tables' grid.</summary>
    public static readonly TrainingDomain Pentagon =
        new([(-3, 0), (1, 0), (1, 8), (0, 10), (-3, 10)], []);

    /// <summary>A FET-like drain current, smooth and finite everywhere.</summary>
    public static double IdAnalytic(double vgs, double vds)
        => 0.04 * Math.Log(1 + Math.Exp(2.5 * (vgs + 1.5))) * Math.Tanh(1.2 * vds) * (1 + 0.02 * vds);

    public static double QgAnalytic(double vgs, double vds)
        => 2e-13 * Math.Log(1 + Math.Exp(2 * (vgs + 1))) + 0.4e-13 * (vgs - vds) + 0.2e-13 * vgs * Math.Tanh(0.3 * vds);

    /// <summary>Id and Qg by table (fitted here), the gate branches and Qd by ANN.</summary>
    public static ComposedModel MixedModel()
    {
        double[] vgsSites = [.. Enumerable.Range(0, 17).Select(i => -3.0 + 0.25 * i)];
        double[] vdsSites = [.. Enumerable.Range(0, 21).Select(i => 0.5 * i)];
        double[] Grid(Func<double, double, double> f)
            => [.. vgsSites.SelectMany(g => vdsSites.Select(d => f(g, d)))];

        var arrays = new SortedDictionary<string, DataCube>(StringComparer.Ordinal);
        foreach (var kv in TensorSplineFit.TableArrays("Id", Inputs, [vgsSites, vdsSites], Grid(IdAnalytic))) arrays[kv.Key] = kv.Value;
        foreach (var kv in TensorSplineFit.TableArrays("Qg", Inputs, [vgsSites, vdsSites], Grid(QgAnalytic))) arrays[kv.Key] = kv.Value;
        foreach (var (net, name) in new[] { (IgGsNet, "IgGs"), (IgGdNet, "IgGd"), (QdNet, "Qd") })
            foreach (var kv in net.Arrays(name)) arrays[kv.Key] = kv.Value;

        return ComposedTestModels.Model() with
        {
            Core = new CoreConstituents(
                Constituent.Table("Id", Inputs, Pentagon),
                Constituent.Gate(Constituent.AnnMethod, "IgGs", "IgGd", Inputs, Pentagon) with { Activation = AnnActivation.Sigmoid },
                Constituent.Table("Qg", Inputs, Pentagon),
                Constituent.Ann("Qd", Inputs, Pentagon)),
            Extrapolation = new ModelExtrapolation(new Dictionary<string, double> { ["Id"] = 2.0, ["IgGs"] = 0.1, ["IgGd"] = 0.1 }),
            Arrays = arrays,
        };
    }

    /// <summary>The core of a written model, at its base periphery and 25 °C.</summary>
    public static ComposedCoreModel Core(ComposedModel model)
    {
        string path = ComposedTestModels.WriteTemp(model);
        var loaded = ComposedModelCache.Load(path);
        return new ComposedCoreModel(loaded, loaded.N0, 25);
    }
}
