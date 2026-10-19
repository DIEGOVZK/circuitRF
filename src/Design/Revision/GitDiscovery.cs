using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CircuitRF.Design.Revision;

/// <summary>A git circuitRF can run.</summary>
/// <param name="Path">
/// The executable, resolved to an ABSOLUTE path once and started from there. A bare name resolves
/// through <c>PATH</c>, and <c>PATH</c> is attacker-influenced on the platforms this targets — the
/// same finding <c>src/Ui/Updates/ProcessRunner.cs</c> records from the 2026-08-25 security review.
/// </param>
/// <param name="Version">The version it reported, parsed. Compared against <see cref="GitDiscovery.MinimumVersion"/>.</param>
/// <param name="Banner">What it said when asked, trimmed to one line. RC-4's Detect shows this.</param>
/// <param name="HowFound">Where it came from, in words — "set in Settings", "found on PATH".</param>
public sealed record GitInstallation(string Path, Version Version, string Banner, string HowFound);

/// <summary>
/// Finds the git circuitRF drives, and answers "is there one" without ever surprising a user who does
/// not want this feature (<c>docs/design/revision-control.md</c> §4.3, §4.3a, §4.7).
///
/// <para><b>Absence is silent</b> (R-rc3-3). No git configured and none on <c>PATH</c> means the
/// feature is simply not there — every affordance hidden, not disabled. A designer who does not want
/// revision control should never learn it exists. This is the opposite of RC-6's HOLD state, and the
/// difference is the point: absent is harmless, held is a designer who may believe they are
/// protected.</para>
///
/// <para><b>Settings ▸ Revision Control is the one exception, by the owner's decision of
/// 2026-09-07</b> (<c>CircuitRF.Ui.Revision.RevisionTabAvailability</c>). That tab is shown on every
/// machine with its rows greyed, because it is where somebody goes to find out whether git works here
/// — and hiding it hid the fact that circuitRF can keep a history and is not keeping one. Everywhere
/// else the silence above is unchanged.</para>
///
/// <para><b>A configured path outranks <c>PATH</c></b> (R-rc3-2), exactly as
/// <c>VerilogACompilerDiscovery</c> does and for the same reason: a preference that lost to
/// <c>PATH</c> would be inert on precisely the machine it exists for — the one with two gits, or one
/// somewhere <c>PATH</c> does not reach. Blank means "search <c>PATH</c>".</para>
///
/// <para><b>On macOS, presence is established without running anything named <c>git</c></b>
/// (R-rc3-2a, §4.3a). <c>/usr/bin/git</c> exists on every Mac whether or not git is installed: with
/// the Command Line Tools absent it is a shim that OPENS APPLE'S "install the developer tools?"
/// DIALOG instead of running. Discovery by <c>git --version</c> would therefore show that dialog,
/// unbidden, on every launch, to exactly the users §4.3 promises never learn the feature exists. So
/// the tools are checked for first — by a file that exists only when they are installed, falling back
/// to <c>xcode-select -p</c>, which is a real binary and not a shim — and a shim without tools is
/// treated as ABSENT. A path the user CONFIGURED is run as configured, because there they named
/// it.</para>
///
/// <para><b>A git below the floor counts as absent, not as broken</b> (R-rc3-3a). A designer with an
/// old git does not want to be told about a feature they cannot have.</para>
/// </summary>
public static class GitDiscovery
{
    /// <summary>
    /// The oldest git circuitRF will drive.
    ///
    /// <para><b>Set by <c>core.hooksPath</c> (git 2.9, 2016), which is R-rc3-7a's hook bypass</b> —
    /// every commit circuitRF makes supplies an empty <c>core.hooksPath</c> per invocation, because
    /// <c>--no-verify</c> skips <c>pre-commit</c> and <c>commit-msg</c> and leaves <c>post-commit</c>
    /// to fire. Nothing else in this project needs anything newer: the checkpoint is plumbing
    /// (<c>write-tree</c>, <c>commit-tree</c>, <c>update-ref</c>, <c>GIT_INDEX_FILE</c>), the pack
    /// trigger is <c>count-objects -v</c>, and the exclusion pathspec magic <c>:(exclude)</c> is
    /// 1.9.</para>
    ///
    /// <para><b><c>safe.directory</c> does NOT raise it, which is worth writing down because it looks
    /// as though it should</b> (§4.7). The ownership check arrived in 2.35.2 and in the backports
    /// 2.30.3/2.31.2/2.32.1/2.33.2/2.34.2 — and every git that ENFORCES it also ACCEPTS the
    /// <c>-c safe.directory=</c> answer. A git old enough to lack the option is old enough to lack the
    /// check, so it needs no answer. R-rc3-4's row for it exists for the versions in between, if any
    /// turn up.</para>
    ///
    /// <para><b>Lower it deliberately, not by guessing.</b> The requirement above is the whole reason
    /// for the number; if <c>core.hooksPath</c> ever stops being how hooks are bypassed, this floor is
    /// free to move.</para>
    /// </summary>
    public static readonly Version MinimumVersion = new(2, 9, 0);

