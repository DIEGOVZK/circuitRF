using System.Text.RegularExpressions;
using CircuitRF.Ui.Updates;

namespace CircuitRF.Ui.Tests.Updates;

/// <summary>
/// What the session that applied a macOS update may still do after it has exchanged its own bundle
/// (owner report, 2026-09-11, beta.17 to beta.18: the Relaunch button closed the application and
/// macOS reported that it had quit unexpectedly).
///
/// <para><b>The finding these hold.</b> circuitRF ships as a .NET single-file bundle, so the runtime
/// re-opens the application executable BY PATH every time it loads an assembly for the first time.
/// The exchange replaces that path's file, so from that moment every not-yet-loaded assembly fails
/// with <c>FileNotFoundException</c> — including <c>System.Diagnostics.Process</c>, which is what the
/// hand-over used to spawn <c>/usr/bin/open</c> with. The exception escaped into
/// <c>RunBeforeUi</c>'s catch-all, the launch carried on into <c>Main</c>, and the next call it made
/// needed another assembly and aborted the process. No successor was ever started and nothing was
/// written down, which is also why the 2026-09-10 investigation could find no <c>open</c> process in
/// the log and no reason for its absence.</para>
///
/// <para><b>So the rule is: after the exchange this process runs only already-loaded code</b> — the
/// runtime's own core library, and <c>libc</c>. Measured, not assumed: on a pair of single-file
/// bundles exchanged with the updater's own <c>renamex_np(RENAME_SWAP)</c>, file I/O and <c>libc</c>
/// P/Invoke keep working afterwards (entry points never called before the exchange included), while
/// <c>Process.Start</c> and <c>System.Text.Json</c> both throw.</para>
/// </summary>
public sealed class HandOverAfterBundleExchangeTests
{
    // ── the primitive itself ────────────────────────────────────────────────────────────────────

    /// <summary>The success answer, and the exit code, both come back — this is what tells a refused
    /// Launch Services request from an accepted one.</summary>
    [Fact]
    public void ASpawnedChildsExitCodeComesBack()
    {
        if (Bin("true") is not { } yes || Bin("false") is not { } no) return;

        Assert.Equal(0, NativeLaunch.TrySpawn(yes, [], out int okPid));
        Assert.True(NativeLaunch.TryWaitForExit(okPid, 10_000, out int okCode));
        Assert.Equal(0, okCode);

        Assert.Equal(0, NativeLaunch.TrySpawn(no, [], out int badPid));
        Assert.True(NativeLaunch.TryWaitForExit(badPid, 10_000, out int badCode));
        Assert.Equal(1, badCode);
    }

    /// <summary>
    /// The arguments actually reach the child. <c>open -n -a &lt;bundle&gt; --args …</c> is the whole
    /// hand-over, so an argument vector that is built but not delivered would relaunch the
    /// application without the one flag that keeps a successor from racing its predecessor.
    /// </summary>
    [Fact]
    public void TheArgumentVectorReachesTheChild()
    {
        if (Bin("touch") is not { } touch) return;

        string marker = Path.Combine(Path.GetTempPath(), $"crf-spawn-{Guid.NewGuid():N}");

        Assert.Equal(0, NativeLaunch.TrySpawn(touch, [marker], out int pid));
        Assert.True(NativeLaunch.TryWaitForExit(pid, 10_000, out int code));
        Assert.Equal(0, code);
        Assert.True(File.Exists(marker), $"the child did not receive '{marker}' as an argument");

        File.Delete(marker);
    }

    /// <summary>
    /// A program that is not there is a REFUSAL with a reason, not an exception and not a silent
    /// success. <c>posix_spawn</c> returns the error rather than setting <c>errno</c>, which is the
    /// one convention a caller has to get right.
    /// </summary>
    [Fact]
    public void AProgramThatIsNotThereIsRefusedWithAnErrno()
    {
        if (Bin("true") is null) return;   // no POSIX spawn to test at all

        int errno = NativeLaunch.TrySpawn("/nonexistent/not-a-program", [], out _);

        Assert.NotEqual(0, errno);
        Assert.Equal(2, errno);   // ENOENT
    }

    // ── the rule, held in the source ────────────────────────────────────────────────────────────

