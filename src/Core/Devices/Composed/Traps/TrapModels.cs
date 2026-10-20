namespace CircuitRF.Core.Devices.Composed.Traps;

/// <summary>
/// The trap models by name (brief-modelcomposer-10 R-mc10-1, D10): <c>CaptureEmission</c> and <c>Custom</c> built in, and
/// any a developer registers. <see cref="ModelTraps.None"/> is not a model — it is the absence of one. A <c>.cmodel</c>
/// naming a model this build does not know is refused by name.
/// </summary>
public static class TrapModels
{
    private static readonly object Gate = new();
    private static IReadOnlyDictionary<string, TrapModelDefinition> _models = new Dictionary<string, TrapModelDefinition>(StringComparer.Ordinal)
    {
        [CaptureEmissionTraps.ModelName] = new CaptureEmissionTraps(),
        [CustomTraps.ModelName] = new CustomTraps(),
    };

    /// <summary>Every name a <c>Traps.Model</c> may carry: <c>None</c>, then the models in registration order.</summary>
    public static IReadOnlyList<string> Names => [ModelTraps.None, .. _models.Keys];

    public static TrapModelDefinition? Find(string name) => _models.TryGetValue(name, out var m) ? m : null;

    /// <summary>Adds a model. A name already registered, or <c>None</c>, is refused.</summary>
    public static void Register(TrapModelDefinition model)
    {
        if (model.States.Count is < 1 or > TrapDynamics.MaxStates)
            throw new ArgumentException($"a trap model declares 1 to {TrapDynamics.MaxStates} states.");
        lock (Gate)
        {
            if (model.Name == ModelTraps.None || _models.ContainsKey(model.Name))
                throw new ArgumentException($"'{model.Name}' is already a trap model name.");
            _models = new Dictionary<string, TrapModelDefinition>(_models, StringComparer.Ordinal) { [model.Name] = model };
        }
    }

    /// <summary>
    /// The block checked and compiled: a known model, one or more distinct states it declares, finite parameters it
    /// declares (or any, for a model whose parameters are the user's), levels only for enabled states — then the model's
    /// own checks. Every refusal is a <see cref="CmodelFormatException"/> by field.
    /// </summary>
    public static TrapDynamics Build(ModelTraps traps, string label)
    {
        var model = Find(traps.Model) ?? throw new CmodelFormatException("Traps.Model",
            $"'{traps.Model}' is not a known trap model; the known models are {string.Join(", ", Names)}.");
        if (traps.States.Count == 0)
            throw new CmodelFormatException("Traps.States", $"no state is enabled; {model.Name}'s states are {string.Join(", ", model.States)}.");
        if (traps.States.Distinct(StringComparer.Ordinal).Count() != traps.States.Count)
            throw new CmodelFormatException("Traps.States", "a state is listed twice.");
        foreach (string s in traps.States)
            if (!model.States.Contains(s))
                throw new CmodelFormatException("Traps.States", $"'{s}' is not a state of {model.Name}; its states are {string.Join(", ", model.States)}.");
        var ordered = model.States.Where(traps.States.Contains).ToList();
        if (!ordered.SequenceEqual(traps.States))
            throw new CmodelFormatException("Traps.States", $"the states are listed in the model's order ({string.Join(", ", ordered)}).");

        if (!ModelTraps.Couplings.Contains(traps.Coupling))
            throw new CmodelFormatException("Traps.Coupling",
                $"'{traps.Coupling}' is not a coupling; the couplings are {string.Join(", ", ModelTraps.Couplings)}.");
        // R-mc19-1: a shift coupling reads one strength per enabled state, under a name the model declares for it.
        var shift = model.States.Select(model.ShiftParameter).OfType<string>().ToList();
        if (traps.Shifted)
        {
            if (shift.Count == 0)
                throw new CmodelFormatException("Traps.Coupling",
                    $"a {ModelTraps.Shift} coupling is defined for {CaptureEmissionTraps.ModelName}; {model.Name} has no shift strengths.");
            foreach (string s in traps.States)
                if (model.ShiftParameter(s) is { } strength && traps.Parameter(strength) is null)
                    throw new CmodelFormatException($"Traps.Parameters.{strength}",
                        $"it is missing; a {ModelTraps.Shift} coupling needs {s}'s strength, in V/V.");
        }

        foreach (var (name, value) in traps.Parameters)
        {
            if (shift.Contains(name))
            {
                if (!traps.Shifted)
                    throw new CmodelFormatException($"Traps.Parameters.{name}", $"it is a strength of the {ModelTraps.Shift} coupling; the coupling is {traps.Coupling}.");
                if (!double.IsFinite(value))
                    throw new CmodelFormatException($"Traps.Parameters.{name}", "it is not a finite strength.");
                continue;
            }
            if (model.DeclaresAllParameters && !model.Parameters.Any(p => p.Name == name))
                throw new CmodelFormatException($"Traps.Parameters.{name}",
                    $"it is not a parameter of {model.Name}; they are {string.Join(", ", model.Parameters.Select(p => p.Name))}.");
            if (!double.IsFinite(value))
                throw new CmodelFormatException($"Traps.Parameters.{name}", "it is not a finite number.");
        }
        foreach (var (state, levels) in traps.Levels)
        {
            if (!traps.States.Contains(state))
                throw new CmodelFormatException($"Traps.Levels.{state}", "it is not an enabled state.");
            if (levels.Any(l => !double.IsFinite(l)))
                throw new CmodelFormatException($"Traps.Levels.{state}", "a level is not a finite voltage.");
        }
        if (model is not CustomTraps && traps.Expressions.Count > 0)
            throw new CmodelFormatException("Traps.Expressions", $"only a {CustomTraps.ModelName} model is written as expressions.");
        return model.Build(traps, label);
    }
}