    /// <summary>
    /// The git the user named in application settings, or null when they have named none. Installed by
    /// <c>src/Ui</c> (RC-4), the same seam and for the same reason as
    /// <c>VerilogACompilerDiscovery.PreferredCommand</c> — the preference lives above the firewall and
    /// this assembly may not reach across it.
    ///
    /// <para>A <c>Func</c> rather than a string so the answer is read at the moment it is needed: a
    /// user who changes the setting must get the git they just named.</para>
    /// </summary>
    public static Func<string?>? PreferredPath { get; set; }

    /// <summary>
    /// Names a git for one process, outranking <c>PATH</c> and beaten only by the user's own
    /// preference. How a headless run — CI, a test, a batch job — points at one without writing
    /// anyone's preferences file.
    /// </summary>
    public const string EnvironmentVariable = "CRF_GIT";

    /// <summary>The bare names searched on <c>PATH</c>, and then in <see cref="SearchDirectories"/>.
    /// A list so a test can empty it, which disables both.</summary>
    public static IReadOnlyList<string> CandidateCommands { get; set; } = ["git"];

    /// <summary>
    /// R-rc3-2a's seam: whether macOS's developer tools are actually installed. Settable so gate 20 can
    /// drive the "absent" answer on every platform and assert that NO process named <c>git</c> is
    /// started — the real shim cannot be exercised in CI, because CI has the tools.
    /// </summary>
    public static Func<bool>? DeveloperToolsPresent { get; set; }

    /// <summary>Every path this class has started a process from, in order. Gate 20 reads it.</summary>
    internal static IReadOnlyList<string> ProbedPaths => _probed;
    private static readonly List<string> _probed = [];

    private static GitInstallation?       _cached;
    private static IReadOnlyList<string>  _cachedRejections = [];
    private static string?                _cachedKey;
    private static bool                   _cacheValid;

    /// <summary>Drop the memoised answer — after the setting changes, or in a test.</summary>
    public static void InvalidateCache()
    {
        _cacheValid       = false;
        _cached           = null;
        _cachedRejections = [];
        lock (_probed) _probed.Clear();
    }

    /// <summary>
    /// The git to use, or null with <paramref name="rejected"/> explaining each candidate.
    ///
    /// <para><b>The rejections are memoised with the answer, and that is not tidiness.</b> They are the
    /// whole content of the one place an under-floor or wrong-path git is ever reported — RC-4's Detect
    /// line — and a cache that kept the null and dropped the reasons made the SECOND press of that
    /// button answer "no git was found on PATH" where the first had said which git was found and why it
    /// was refused. The user's situation had not changed; only circuitRF's memory of it had.</para>
    /// </summary>
    public static GitInstallation? Find(out IReadOnlyList<string> rejected)
    {
        string? preferred = PreferredPath?.Invoke()?.Trim();
        string  key       = preferred ?? "";

        // Memoised, but keyed on the preference: a user who names a different git and asks again must
        // not get the previous answer back.
        if (_cacheValid && string.Equals(_cachedKey, key, StringComparison.Ordinal))
        {
            rejected = _cachedRejections;
            return _cached;
        }

        var notes = new List<string>();
        rejected  = notes;

        var found = Locate(preferred, notes);

        _cached           = found;
        _cachedRejections = notes;
        _cachedKey        = key;
        _cacheValid       = true;
        return found;
    }

    /// <summary>Whether the feature exists at all on this machine (R-rc3-3).</summary>
    public static bool IsAvailable => Find(out _) is not null;

