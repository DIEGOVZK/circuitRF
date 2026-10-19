// Renaming a drawing against the schematic it is about to be added to — lifted out of the image paste's own merge
// (brief-schimp-1 R-si1-9) so Import Schematic's drop and Paste Image as Schematic rename by one rule.
//
// Nothing here adds anything to the open schematic: the caller's editor adds the renamed drawing through its own
// commands, as one undo step.

namespace CircuitRF.Design.Schematic;

/// <summary>What <see cref="SchematicMerge.RenameAgainst"/> renamed, old name → new.</summary>
public sealed record SchematicMergeRenames(
    Dictionary<string, string> Variables,
    Dictionary<string, string> Nets,
    Dictionary<string, string> Instances);

public static class SchematicMerge
{
    /// <summary>
    /// Every variable and net name of <paramref name="model"/> that <paramref name="into"/> already has given a suffix,
    /// so nothing new joins anything there — and with <paramref name="instances"/>, every instance name too, renumbered
    /// past the highest <paramref name="into"/> uses for its prefix. A variable's uses in parameter expressions and in
    /// the tuning block follow it; a net label keeps its new name on every label of the old one.
    /// </summary>
    public static SchematicMergeRenames RenameAgainst(SchematicEditModel model, SchematicEditModel into, bool instances = false)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(into);

        var haveVariables = into.Components.Where(c => c.Symbol == SymbolKind.Var).SelectMany(c => c.Parameters).Select(p => p.Name)
                                .Concat(into.Tuning?.Variables.Select(v => v.Key) ?? []).ToHashSet(StringComparer.Ordinal);
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var v in model.Components.Where(c => c.Symbol == SymbolKind.Var).SelectMany(c => c.Parameters))
            if (haveVariables.Contains(v.Name))
            {
                string n = v.Name;
                for (int k = 2; haveVariables.Contains(n) || variables.ContainsValue(n); k++) n = $"{v.Name}_{k}";
                variables[v.Name] = n;
            }
        foreach (var c in model.Components)
            foreach (var p in c.Parameters)
            {
                if (c.Symbol == SymbolKind.Var && variables.TryGetValue(p.Name, out var renamed)) p.Name = renamed;
                else if (variables.TryGetValue(p.Expression, out var used)) p.Expression = used;
            }
        if (model.Tuning is { } tuning) foreach (var t in tuning.Variables) if (variables.TryGetValue(t.Key, out var r)) t.Key = r;

        var haveNets = into.NetLabels.Select(l => l.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nets = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var label in model.NetLabels)
        {
            if (!haveNets.Contains(label.Name) && !nets.ContainsKey(label.Name)) continue;
            if (!nets.TryGetValue(label.Name, out var n))
            {
                n = label.Name;
                for (int k = 2; haveNets.Contains(n) || nets.ContainsValue(n); k++) n = $"{label.Name}_{k}";
                nets[label.Name] = n;
            }
            label.Name = n;
        }

        var renamedInstances = new Dictionary<string, string>(StringComparer.Ordinal);
        if (instances)
        {
            var taken = into.Components.Select(c => c.InstanceName).Where(n => n.Length > 0).ToHashSet(StringComparer.Ordinal);
            var own = model.Components.Select(c => c.InstanceName).Where(n => n.Length > 0).ToHashSet(StringComparer.Ordinal);
            foreach (var c in model.Components)
            {
                if (c.InstanceName.Length == 0 || !taken.Contains(c.InstanceName)) { taken.Add(c.InstanceName); continue; }
                string prefix = c.InstanceName.TrimEnd("0123456789".ToCharArray());
                if (prefix.Length == 0) prefix = c.InstanceName + "_";
                int k = taken.Concat(own).Where(n => n.StartsWith(prefix, StringComparison.Ordinal)
                                                     && int.TryParse(n.AsSpan(prefix.Length), out _))
                             .Select(n => int.Parse(n.AsSpan(prefix.Length))).DefaultIfEmpty(0).Max() + 1;
                string next = $"{prefix}{k}";
                renamedInstances[c.InstanceName] = next;
                c.InstanceName = next;
                taken.Add(next);
            }
        }

        return new SchematicMergeRenames(variables, nets, renamedInstances);
    }
}