    /// <summary>
    /// The hand-over may not go through <c>System.Diagnostics.Process</c>: its assembly is exactly
    /// the one that cannot be loaded any more by the time this code runs. A comment saying so does
    /// not satisfy the rule, so the source is read with its comments stripped.
    /// </summary>
    [Fact]
    public void TheLaunchServicesRequestDoesNotGoThroughSystemDiagnosticsProcess()
    {
        string body = Body(Source("AppRelaunch.cs"), "private static bool OpenNewInstance(");

        Assert.DoesNotContain("Process", body, StringComparison.Ordinal);
        Assert.Contains("NativeLaunch.TrySpawn", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// And it may not leave through <c>Environment.Exit</c>, which raises <c>ProcessExit</c> — a hook
    /// any part of the application may have taken, in an assembly this session may no longer be able
    /// to load.
    /// </summary>
    [Theory]
    [InlineData("private static void HandOverTo(")]
    [InlineData("private static void LeaveTheUpdateForTheNextLaunch(")]
    [InlineData("private static void LeaveRatherThanOutliveTheExchange(")]
    public void NothingOnThePathOutOfAnExchangedBundleCallsEnvironmentExit(string signature)
    {
        string body = Body(Source("UpdateStartup.cs"), signature);

        Assert.DoesNotContain("Environment.Exit", body, StringComparison.Ordinal);
        Assert.Contains("NativeLaunch.Exit", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The one flag the refusal diagnostics read lives in ANOTHER assembly, so it is written before
    /// the exchange rather than after it. Ordering is the whole content of this test: the same two
    /// statements in the other order are the crash.
    /// </summary>
    [Fact]
    public void TheReplacedBundleFlagIsSetBeforeTheExchangeNotOnlyAfterIt()
    {
        string run = Body(Source("UpdateStartup.cs"), "public static void RunBeforeUi(");

        int first = run.IndexOf("AppBundleReplacedThisSession", StringComparison.Ordinal);
        int swap  = run.IndexOf("UpdateSwap.ApplyAtLaunch", StringComparison.Ordinal);

        Assert.True(first >= 0 && swap >= 0, "RunBeforeUi no longer has the statements this pins.");
        Assert.True(first < swap,
                    "CircuitRF.Diagnostics is loaded for the first time by this write. After the "
                    + "exchange it cannot be loaded at all, so the write has to happen before the "
                    + "swap — see NativeLaunch for the measurement.");
    }

    /// <summary>
    /// Serialising state is a post-exchange call on every branch, and it loads an assembly that
    /// DESERIALISING never did — so the write path is made resident before anything moves.
    ///
    /// <para>Measured on a pair of exchanged single-file bundles, with and without the priming: with
    /// it the post-exchange write succeeds; without it, <c>System.IO.Pipelines</c> cannot be loaded
    /// and the write throws. It used to survive only because <c>UpdateSwap.SwapBundle</c> persists
    /// <c>SwapInProgress</c> one line before the exchange for an unrelated durability reason — and
    /// <c>Revert</c>, the rollback, has no such neighbour.</para>
    /// </summary>
    [Fact]
    public void TheStateWritePathIsMadeResidentBeforeTheExchange()
    {
        string run = Body(Source("UpdateStartup.cs"), "public static void RunBeforeUi(");

        int prime = run.IndexOf("UpdateStateIo.PrimeWritePath", StringComparison.Ordinal);
        int swap  = run.IndexOf("UpdateSwap.ApplyAtLaunch", StringComparison.Ordinal);

        Assert.True(prime >= 0,
                    "RunBeforeUi no longer primes the state WRITE path. Every branch below the swap "
                    + "writes state, and JsonSerializer.Serialize loads System.IO.Pipelines — which a "
                    + "session that has exchanged its own bundle cannot load at all.");
        Assert.True(prime < swap,
                    "The priming has to happen BEFORE the exchange. After it, it is the crash.");
    }

    /// <summary>
    /// And the rollback is the branch that has no accidental warming of its own: it writes the
    /// blacklist entry and the notice AFTER <c>Revert</c> has exchanged the bundles back. This pins
    /// the shape rather than the ordering, so the reason the priming exists cannot quietly go away.
    /// </summary>
    [Fact]
    public void TheRollbackBranchWritesStateAfterTheExchangeAndHasNoWarmingOfItsOwn()
    {
        string swapSource = Source("UpdateSwap.cs");
        string revert     = Body(swapSource, "private static SwapResult Revert(");

        int exchange = revert.IndexOf("AtomicFile.SwapDirectories", StringComparison.Ordinal);
        Assert.True(exchange >= 0, "Revert no longer exchanges the bundles — repoint this test.");

        Assert.DoesNotContain("persist(", revert[..exchange]);
    }

    // ── and if something on that path throws anyway ─────────────────────────────────────────────

    /// <summary>
    /// <b>The catch-all must not return.</b> Every report in this series has the same second half: an
    /// exception on the post-exchange path is swallowed by <c>RunBeforeUi</c>'s catch — whose rule, an
    /// updater that can prevent a launch is worse than no updater, is exactly wrong once the bundle is
    /// gone — and then <c>Main</c>'s very next call dies at the prestub, as a crash the user sees and
    /// with nothing written down anywhere. Leaving instead costs one more launch, and the exchange is
    /// already durable, so the version on disk is the new one either way.
    ///
    /// <para>Held as a source scan because the alternative is a test that calls <c>_exit</c> and takes
    /// the test host with it.</para>
    /// </summary>
    [Fact]
    public void AFailureAfterTheExchangeLeavesRatherThanReturningIntoMain()
    {
        string run   = Body(Source("UpdateStartup.cs"), "public static void RunBeforeUi(");
        int    catchAt = run.LastIndexOf("catch (Exception)", StringComparison.Ordinal);

        Assert.True(catchAt >= 0, "RunBeforeUi no longer has the catch-all this pins.");

        string tail = run[catchAt..];
        Assert.Contains("UpdateSwap.BundleExchangedThisSession", tail, StringComparison.Ordinal);
        Assert.Contains("LeaveTheUpdateForTheNextLaunch", tail, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the flag it reads is set AT the exchange, not after whatever follows it — the whole point
    /// is that it is true even when the next statement throws. Both exchanges are pinned: the update
    /// and the rollback.
    /// </summary>
    [Fact]
    public void TheExchangeFlagIsRaisedByTheExchangeItself()
    {
        string swap = Source("UpdateSwap.cs");

        Assert.Equal(2, Occurrences(swap, "AtomicFile.SwapDirectories"));

        foreach (int at in Positions(swap, "AtomicFile.SwapDirectories"))
        {
            string next = swap[at..].Split('\n').Skip(1).First();
            Assert.Contains("BundleExchangedThisSession = true", next, StringComparison.Ordinal);
        }
    }

    private static int Occurrences(string text, string needle) => Positions(text, needle).Count();

    private static IEnumerable<int> Positions(string text, string needle)
    {
        for (int i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(needle, i + 1, StringComparison.Ordinal))
            yield return i;
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Where a standard utility actually is, or null on a platform that has no such thing —
    /// these tests exercise a real spawn, so they need a real program to spawn.</summary>
    private static string? Bin(string name)
        => new[] { $"/usr/bin/{name}", $"/bin/{name}" }.FirstOrDefault(File.Exists);

    private static string Source(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx")))
            dir = dir.Parent;

        Assert.True(dir is not null, "Could not locate the repository root from the test output directory.");

        return WithoutComments(
            File.ReadAllText(Path.Combine(dir!.FullName, "src", "Ui", "Updates", file)));
    }

    /// <summary>A file with its comments removed — a note ABOUT a rule must not satisfy the rule.</summary>
    private static string WithoutComments(string text)
    {
        text = Regex.Replace(text, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return string.Join("\n", text.Split('\n').Select(line =>
        {
            int slash = line.IndexOf("//", StringComparison.Ordinal);
            return slash < 0 ? line : line[..slash];
        }));
    }

    /// <summary>One method's text, from its signature to the closing brace at method indentation.</summary>
    private static string Body(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signature}' is not in the source any more — repoint this test.");

        int end = source.IndexOf("\n    }", start, StringComparison.Ordinal);
        Assert.True(end > start, $"could not find the end of '{signature}'.");

        return source[start..end];
    }
}