    private static GitInstallation? Locate(string? preferred, List<string> notes)
    {
        // A path the user NAMED is run as configured — including on macOS, and including a path that
        // IS the shim, because there the user asked for it (R-rc3-2a). Never falls back: a user who
        // named a git and silently got a different one has been overruled without being told.
        if (preferred is { Length: > 0 })
        {
            if (TryProbe(preferred, "set in Settings", out var chosen, out string? why)) return chosen;
            notes.Add($"the git set in Settings ('{preferred}'): {why}");
            return null;
        }

        if (Environment.GetEnvironmentVariable(EnvironmentVariable)?.Trim() is { Length: > 0 } fromEnv)
        {
            if (TryProbe(fromEnv, $"named by {EnvironmentVariable}", out var chosen, out string? why)) return chosen;
            notes.Add($"the git named by {EnvironmentVariable} ('{fromEnv}'): {why}");
            return null;
        }

        // R-rc3-2a. Everything below this line may START A PROCESS, so the shim check comes first and
        // stops the search dead rather than degrading to a slower answer.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && !MacDeveloperToolsPresent())
        {
            notes.Add("macOS: the developer tools are not installed, so /usr/bin/git is a shim that "
                    + "would open Apple's install dialog rather than run");
            return null;
        }

        foreach (string command in CandidateCommands)
        {
            if (string.IsNullOrWhiteSpace(command)) continue;
            foreach (string candidate in ResolveOnPath(command))
            {
                if (TryProbe(candidate, "found on PATH", out var chosen, out string? why)) return chosen;
                notes.Add($"'{candidate}': {why}");
            }
        }

        foreach (string candidate in WellKnownInstallPaths())
        {
            if (TryProbe(candidate, $"found at {candidate}", out var chosen, out string? why)) return chosen;
            notes.Add($"'{candidate}': {why}");
        }

        return null;
    }

    /// <summary>
    /// Where a user-installed git is looked for once <c>PATH</c> has failed — <c>~/.local/bin</c>,
    /// <c>/opt/homebrew/bin</c>, <c>/usr/local/bin</c>. Empty on Windows.
    ///
    /// <para><b>An application started from a launcher does not inherit the user's <c>PATH</c>.</b> A
    /// Finder-launched macOS bundle gets <c>/usr/bin:/bin:/usr/sbin:/sbin</c> from launchd and nothing
    /// of the login shell; a Linux desktop entry is no better. So "on <c>PATH</c>" means the user's
    /// SHELL, and a search that stops there can only find what is installed in the system
    /// directories — which on Linux is not where a user-local git lives. macOS is saved from this by
    /// <c>/usr/bin/git</c> being there anyway, so it is Linux the tier actually rescues; it is added
    /// on both because the premise is the same and the cost is a <see cref="File.Exists(string)"/>
    /// per entry. Found the same week as the identical gap in the Verilog-A compiler's discovery,
    /// which had no such saving grace — see <c>src/Core/RESOLVED.md</c>.</para>
    ///
    /// <para><b>Below the macOS shim gate, deliberately</b>, so it changes nothing about R-rc3-2a:
    /// with the developer tools absent the search still stops dead before anything is started, even
    /// though none of these directories could hold the shim. Lifting that is a decision about gate
    /// 20's own invariant, not a side effect of this tier.</para>
    ///
    /// <para>Derived from <see cref="CandidateCommands"/>, so emptying that list still disables every
    /// unprompted route, which is what it is documented to do. Settable for the same reason that list
    /// is — so a test can point the mechanism somewhere it controls.</para>
    /// </summary>
    public static IReadOnlyList<string> SearchDirectories { get; set; } = DefaultSearchDirectories();

    private static string[] DefaultSearchDirectories()
    {
        if (OperatingSystem.IsWindows()) return [];

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return home.Length > 0
            ? [Path.Combine(home, ".local", "bin"), "/opt/homebrew/bin", "/usr/local/bin"]
            : ["/opt/homebrew/bin", "/usr/local/bin"];
    }

