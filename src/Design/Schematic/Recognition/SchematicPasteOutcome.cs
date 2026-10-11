// What a paste did, in one line — brief-imf-9-auto-settings-and-paste.md R-imf9-4 (overview L1).
//
// Paste asks nothing, so the one thing it owes the user is a true account: how much was pasted, and which of it needs
// a look — a part of no known kind (O3's ?n), an amplifier, IC or System block standing on default parameters, a value
// that was not read and became a tunable global. Those are the parts the paste selects, and the line says how to
// correct them: the dialog, opened on the same picture with the same reading. Built here, from the reading alone, so
// the line is the same whichever surface shows it.

using System.Globalization;

namespace CircuitRF.Design.Schematic.Recognition;

/// <summary>The paste's line and the instances it selects for correcting (R-imf9-4).</summary>
/// <param name="ToCorrect">Instance names, as emitted, of the parts that need a look.</param>
public sealed record SchematicPasteOutcome(string Sentence, IReadOnlyList<string> ToCorrect, int Parts, int Nets, int Ports)
{
    public const string CorrectIn = "Create Schematic from Image";

    public static SchematicPasteOutcome Of(SchematicImageResult reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        var circuit = reading.Circuit ?? throw new ArgumentException("The reading made no circuit.", nameof(reading));
        var instances = circuit.TestBench.Instances;
        int ports = circuit.Ports.Count;
        int parts = instances.Count(i => i.Reference != "Port");
        int nets = instances.SelectMany(i => i.RefNetBinding is { } r ? [.. i.NetBindings, r] : i.NetBindings)
                            .Distinct(StringComparer.Ordinal).Count();

        var symbols = reading.Symbols?.Symbols ?? [];
        ImageSymbolKind? KindOf(string name) =>
            circuit.SymbolOf.TryGetValue(name, out int k) && k >= 0 && k < symbols.Count ? symbols[k].Kind : null;
        var variables = circuit.Variables.ToHashSet(StringComparer.Ordinal);
        var unknown = circuit.UnknownParts.ToHashSet(StringComparer.Ordinal);
        List<string> notRecognised = [], amplifiers = [], blocks = [], unread = [];
        foreach (var i in instances)
        {
            if (unknown.Contains(i.InstanceName)) notRecognised.Add(i.InstanceName);
            else if (KindOf(i.InstanceName) == ImageSymbolKind.Amplifier) amplifiers.Add(i.InstanceName);
            else if (KindOf(i.InstanceName) is ImageSymbolKind.Ic or ImageSymbolKind.Block || i.Reference == "SnP") blocks.Add(i.InstanceName);
            else if (i.Overrides.Any(p => variables.Contains(p.Expression))) unread.Add(i.InstanceName);
        }

        static string N(int n, string one, string many) => n.ToString(CultureInfo.InvariantCulture) + " " + (n == 1 ? one : many);
        string pasted = $"Pasted {N(parts, "part", "parts")}, {N(nets, "net", "nets")}, {N(ports, "port", "ports")} from the picture";
        var said = new List<string>();
        if (notRecognised.Count > 0) said.Add(N(notRecognised.Count, "part", "parts") + " not recognised");
        if (amplifiers.Count > 0) said.Add(N(amplifiers.Count, "amplifier", "amplifiers") + " with default gain");
        if (blocks.Count > 0) said.Add(N(blocks.Count, "IC or block", "ICs or blocks") + " with default parameters");
        if (unread.Count > 0) said.Add(N(unread.Count, "part", "parts") + " whose value was not read");
        List<string> flagged = [.. notRecognised, .. amplifiers, .. blocks, .. unread];
        string sentence = said.Count == 0
            ? pasted + "."
            : $"{pasted} — {string.Join(", ", said)} (selected): open {(flagged.Count == 1 ? "it" : "them")} in {CorrectIn} to correct.";
        return new SchematicPasteOutcome(sentence, flagged, parts, nets, ports);
    }
}
