using System.Text.Json;
using CircuitRF.Design.Schematic;

namespace CircuitRF.SchematicSynth;

/// <summary>One class of <c>tools/schematic-ml/classes.json</c>, as the generator needs it.</summary>
public sealed record SynthClass(string Name, IReadOnlyList<string>? Pins, IReadOnlyList<string> Variants);

/// <summary>
/// <c>tools/schematic-ml/classes.json</c> — the one class list the generators, the training kit and the exported models
/// share (IML-1 §3) — embedded into this assembly at build time, so a generated label can never name a class the list
/// does not have, and a change to the list is a change to what the next batch says.
/// </summary>
public static class ClassList
{
    private static readonly Lazy<(Dictionary<string, SynthClass> Classes, string Charset)> Data = new(Load);

    public static IReadOnlyDictionary<string, SynthClass> Classes => Data.Value.Classes;

    /// <summary>The characters the text net can output; a generator never draws one outside it.</summary>
    public static string Charset => Data.Value.Charset;

    /// <summary>
    /// The class each circuitRF kind is drawn as, in circuitRF's own pictures. A kind absent here has no class, and a
    /// circuit that would draw one is never generated: ink nobody labelled teaches a detector that a part is background.
    ///
    /// <para>Classes circuitRF never DRAWS are absent by construction: <c>capacitor-polarised</c>, <c>power-port</c>,
    /// <c>op-amp</c>, <c>ic-box</c>, <c>stub-short</c> (circuitRF draws a TLIN and a ground symbol), <c>transformer</c>
    /// and the annotation classes but <c>controller</c>. Those are other editors' conventions — IML-3's.</para>
    /// </summary>
    public static readonly IReadOnlyDictionary<SymbolKind, string> OfKind = new Dictionary<SymbolKind, string>
    {
        [SymbolKind.Resistor] = "resistor", [SymbolKind.Capacitor] = "capacitor", [SymbolKind.Inductor] = "inductor",
        [SymbolKind.Ground] = "ground", [SymbolKind.Pin] = "terminal", [SymbolKind.Term] = "port",
        [SymbolKind.P1Tone] = "source-ac", [SymbolKind.Vdc] = "source-dc", [SymbolKind.CurrentToneSource] = "source-current",
        [SymbolKind.Diode] = "diode", [SymbolKind.BjtNpn] = "bjt-npn", [SymbolKind.BjtPnp] = "bjt-pnp",
        [SymbolKind.FetCurtice] = "fet-n", [SymbolKind.PFetCurtice] = "fet-p", [SymbolKind.Amp] = "amplifier",
        [SymbolKind.Snp] = "block", [SymbolKind.Tline] = "tline", [SymbolKind.Mlin] = "mlin", [SymbolKind.MTee] = "mtee",
        [SymbolKind.MCross] = "mcross", [SymbolKind.MBend] = "mbend", [SymbolKind.Mtaper] = "mstep",
        [SymbolKind.ViaGnd] = "via", [SymbolKind.Atten] = "attenuator", [SymbolKind.Filter] = "filter",
        [SymbolKind.Circulator] = "circulator", [SymbolKind.Switch] = "switch", [SymbolKind.SwitchD] = "switch-spdt",
        [SymbolKind.Coupler] = "coupler", [SymbolKind.Balun] = "balun", [SymbolKind.Mixer] = "mixer",
        [SymbolKind.Var] = "controller", [SymbolKind.Meas] = "controller",
    };

    /// <summary>The variant a circuitRF kind's glyph IS: circuitRF's DC source is drawn as a battery.</summary>
    public static string? VariantOf(SymbolKind kind) => kind == SymbolKind.Vdc ? "battery" : null;

    private static (Dictionary<string, SynthClass>, string) Load()
    {
        using var stream = typeof(ClassList).Assembly.GetManifestResourceStream("classes.json")
            ?? throw new InvalidOperationException("classes.json is not embedded in this assembly.");
        using var doc = JsonDocument.Parse(stream);
        var classes = new Dictionary<string, SynthClass>(StringComparer.Ordinal);
        foreach (var c in doc.RootElement.GetProperty("classes").EnumerateArray())
        {
            string name = c.GetProperty("name").GetString()!;
            var pins = c.GetProperty("pins");
            IReadOnlyList<string>? pinNames = pins.ValueKind == JsonValueKind.Array
                ? [.. pins.EnumerateArray().Select(p => p.GetProperty("name").GetString()!)]
                : null;
            IReadOnlyList<string> variants = c.TryGetProperty("variants", out var v)
                ? [.. v.EnumerateArray().Select(e => e.GetString()!)]
                : [];
            classes[name] = new SynthClass(name, pinNames, variants);
        }
        return (classes, doc.RootElement.GetProperty("charset").GetString()!);
    }
}
