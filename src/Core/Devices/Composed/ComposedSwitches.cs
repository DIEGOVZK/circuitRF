using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Core.Devices.Composed;

/// <summary>
/// The instance switches of a composed FET (brief-modelcomposer-21 R-mc21-1): which parts of the model one instance
/// evaluates. None refits anything — the <c>.cmodel</c> is untouched, and the defaults are the model as fitted, so an
/// instance that states none of them is exactly the instance it always was.
///
/// <para>The ONE place the six names are read and written: the elaborator, the schematic's extraction and annotation,
/// the parameter editor and <c>explain</c> all go through here, so they cannot disagree about a spelling or a default.
/// Every value is read by <see cref="BooleanParameter"/>; a bare spelling (<c>off</c>, <c>no</c>) is taken as written
/// before anything is evaluated, because to the expression engine it is a variable name.</para>
/// </summary>
public sealed record ComposedSwitches
{
    public const string ChargeName = "Charge", LinearCName = "LinearC", GateLagName = "GateLag",
                        DrainLagName = "DrainLag", SelfHeatingName = "SelfHeating", DispersionName = "Dispersion";

    /// <summary>The six names in their fixed order — the order <see cref="Write"/> writes and the editor lists.</summary>
    public static readonly IReadOnlyList<string> Names =
        [ChargeName, LinearCName, GateLagName, DrainLagName, SelfHeatingName, DispersionName];

    /// <summary><c>false</c>: no intrinsic charge at all. The extrinsic network's capacitors stay.</summary>
    public bool Charge { get; init; } = true;

    /// <summary><c>true</c>: both intrinsic charges linearised at the quiescent DC operating point.</summary>
    public bool LinearC { get; init; }

    /// <summary><c>false</c>: every gate-lag trap state follows its DC value at the instantaneous voltages.</summary>
    public bool GateLag { get; init; } = true;

    /// <summary><c>false</c>: every drain-lag trap state follows its DC value at the instantaneous voltages.</summary>
    public bool DrainLag { get; init; } = true;

    /// <summary><c>false</c>: evaluated at the run's ambient, no power into the thermal node.</summary>
    public bool SelfHeating { get; init; } = true;

    /// <summary><c>false</c>: the drain carries <c>IdDC</c> at every frequency; no dispersion node.</summary>
    public bool Dispersion { get; init; } = true;

    public static readonly ComposedSwitches Default = new();

    public bool IsDefault => this == Default;

    /// <summary>Whether the intrinsic charge is linearised: <see cref="LinearC"/>, unless <see cref="Charge"/> removes it.</summary>
    public bool LinearCharge => Charge && LinearC;

    /// <summary>A switch's default.</summary>
    public static bool DefaultOf(string name) => name != LinearCName;

    /// <summary>Whether <paramref name="name"/> is one of the six, as written (ordinal, like every parameter name).</summary>
    public static bool IsSwitch(string name) => Names.Contains(name, StringComparer.Ordinal);

    /// <summary>A switch's value by name.</summary>
    public bool this[string name] => name switch
    {
        ChargeName      => Charge,
        LinearCName     => LinearC,
        GateLagName     => GateLag,
        DrainLagName    => DrainLag,
        SelfHeatingName => SelfHeating,
        DispersionName  => Dispersion,
        _ => throw new ArgumentException($"'{name}' is not a ComposedFet switch.", nameof(name)),
    };

    private ComposedSwitches With(string name, bool value) => name switch
    {
        ChargeName      => this with { Charge = value },
        LinearCName     => this with { LinearC = value },
        GateLagName     => this with { GateLag = value },
        DrainLagName    => this with { DrainLag = value },
        SelfHeatingName => this with { SelfHeating = value },
        DispersionName  => this with { Dispersion = value },
        _ => this,
    };

