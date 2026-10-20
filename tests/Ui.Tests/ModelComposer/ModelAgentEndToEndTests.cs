using System.Globalization;
using CircuitRF.Core.Tests.Composed;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// brief-modelcomposer-13 §4: an agent's whole path with no display at any step — raw synthetic files, a <c>.cmc</c>
/// created, both sets imported, de-embedded, fitted, checked, exported, explained, then a netlist placing the exported
/// model simulated by <c>circuitrf hb</c> — every step the built CLI as a process. The amplifier's output at the
/// fundamental is held to the truth's own SDD in the same bench.
/// </summary>
public sealed class ModelAgentEndToEndTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("crf-mc13-e2e-").FullName;

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

    /// <summary>A class-AB stage: gate driven through 50 Ω, drain fed through a choke into a 50 Ω load.</summary>
    private static string Bench(string device) =>
        "V_1Tone:VG vs 0 Vdc=-1.0 Freq=2e9 V=0.2\nR:RG vs g R=50\n" +
        "Vdc:VD vdd 0 V=5\nL:LD vdd d L=1e-6\nC:CD d o C=1e-9\nR:RL o 0 R=50\n" +
        device +
        "analysis HB1 type=hb Tone=2e9 MaxHarm=5\n";

    [Fact]
    public void FromRawFiles_ToAnHbRunOfTheExportedModel_HeadlessThroughout()
    {
        ModelCliFixture.WriteRaw(_dir);
        Step("model", "new", "fet.cmc", "--kind", "active", "--network", "gan-two-shell", "--fingers", "4", "--unit-width", "125 um");
        Step(["model", "import", "fet.cmc", .. ModelCliFixture.ImportS]);
        Step(["model", "import", "fet.cmc", .. ModelCliFixture.ImportDc]);
        var deembed = Step("model", "deembed", "fet.cmc");
        Assert.Contains("bias: 12 point(s)", deembed, StringComparison.Ordinal);

        ModelCliFixture.Quick(Path.Combine(_dir, "fet.cmc"));    // the agent edits the step settings, then fits
        Step("model", "fit", "fet.cmc", "--step", "all", "--seed", "1");
        Step("model", "check-charge", "fet.cmc");
        Step("model", "export", "fet.cmc", "-o", "fet.cmodel");
        var explain = Step("model", "explain", "fet.cmc");
        Assert.Contains("Current: fresh.", explain, StringComparison.Ordinal);
        Assert.Contains("Charge: fresh.", explain, StringComparison.Ordinal);

        File.WriteAllText(Path.Combine(_dir, "amp.cnl"), Bench("ComposedFET:X1 g d 0 File=fet.cmodel\n"));
        Step("hb", "amp.cnl", "-o", "amp.npy");
        File.WriteAllText(Path.Combine(_dir, "truth.cnl"),
            ComposedTestModels.SddGlobals + ComposedTestModels.ReferenceCell("ref") + Bench("ref:X1 g d 0\n"));
        Step("hb", "truth.cnl", "-o", "truth.npy");

        double model = ModelCliFixture.Fundamental(Path.Combine(_dir, "amp.npy"), "o");
        double truth = ModelCliFixture.Fundamental(Path.Combine(_dir, "truth.npy"), "o");
        double db = 20 * Math.Log10(model / truth);
        output.WriteLine($"|V(o)| at f0: model {model:G5} V, truth {truth:G5} V, {db.ToString("F3", CultureInfo.InvariantCulture)} dB");
        Assert.True(Math.Abs(db) < 1, $"the exported model's output is {db:F2} dB from the truth's");
    }

    private string Step(params string[] args)
    {
        var run = ModelCliFixture.Run(_dir, args);
        output.WriteLine($"$ circuitrf {string.Join(' ', args)}  → {run.ExitCode}\n{run.StdOut}{run.StdErr}");
        Assert.Equal(0, run.ExitCode);
        return run.StdOut;
    }
}
