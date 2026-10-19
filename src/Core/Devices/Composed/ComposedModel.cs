using RfCore.Data;

namespace CircuitRF.Core.Devices.Composed;

/// <summary>
/// The contents of a <c>.cmodel</c> — a composed nonlinear FET model, the file a
/// <c>ComposedFet</c> instance names (brief-modelcomposer-1 R-mc1-1; <c>docs/design/model-composer.md</c>).
///
/// <para>The container is a ZIP holding <c>model.json</c> (this record, minus <see cref="Arrays"/>) and
/// one <c>arrays/&lt;name&gt;.npy</c> per entry of <see cref="Arrays"/>. <see cref="CmodelFile"/> reads,
/// validates and writes it; nothing else touches the bytes.</para>
/// </summary>
public sealed record ComposedModel
{
    /// <summary>The newest <see cref="FormatVersion"/> this build reads and the one it writes.</summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>The only <see cref="Kind"/> there is so far: a three-terminal FET.</summary>
    public const string FetKind = "Fet";

    public int    FormatVersion { get; init; } = CurrentFormatVersion;
    public string Kind          { get; init; } = FetKind;

    /// <summary>Always <c>G D S</c> for a FET: pins 1, 2 and 3 of the symbol, in that order.</summary>
    public IReadOnlyList<string> Terminals { get; init; } = ["G", "D", "S"];

    /// <summary>The periphery of the measured device, which <c>N</c> scales from (D9).</summary>
    public required BasePeriphery BasePeriphery { get; init; }

    public required ModelTemperature Temperature { get; init; }

    public required ExtrinsicNetwork Network { get; init; }

    public required CoreConstituents Core { get; init; }

    /// <summary>Guided extrapolation's settings (R-mc2-3): present when a current constituent is a
    /// table or an ANN, absent for an all-equation model.</summary>
    public ModelExtrapolation? Extrapolation { get; init; }

    /// <summary>A library symbol name, or a <c>.csym</c> path relative to the <c>.cmodel</c>. Null =
    /// the standard three-terminal FET symbol.</summary>
    public string? Symbol { get; init; }

    public ModelProvenance Provenance { get; init; } = new();

    /// <summary>The <c>arrays/*.npy</c> entries, by name. Tables and network weights (MC-2); empty for
    /// an all-equation model.</summary>
    public IReadOnlyDictionary<string, DataCube> Arrays { get; init; } = new Dictionary<string, DataCube>();
}

/// <summary>The measured device's finger count <c>N0</c> and unit gate width <c>W0</c>, the latter as
/// written (<c>"125 um"</c>).</summary>
public sealed record BasePeriphery(int Fingers, string UnitWidth);

public enum TemperatureMode { Single, Range }

/// <summary>
/// The temperature a model is valid at (D8). <see cref="TemperatureMode.Single"/>: one temperature
/// <see cref="T0"/>, and no temperature input. <see cref="TemperatureMode.Range"/>: every constituent
/// takes <c>T</c>, clamped to [<see cref="Tmin"/>, <see cref="Tmax"/>]. All in °C.
/// </summary>
public sealed record ModelTemperature(TemperatureMode Mode, double T0, double Tmin, double Tmax)
{
    public static ModelTemperature Single(double t0)               => new(TemperatureMode.Single, t0, t0, t0);
    public static ModelTemperature Range(double tmin, double tmax) => new(TemperatureMode.Range, double.NaN, tmin, tmax);
}

/// <summary>
/// The extrinsic network (D5): <c>.cnl</c> text holding one <c>define</c> block with exactly six
/// pins — extrinsic gate, drain, source, then intrinsic gate, drain, source — and its variables'
/// values; plus the scaling rule of each variable that is not <c>fixed</c> (D9).
/// </summary>
public sealed record ExtrinsicNetwork(string Netlist, IReadOnlyList<ScalingRule> Scaling);

/// <summary>One network variable's periphery rule, by name (<see cref="PeripheryScaling"/>).</summary>
public sealed record ScalingRule(string Variable, string Rule);

/// <summary>The four constituents of D4, and the optional dispersive <c>IdRF</c>.</summary>
public sealed record CoreConstituents(
    Constituent Id, Constituent Ig, Constituent Qg, Constituent Qd, Constituent? IdRF = null)
{
    /// <summary>The drain–source symmetry option (R-mc2-4). Off by default: a field-plated power
    /// device is deliberately asymmetric ([P1] §6.5.5).</summary>
    public DrainSourceSymmetry Symmetry { get; init; } = DrainSourceSymmetry.None;
}

/// <summary>
/// <see cref="Exchange"/>: the constituents are defined for <c>vds ≥ 0</c>, and a point with
/// <c>vds &lt; 0</c> is evaluated at its image <c>(vgs − vds, −vds)</c> with drain and source exchanged
/// ([P1] §6.5, eqs. 6.61–6.64).
/// </summary>
public enum DrainSourceSymmetry { None, Exchange }

/// <summary>
/// <c>Extrapolation</c>: the added conductance <c>G_x</c> of each current payload that is a table or
/// an ANN, by payload — <c>Id</c>, <c>IgGs</c>, <c>IgGd</c> (and <c>IdRF</c>). In siemens, at the
/// base periphery.
/// </summary>
public sealed record ModelExtrapolation(IReadOnlyDictionary<string, double> Gx)
{
    /// <summary>The order the writer puts them in.</summary>
    public static readonly IReadOnlyList<string> Keys = ["Id", "IgGs", "IgGd", "IdRF"];
}