    private static IEnumerable<string> WellKnownInstallPaths()
    {
        foreach (string command in CandidateCommands)
        {
            if (string.IsNullOrWhiteSpace(command)) continue;
            // A command carrying a separator was already resolved as itself by ResolveOnPath.
            if (command.Contains(Path.DirectorySeparatorChar)
                || command.Contains(Path.AltDirectorySeparatorChar)) continue;

            foreach (string directory in SearchDirectories)
            {
                if (string.IsNullOrWhiteSpace(directory)) continue;
                string candidate;
                try { candidate = Path.Combine(directory, command.Trim()); }
                catch (ArgumentException) { continue; }
                bool exists;
                try { exists = File.Exists(candidate); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                if (exists) yield return candidate;
            }
        }
    }

    /// <summary>
    /// Whether macOS's Command Line Tools (or Xcode) are actually there.
    ///
    /// <para>The FILE check first, because it starts nothing at all. <c>xcode-select</c> is the
    /// fallback and is safe to run: it is a real binary, not one of the shims, and with the tools
    /// absent it prints an error and exits non-zero rather than offering to install anything.</para>
    /// </summary>
    private static bool MacDeveloperToolsPresent()
    {
        if (DeveloperToolsPresent is { } seam) return seam();

        foreach (string p in (string[])
                 ["/Library/Developer/CommandLineTools/usr/bin/git",
                  "/Applications/Xcode.app/Contents/Developer/usr/bin/git"])
        {
            try { if (File.Exists(p)) return true; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        try
        {
            var psi = new ProcessStartInfo("/usr/bin/xcode-select")
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            psi.ArgumentList.Add("-p");
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            if (!p.WaitForExit(10_000)) { try { p.Kill(true); } catch { /* already gone */ } return false; }
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// Every absolute path a bare command resolves to on <c>PATH</c>, in order. Resolved HERE rather
    /// than left to the process start, so what is started is a path this class chose and a test can
    /// read back (<see cref="ProbedPaths"/>).
    /// </summary>
    private static IEnumerable<string> ResolveOnPath(string command)
    {
        if (command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar))
        {
            yield return Path.GetFullPath(command);
            yield break;
        }

        string[] exts = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';',
                  StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [""];

        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string ext in exts)
            {
                string candidate;
                try { candidate = Path.Combine(dir.Trim(), command + ext); }
                catch (ArgumentException) { continue; }   // a malformed PATH entry is not fatal
                bool exists;
                try { exists = File.Exists(candidate); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                if (exists) yield return candidate;
            }
        }
    }

    /// <summary>
    /// Runs a candidate and asks what it is. Public so RC-4's Detect can report a path the user typed
    /// without going through <see cref="Find"/>'s precedence.
    /// </summary>
    public static bool TryProbe(
        string path, string howFound, out GitInstallation? installation, out string? why)
    {
        installation = null;
        why          = null;

        string absolute;
        try { absolute = Path.GetFullPath(path); }
        catch (Exception e) when (e is ArgumentException or PathTooLongException or NotSupportedException)
        {
            why = "that is not a usable path";
            return false;
        }

        lock (_probed) _probed.Add(absolute);

        string banner;
        try
        {
            var psi = new ProcessStartInfo(absolute)
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                RedirectStandardInput  = true,
                CreateNoWindow         = true,
            };
            psi.ArgumentList.Add("--version");
            GitEnvironment.Apply(psi.Environment);

            using var probe = Process.Start(psi);
            if (probe is null) { why = "it could not be started"; return false; }
            probe.StandardInput.Close();   // never the serve pipe — Em3dProcessLauncher.Start says why

            string stdout = probe.StandardOutput.ReadToEnd();
            string stderr = probe.StandardError.ReadToEnd();
            if (!probe.WaitForExit((int)ProbeTimeout.TotalMilliseconds))
            {
                try { probe.Kill(entireProcessTree: true); } catch { /* already gone */ }
                why = "it did not answer";
                return false;
            }
            banner = FirstLine(stdout.Trim().Length > 0 ? stdout : stderr);
        }
        catch (Exception ex)
        {
            why = ex.Message;
            return false;
        }

        if (ParseVersion(banner) is not { } version)
        {
            // Something is at that path and it is not git. R-rc3-4's row: the sentence names the path
            // that was tried, and there is no stack trace anywhere near it.
            why = banner.Length == 0
                ? "it started but identified itself with nothing"
                : $"it identified itself as '{banner}', which is not a git";
            return false;
        }

        if (version < MinimumVersion)
        {
            // R-rc3-3a: ABSENT, not broken. The caller reports nothing.
            why = $"it is version {version}, older than the {MinimumVersion} circuitRF needs";
            return false;
        }

        installation = new GitInstallation(absolute, version, banner, howFound);
        return true;
    }

    /// <summary>How long a candidate has to identify itself. A probe that hangs — a stale symlink, a
    /// shim waiting on a network store — must not hold up a workspace open.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The version out of <c>git version 2.50.1 (Apple Git-155)</c>, or null when the banner is not
    /// git's. Deliberately strict about the leading words: that is what makes "something else is at
    /// this path" a detectable answer rather than a plausible one.
    /// </summary>
    internal static Version? ParseVersion(string banner)
    {
        const string prefix = "git version ";
        int at = banner.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;

        string rest = banner[(at + prefix.Length)..].Trim();
        var parts   = new List<int>();
        foreach (string piece in rest.Split(' ')[0].Split('.'))
        {
            // Trailing vendor suffixes ("2.39.3.windows.1", "2.24.3 (Apple Git-128)") stop the walk
            // rather than failing it.
            int digits = 0;
            while (digits < piece.Length && char.IsAsciiDigit(piece[digits])) digits++;
            if (digits == 0) break;
            parts.Add(int.Parse(piece[..digits]));
            if (digits != piece.Length) break;
            if (parts.Count == 4) break;
        }

        return parts.Count switch
        {
            0 => null,
            1 => new Version(parts[0], 0),
            2 => new Version(parts[0], parts[1]),
            3 => new Version(parts[0], parts[1], parts[2]),
            _ => new Version(parts[0], parts[1], parts[2], parts[3]),
        };
    }

    private static string FirstLine(string s)
    {
        string line = s.Split('\n', '\r').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
        return line.Length <= 200 ? line : line[..200];
    }
}
