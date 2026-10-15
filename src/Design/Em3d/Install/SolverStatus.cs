namespace CircuitRF.Design.Em3d.Install;

/// <summary>
/// One tool's row, as Settings ▸ Solvers shows it and <c>circuitrf solver list</c> prints it
/// (brief-em3d-24 R-em3d24-5a, R-em3d24-7): what discovery found, where and how, whether it is validated,
/// what its build can do, and whether the assistant offers to install it here. <b>Both call
/// <see cref="Of"/></b>, so the window and the terminal cannot disagree about a machine.
/// </summary>
/// <param name="Summary">The row's sentence — <see cref="SolverDiscovery.DescribeForSettings"/>.</param>
/// <param name="Capabilities">Each capability a run could ask, probed — empty unless a run would use the
/// program (<see cref="SolverDiscovery.Runs"/>; an unvalidated build is not probed, as a run does not probe it).</param>
/// <param name="Recipe">The recipe this machine would install, or null when it has none.</param>
/// <param name="OfferInstall">Whether <i>Install …</i> is offered — <see cref="SolverInstaller.OfferFor"/>'s rule.</param>
public sealed record SolverStatus(
    SolverTool Tool, string Name, SolverInstallation? Found, IReadOnlyList<string> Rejected, string Summary,
    IReadOnlyList<SolverCapabilityVerdict> Capabilities, SolverRecipe? Recipe, bool OfferInstall)
{
    /// <summary>True when the program found is one circuitRF installed — the only kind brief 25 may remove.</summary>
    public bool InstalledByCircuitRf => Found?.HowFound == SolverHowFound.Installed;

    /// <summary>Asks discovery about <paramref name="tool"/>. Starts short processes (a version question,
    /// and cached capability probes); call it off the UI thread.</summary>
    public static SolverStatus Of(SolverTool tool, SolverDiscovery? discovery = null)
    {
        var d = discovery ?? SolverDiscovery.For(tool);
        var found = d.Find(out var rejected);
        var capabilities = found is not null && d.Runs(found)
            ? SolverInstaller.CapabilitiesToCheck(tool).Select(c => d.Probe(found, c)).ToList()
            : [];
        var recipe = SolverRecipes.For(tool);
        // brief-em3d-26 — Palace on Windows has no recipe HERE; it installs into the Linux subsystem.
        bool offer = (recipe is not null || SolverInstallPlan.HasRoute(tool)) && SolverInstaller.WouldHelp(d, found);
        // With no Install … to offer, the row says where to build it from instead (Gmsh on Linux arm64).
        string summary = d.DescribeForSettings(found, rejected);
        if (recipe is null && !SolverInstallPlan.HasRoute(tool) && found is not { Validated: true } && SolverRecipes.ByHand(tool) is { } byHand)
            summary += " " + byHand;
        return new SolverStatus(tool, d.Name, found, rejected, summary, capabilities, recipe, offer);
    }
}