    /// <summary>
    /// The switches an instance line states. <paramref name="evaluate"/> evaluates an override that is not a bare
    /// spelling in the instance's parent scope; anything that is not a yes or no is refused by name with the list.
    /// </summary>
    public static ComposedSwitches Read(IEnumerable<ParameterAssignment> overrides, Func<ParameterAssignment, Value> evaluate)
    {
        var s = Default;
        foreach (var ov in overrides)
        {
            if (!IsSwitch(ov.Name)) continue;
            Value value;
            if (Spelling(ov.Expression) is { } b) value = new Value(b);
            else
            {
                try { value = evaluate(ov); }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // A word that is neither a spelling nor a name in scope is refused as a switch, with the list —
                    // the evaluator's own complaint (an unresolved name) kept beside it.
                    try { BooleanParameter.Parse(ov.Name, new Value(ov.Expression.Trim())); }
                    catch (InvalidOperationException refusal) { throw new InvalidOperationException($"{refusal.Message} ({ex.Message})", ex); }
                    throw;
                }
            }
            s = s.With(ov.Name, BooleanParameter.Parse(ov.Name, value));
        }
        return s;
    }

    /// <summary>The switches a resolved parameter set carries (the core's own instance).</summary>
    public static ComposedSwitches Read(IReadOnlyDictionary<string, Value> parameters)
    {
        var s = Default;
        foreach (string name in Names)
            if (parameters.TryGetValue(name, out var v)) s = s.With(name, BooleanParameter.Parse(name, v));
        return s;
    }

    /// <summary>The non-default switches as <c>key=value</c> pairs, in <see cref="Names"/>' order; empty for the default.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Write()
        => [.. Names.Where(n => this[n] != DefaultOf(n)).Select(n => new KeyValuePair<string, string>(n, this[n] ? "true" : "false"))];

    /// <summary>The short summary the schematic draws under a switched instance — <c>linear C · no gate lag · isothermal</c>;
    /// empty for the default.</summary>
    public string Describe() => string.Join(" · ", Phrases(sentence: false));

    /// <summary>The run note's form — <c>linear C at the bias point, no gate lag</c>; empty for the default.</summary>
    public string Sentence() => string.Join(", ", Phrases(sentence: true));

    private IEnumerable<string> Phrases(bool sentence)
    {
        if (!Charge)       yield return "no intrinsic charge";
        else if (LinearC)  yield return sentence ? "linear C at the bias point" : "linear C";
        if (!GateLag)      yield return "no gate lag";
        if (!DrainLag)     yield return "no drain lag";
        if (!SelfHeating)  yield return "isothermal";
        if (!Dispersion)   yield return "no dispersion";
    }

    /// <summary>
    /// The annotation for switches as a document holds them, each as its raw text (null or blank: not stated). A bare
    /// spelling reads as its value; an expression cannot be evaluated here and is shown as written, <c>GateLag=gl</c>, so
    /// a switch bound to a variable is never mistaken for one left at its default.
    /// </summary>
    public static string Annotate(Func<string, string?> rawOf)
    {
        var literal = Default;
        var shown   = new List<string>();
        foreach (string name in Names)
        {
            string? raw = rawOf(name)?.Trim();
            if (string.IsNullOrEmpty(raw)) continue;
            if (Spelling(raw) is { } b) literal = literal.With(name, b);
            else shown.Add($"{name}={raw}");
        }
        string text = literal.Describe();
        if (text.Length > 0) shown.Insert(0, text);
        return string.Join(" · ", shown);
    }

    /// <summary>Whether raw text states the default (blank, or a spelling of the default) — what extraction leaves out.</summary>
    public static bool StatesDefault(string name, string? raw)
        => string.IsNullOrWhiteSpace(raw) || Spelling(raw) == DefaultOf(name);

    /// <summary>A bare yes-or-no spelling, or null for anything else (an expression, a number other than 0 or 1).</summary>
    public static bool? Spelling(string raw)
    {
        string t = raw.Trim().Trim('"');
        return BooleanParameter.TryParse(new Value(t), out bool b) ? b : null;
    }
}
