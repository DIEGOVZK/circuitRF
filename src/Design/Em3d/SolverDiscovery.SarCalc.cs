// brief-sar-5 R-sar5-6 — openEMS's sar_calc, found beside the openEMS discovery already chose. It is not a solver of
// its own: it ships in the same bin directory as openEMS (upstream's build script installs both there, and its Windows
// archive carries both), it has no version of its own to validate, and a SAR cross-check is only meaningful against
// the openEMS that wrote the dump. So it is looked for next to that program, and nowhere else.

namespace CircuitRF.Design.Em3d;

public sealed partial class SolverDiscovery
{
    /// <summary>The executable's file name on this operating system.</summary>
    public static string SarCalcFileName => OperatingSystem.IsWindows() ? "sar_calc.exe" : "sar_calc";

    /// <summary>
    /// sar_calc beside <paramref name="openEms"/> — in the directory of the path discovery resolved, or of what that path
    /// links to — or null when there is none. Null too for an openEMS in the Linux subsystem: the cross-check runs a
    /// native program on a native path.
    /// </summary>
    public static string? SarCalcBeside(SolverInstallation openEms)
    {
        ArgumentNullException.ThrowIfNull(openEms);
        if (openEms.Tool != SolverTool.OpenEms || openEms.Distribution is not null) return null;
        var dirs = new List<string>();
        try
        {
            var fi = new FileInfo(openEms.Path);
            if (fi.DirectoryName is { } dir) dirs.Add(dir);
            if (fi.LinkTarget is not null && fi.ResolveLinkTarget(returnFinalTarget: true) is FileInfo target && target.DirectoryName is { } targetDir)
                dirs.Add(targetDir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        return dirs.Select(d => Path.Combine(d, SarCalcFileName)).FirstOrDefault(File.Exists);
    }
}