/// <summary>
/// One constituent: its method, the inputs it may read, and the method's payload, held in
/// <see cref="Expressions"/> by key — one entry for <c>Id</c>/<c>Qg</c>/<c>Qd</c>/<c>IdRF</c>, keyed by the
/// method (<see cref="PayloadKey"/>); two, <c>IgGs</c> and <c>IgGd</c>, for <c>Ig</c>.
/// <list type="bullet">
/// <item><c>Equation</c>: each entry is an expression; <see cref="Parameters"/> are the named
/// constants it reads.</item>
/// <item><c>Table</c>: each entry names arrays <c>&lt;name&gt;.values</c> and
/// <c>&lt;name&gt;.coefficients</c>; <see cref="Smoothing"/> is the λ the coefficients were fitted with.</item>
/// <item><c>ANN</c>: each entry names a network's arrays (<see cref="ComposedArrays.Ann"/>);
/// <see cref="Activation"/> is its hidden layers' activation.</item>
/// </list>
/// A table or ANN carries its training <see cref="Domain"/>; an equation is global and has none.
/// </summary>
public sealed record Constituent
{
    public const string EquationMethod = "Equation";
    public const string TableMethod    = "Table";
    public const string AnnMethod      = "ANN";

    public required string Method { get; init; }
    public required IReadOnlyList<string> Inputs { get; init; }
    public IReadOnlyList<KeyValuePair<string, string>> Expressions { get; init; } = [];
    public IReadOnlyList<KeyValuePair<string, double>> Parameters  { get; init; } = [];

    /// <summary>Where a table or ANN was trained (R-mc2-3). Null for an equation.</summary>
    public TrainingDomain? Domain { get; init; }

    /// <summary>A table's smoothing weight λ; 0 is interpolation (R-mc2-1).</summary>
    public double? Smoothing { get; init; }

    /// <summary>An ANN's hidden-layer activation (R-mc2-2).</summary>
    public AnnActivation? Activation { get; init; }

    /// <summary>The single-payload key of this method: <c>Expression</c>, <c>Table</c> or <c>Network</c>.</summary>
    public string PayloadKey => PayloadKeyOf(Method);

    public static string PayloadKeyOf(string method) => method switch
    {
        TableMethod => "Table",
        AnnMethod   => "Network",
        _           => "Expression",
    };

    /// <summary>One equation by its payload key, or null.</summary>
    public string? Expression(string key)
        => Expressions.FirstOrDefault(e => e.Key == key) is { Key: not null } kv ? kv.Value : null;

    public static Constituent Equation(string expression, IReadOnlyList<string> inputs,
                                       params (string Name, double Value)[] parameters)
        => new()
        {
            Method      = EquationMethod,
            Inputs      = inputs,
            Expressions = [new("Expression", expression)],
            Parameters  = [.. parameters.Select(p => new KeyValuePair<string, double>(p.Name, p.Value))],
        };

    /// <summary>A table constituent naming the arrays under <paramref name="name"/>.</summary>
    public static Constituent Table(string name, IReadOnlyList<string> inputs, TrainingDomain domain,
                                    double smoothing = 0.0)
        => new() { Method = TableMethod, Inputs = inputs, Expressions = [new("Table", name)],
                   Domain = domain, Smoothing = smoothing };

    /// <summary>An ANN constituent naming the arrays under <paramref name="name"/>.</summary>
    public static Constituent Ann(string name, IReadOnlyList<string> inputs, TrainingDomain domain,
                                  AnnActivation activation = AnnActivation.Tanh)
        => new() { Method = AnnMethod, Inputs = inputs, Expressions = [new("Network", name)],
                   Domain = domain, Activation = activation };

    /// <summary>A gate-conduction pair by table or ANN: the two branches' array names.</summary>
    public static Constituent Gate(string method, string igGs, string igGd, IReadOnlyList<string> inputs,
                                   TrainingDomain domain)
        => new()
        {
            Method = method, Inputs = inputs, Expressions = [new("IgGs", igGs), new("IgGd", igGd)],
            Domain = domain,
            Smoothing  = method == TableMethod ? 0.0 : null,
            Activation = method == AnnMethod ? AnnActivation.Tanh : null,
        };

    /// <summary>A gate-conduction equation pair: the gate–source and gate–drain branch currents.</summary>
    public static Constituent GateEquations(string igGs, string igGd, IReadOnlyList<string> inputs,
                                            params (string Name, double Value)[] parameters)
        => new()
        {
            Method      = EquationMethod,
            Inputs      = inputs,
            Expressions = [new("IgGs", igGs), new("IgGd", igGd)],
            Parameters  = [.. parameters.Select(p => new KeyValuePair<string, double>(p.Name, p.Value))],
        };
}

/// <summary>Where a model came from. Written as given — the writer stamps nothing itself, which is
/// what makes the same model write the same bytes.</summary>
public sealed record ModelProvenance
{
    public string Created          { get; init; } = "";
    public string CircuitRfVersion { get; init; } = "";
    public IReadOnlyList<ModelSource> Sources { get; init; } = [];
    public string SettingsDigest   { get; init; } = "";
}

public sealed record ModelSource(string Path, string Sha256);
