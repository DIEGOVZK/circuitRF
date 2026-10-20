// ================================================================
//  ExampleWorkspaceLoadsTests.cs — the shipped Model Composer example (brief-modelcomposer-16 R-mc16-1): each document
//  opens as it was left, `check` finds nothing wrong with it, and its test benches run.
//
//  Run on a COPY of the example, as File ▸ New ▸ Examples makes one: the copy takes new timestamps, which is exactly the
//  case that once rewrote the cache and lost every fit.
// ================================================================

using CircuitRF.Design.Circuit;
using CircuitRF.Design.Modeling;
using CircuitRF.Ui.Tests.ModelComposer;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Examples;

public sealed class ExampleWorkspaceLoadsTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _ws = CopyOfTheExample();

    public void Dispose() { try { Directory.Delete(Path.GetDirectoryName(_ws)!, true); } catch (IOException) { } }

    private static string CopyOfTheExample()
    {
        string from = Path.Combine(ModelCliFixture.RepoRoot(), "examples", ModelComposerExampleAuthoring.Folder);
        string to = Path.Combine(Directory.CreateTempSubdirectory("crf-mc16-example-").FullName, ModelComposerExampleAuthoring.Folder);
        foreach (string d in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, d)));
        Directory.CreateDirectory(to);
        foreach (string f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(f, Path.Combine(to, Path.GetRelativePath(from, f)));
        return to;
    }

    /// <summary>Every <c>.cmc</c> checks with no error, every step it ran is fresh, and the fitted ones still hold their
    /// fits — the arrays live in the cache, which the copy carries.</summary>
    [Fact]
    public void EveryDocument_ChecksClean_AndIsAsItWasLeft()
    {
        foreach (string cmc in Directory.GetFiles(_ws, "*.cmc", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(_ws, cmc);
            var check = ModelCliFixture.Run(_ws, "check", rel);
            output.WriteLine($"check {rel} → {check.ExitCode}\n{check.StdOut}{check.StdErr}");
            Assert.Equal(0, check.ExitCode);

            var lines = ModelCommands.Explain(cmc).Select(l => l.Text).ToList();
            foreach (string l in lines) output.WriteLine(l);
            Assert.DoesNotContain(lines, l => l.EndsWith(": stale.", StringComparison.Ordinal));
        }
        string gan = Path.Combine(_ws, "synthetic-gan", SyntheticGanDocument.Name);
        var doc = CmcFile.Read(gan);
        Assert.NotNull(CurrentFit.Load(gan, doc));
        Assert.NotNull(ChargeFit.Load(gan, doc));
    }

    /// <summary>The spiral filter runs as Simulate runs it; the two GaN benches check clean. Running those is minutes (27
    /// harmonic-balance points of the trapped, self-heating model, and 49 loadpull loads), so it is opt-in below.</summary>
    [Fact]
    public void TheSpiralFilterRuns_AndTheGanBenchesCheckClean()
    {
        Run("SpiralFilter");
        foreach (string cell in new[] { "GanPowerSweep", "GanLoadpull" })
        {
            string rel = Path.Combine(cell, "schematic", cell + ".csch");
            var check = ModelCliFixture.Run(_ws, "check", rel);
            output.WriteLine($"check {rel} → {check.ExitCode}\n{check.StdOut}{check.StdErr}");
            Assert.Equal(0, check.ExitCode);
        }
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void TheGanBenches_Run()
    {
        Run("GanPowerSweep");
        Run("GanLoadpull");
    }

    private void Run(string cell)
    {
        var r = CircuitEvaluation.Evaluate(PreparedCircuit.FromSchematic(Path.Combine(_ws, cell, "schematic", cell + ".csch")),
                                           new CircuitEvaluationRequest());
        foreach (string w in r.Warnings) output.WriteLine($"{cell}: {w}");
        Assert.True(r.Status == RunStatus.Success, $"{cell}: {r.Status} {r.StatusMessage}");
        Assert.All(r.Convergence, c => Assert.True(c.Converged != false, $"{cell}: {c}"));
    }
}
