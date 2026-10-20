using System.Text.RegularExpressions;
using CircuitRF.Core;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Core.Tests.Composed;
using CircuitRF.Engine.HarmonicBalance;
using static CircuitRF.Engine.Tests.Composed.ComposedFetEquivalenceTests;

namespace CircuitRF.Engine.Tests.Composed;

/// <summary>
/// brief-modelcomposer-21 R-mc21-3: the bias-point freeze. An unfrozen linear-charge core refuses to return a charge, by
/// name; every engine that reads charge freezes first — warm-started HB included — and re-freezes when the DC point moves.
/// </summary>
public sealed class BiasFreezeTests
{
    private static readonly string ModelPath = ComposedTestModels.WriteTemp(ComposedTestModels.Model());

    private static string LinearFet => $"ComposedFet:X1 g d 0 File=\"{ModelPath}\" LinearC=true\n";

    private static ComposedCoreModel Core(ElaboratedNetlist nl)
        => nl.Components.Select(c => c.Model).OfType<ComposedCoreModel>().Single();

    [Fact]
    public void AnUnfrozenLinearCore_RefusesByName_ExceptWhereNoChargeIsRead()
    {
        var core = Core(Elaborate(Bench(-0.6, 6) + LinearFet));
        var v = new PortVoltages(new double[core.PortCount]);
        var ex = Assert.Throws<ComposedEvaluationException>(() => core.Evaluate(v));
        Assert.Contains("'X1'", ex.Message);
        Assert.Contains("bias point was never set", ex.Message);

        using (ChargeReading.NotRead()) core.Evaluate(v);
    }

    [Fact]
    public void ATwoPointSweepOverTheBias_FreezesTwice()
    {
        var (lib, tb) = new CnlReader().Read($"""
            vg = -0.6
            {Bench(-0.6, 6).Replace("V=-0.6", "V=vg")}{LinearFet}analysis SP1 type=sparam start=1 stop=3 npts=3 Unit=GHz
            analysis SW1 type=parametric_sweep Var=vg Values=-0.8,-0.4 Inner=SP1
            """);
        BiasFreeze.ResetCount();
        ParametricSweepEngine.Run(tb.Analyses.OfType<ParametricSweepAnalysis>().Single(), lib, tb);
        Assert.Equal(2, BiasFreeze.FreezeCount);
    }

    [Fact]
    public void AWarmStartedHbSolve_StillFreezes()
    {
        string cnl = """
            V_1Tone:VS src 0 Vdc=-0.6 V=0.3 Freq=2e9 Phase=0
            R:RS src g R=50
            Vdc:VD vd 0 V=6
            R:RL vd d R=50
            analysis HB1 type=hb Tone=2e9 MaxHarm=4

            """ + LinearFet;
        HbRunResult Solve(System.Numerics.Complex[,]? seed, out ElaboratedNetlist nl)
        {
            var (lib, tb) = new CnlReader().Read(cnl);
            nl = new Elaborator(lib).Elaborate(tb);
            var hba = tb.Analyses.OfType<HarmonicBalanceAnalysis>().Single();
            return new HbEngine(nl, tb).Run(HbEngine.Resolve(hba, nl.ResolvedGlobals), seed);
        }

        var cold = Solve(null, out _);
        Assert.True(cold.Converged);
        BiasFreeze.ResetCount();
        var warm = Solve(cold.InterfaceV, out var fresh);
        Assert.True(warm.Converged);
        Assert.Equal(1, BiasFreeze.FreezeCount);
        Assert.True(Core(fresh).IsFrozen);
    }

    /// <summary>
    /// Every engine entry point that reads charge freezes first. A file in <c>src/Engine</c> or <c>src/Harmonica</c> that
    /// solves HB or linearises a device must call <see cref="BiasFreeze"/>; each public <c>Run*</c> of <c>HbEngine</c>
    /// must, in its own body (a warm start skips everything else); and the member that solves must be the one that freezes.
    /// </summary>
    [Fact]
    public void EveryEngineEntryPointThatReadsCharge_Freezes()
    {
        string root = RepoRoot();
        var reads = new Regex(@"\bHbNewton(2D|Nd)?\.Solve\(|\.StampLinearized(Passive)?\(");
        // The member that SOLVES: an HB Newton solve, or the S-parameter engine's operating point, which it linearises at.
        var hbSolve = new Regex(@"\bHbNewton(2D|Nd)?\.Solve\(");
        var dcSolve = new Regex(@"\bNonlinearDcEngine\.Run\(");
        var checkedFiles = new List<string>();

        foreach (string dir in (string[])["src/Engine", "src/Harmonica"])
            foreach (string file in Directory.EnumerateFiles(Path.Combine(root, dir), "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                    file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
                string name = Path.GetFileName(file);
                if (name is "HbNewton.cs" or "HbNewton2D.cs" or "HbNewtonNd.cs" or "BiasFreeze.cs") continue;
                string code = Strip(File.ReadAllText(file));
                if (!reads.IsMatch(code)) continue;
                checkedFiles.Add(name);

                Assert.True(code.Contains("BiasFreeze."), $"{name} reads charge and never freezes the bias point.");
                foreach (var (header, body) in Members(code))
                {
                    bool entry = name == "HbEngine.cs" && Regex.IsMatch(header, @"\bpublic\s+[\w.<>\[\],? ]+\s+Run\w*\s*\(");
                    bool solver = name != "HbEngine.cs"
                               && (hbSolve.IsMatch(body) || name == "SParameterEngine.cs" && dcSolve.IsMatch(body));
                    if (entry || solver)
                        Assert.True(body.Contains("BiasFreeze."),
                            $"{name}: '{header.Trim().Split('\n')[^1].Trim()}' reads charge before any freeze.");
                }
            }

        Assert.Contains("SParameterEngine.cs", checkedFiles);
        Assert.Contains("HbEngine.cs", checkedFiles);
        Assert.Contains("HarmonicaContext.cs", checkedFiles);
    }

    // ── harness ──────────────────────────────────────────────────────────────

    /// <summary>Comments and string contents out, so neither a mention in prose nor a brace in a string counts.</summary>
    private static string Strip(string code)
    {
        code = Regex.Replace(code, @"/\*.*?\*/", "", RegexOptions.Singleline);
        code = Regex.Replace(code, @"""(?:[^""\\\n]|\\.)*""", "\"\"");
        code = Regex.Replace(code, @"'(?:[^'\\\n]|\\.)'", "' '");
        return Regex.Replace(code, @"//[^\n]*", "");
    }

    /// <summary>The members of a file-scoped type: each top-level brace block inside the type, with the text before it.</summary>
    private static IEnumerable<(string Header, string Body)> Members(string code)
    {
        int depth = 0, start = 0, last = 0;
        for (int i = 0; i < code.Length; i++)
        {
            if (code[i] == '{')
            {
                if (++depth == 2) start = i;
            }
            else if (code[i] == '}')
            {
                if (depth-- == 2)
                {
                    yield return (code[last..start], code[start..(i + 1)]);
                    last = i + 1;
                }
                if (depth == 0) last = i + 1;
            }
            else if (code[i] == ';' && depth == 1) last = i + 1;
        }
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "circuitRF.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
