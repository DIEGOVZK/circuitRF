using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Avalonia.Controls;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.ModelComposer;
using CircuitRF.Ui.Views.ModelComposer;

namespace CircuitRF.Ui.Diagnostics.Fixtures;

/// <summary>
/// The Model Composer chapter's figures, every one on the shipped <c>synthetic-gan</c> example document.
/// </summary>
/// <remarks>
/// <b>The example workspace is opened from a COPY.</b> Opening a <c>.cmc</c> reads its cache and an exclusion re-opens its data, so a
/// figure built on <c>examples/</c> in place could leave the shipped tree different from what was committed. The copy is
/// deleted when the capture is done.
///
/// <para><b>The data opens on this thread.</b> The window's background runner is replaced by one that runs inline, so the
/// capture waits for nothing and the plots are the ones a reader sees once the document has opened.</para>
///
/// <para><b>A panel is captured on its own with the window's styles moved onto it.</b> The panes, cards and lit lamps are
/// styled by <c>ModelComposerWindow</c>'s <c>Window.Styles</c>, so a panel lifted out without them draws as bare
/// controls.</para>
/// </remarks>
public static class DocModelComposerFixtures
{
    private const string Example = "Model Composer";
    private const string Folder  = "synthetic-gan";
    private const string Cmc     = "gan_4x125.cmc";

    /// <summary>The whole window on the Data step's output family.</summary>
    public static FigureScene Window()
    {
        var (vm, cleanup) = Opened("Data", ModelComposerViewModel.OutputTab);
        var window = new ModelComposerWindow { DataContext = vm };
        var content = Lift(window);
        content.DataContext = vm;
        return new FigureScene(content) { Cleanup = cleanup };
    }

    /// <summary>
    /// The Plots panel on the bias plane with the lasso lamp lit: three points already excluded for compliance, and a lasso
    /// part-drawn around the pinched-off, high-drain-voltage corner with Breakdown chosen as its reason.
    /// </summary>
    public static FigureScene BiasPlaneLasso()
    {
        var (vm, cleanup) = Opened("Data", ModelComposerViewModel.BiasPlaneTab);

        // The excluded points: the DC set's highest gate voltage at its three highest drain voltages, the corner where a
        // real instrument would have hit its current limit.
        var dc = vm.Loaded.Data["dc_25C"];
        double vgsMax = dc.Points.Max(p => p.Vgs);
        var compliance = dc.Points.Where(p => p.Vgs == vgsMax).OrderByDescending(p => p.Vds).Take(3)
                                  .Select(p => ("dc_25C", p.PointId)).ToList();
        vm.ExclusionReason = "Compliance";
        if (!vm.ToggleExclusion(compliance))
            throw new InvalidOperationException("The bias-plane figure's exclusion changed nothing: " + vm.StatusText);

        // The lasso in progress: armed, its reason chosen, and a closed freehand path round Vds 14.6-21.4 V, Vgs -5.25 to -3.55 V,
        // inside the axes so none of it is clipped.
        // It is drawn while the drag is held, so the figure is taken before it is released.
        vm.ExcludeArmed = true;
        vm.ExclusionReason = "Breakdown";
        var lasso = vm.BiasPlaneLasso;
        lasso.DragBegin(new object());
        const int n = 28;
        for (int i = 0; i <= n; i++)
        {
            double t = 2 * Math.PI * i / n;
            // A slightly uneven loop, as a hand draws one.
            double wobble = 1 + 0.05 * Math.Sin(3 * t);
            lasso.DragTo(new Complex(18 + 3.3 * wobble * Math.Cos(t), -4.4 + 0.8 * wobble * Math.Sin(t)));
        }

        var panel = Styled(new PlotsPanelView { DataContext = vm });
        return new FigureScene(panel) { Cleanup = () => { lasso.DragEnd(cancelled: true); cleanup(); } };
    }

    /// <summary>The Steps panel on the Current step: the rail, and the Id and Ig method pickers under it.</summary>
    public static FigureScene StepsPanel()
    {
        var (vm, cleanup) = Opened("Current", null);
        return new FigureScene(Styled(new StepsPanelView { DataContext = vm })) { Cleanup = cleanup };
    }

    /// <summary>The Values panel after the network fit: every value with its lock, bounds, scaling rule and ± error.</summary>
    public static FigureScene ValuesPanel()
    {
        var (vm, cleanup) = Opened("Network", null);
        if (vm.NetworkValues.Count == 0)
            throw new InvalidOperationException($"The {Folder} example's network has no values, so the Values figure would be empty.");
        return new FigureScene(Styled(new ValuesPanelView { DataContext = vm })) { Cleanup = cleanup };
    }

    /// <summary>The Model Validation Report dialog, opened on the example's stored options.</summary>
    public static FigureScene ReportDialog()
    {
        var (vm, cleanup) = Opened("Validate", null);
        var report = new ModelReportViewModel(vm);
        // A few choices ticked, so the figure shows a choice as well as what none chosen falls back to: S at three gate
        // voltages along Vds 10 V, and C–V curves at two drain voltages.
        Tick(report.SBiasItems, "-1.2, 10", "-0.4, 10", "0.3, 10");
        Tick(report.CvVdsItems, "2", "10");
        var dialog = new ModelReportDialog(report);
        var content = Lift(dialog);
        content.DataContext = report;
        // The generator sizes the lifted body to the whole figure, which would swallow the dialog's margin.
        var margin = content.Margin;
        content.Margin = default;
        return new FigureScene(new Border { Padding = margin, Child = content }) { Cleanup = cleanup };
    }

    private static void Tick(System.Collections.Generic.IReadOnlyList<PickItem> items, params string[] texts)
    {
        foreach (string t in texts)
            (items.FirstOrDefault(i => i.Text == t)
                ?? throw new InvalidOperationException($"The report dialog offers no '{t}' on the example; it offers "
                                                     + string.Join(" | ", items.Select(i => i.Text)) + "."))
                .IsChecked = true;
    }

    // ── shared ──────────────────────────────────────────────────────────────────

    /// <summary>A copy of the example, its data opened, on <paramref name="step"/> and, if given, <paramref name="tab"/>.</summary>
    private static (ModelComposerViewModel Vm, Action Cleanup) Opened(string step, string? tab)
    {
        string root = ExampleWorkspaces.ResolveRoot()
            ?? throw new InvalidOperationException($"No examples/ tree beside the generator or above it, so the {Example} figures have no document.");
        // The whole workspace, not the one folder: the document's network is a schematic in a sibling cell.
        string dir = Directory.CreateTempSubdirectory("crf-doc-mc-").FullName;
        Copy(Path.Combine(root, Example), dir);

        var vm = ModelComposerViewModel.OpenFile(Path.Combine(dir, Folder, Cmc));
        vm.RunOffThread = (work, _) => Task.FromResult(work());
        vm.BeginLoad();
        if (vm.Loaded.Data.Count == 0)
            throw new InvalidOperationException($"The {Folder} example opened no data: {vm.StatusText}");

        vm.SelectedStep = vm.Steps.FirstOrDefault(s => s.Name == step)
            ?? throw new InvalidOperationException($"The Model Composer has no step named '{step}'.");
        if (vm.SelectedStep?.Name != step)
            throw new InvalidOperationException($"The {step} step is disabled on the {Folder} example, so its figure cannot be taken.");
        if (tab is not null)
        {
            vm.SelectedPlotTab = tab;
            if (vm.SelectedPlotTab != tab)
                throw new InvalidOperationException($"The {step} step has no '{tab}' plot tab.");
        }

        return (vm, () =>
        {
            vm.Dispose();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        });
    }

    /// <summary>A window's content, lifted out with the window's styles moved onto it.</summary>
    private static Control Lift(Window window)
    {
        var content = (Control)window.Content!;
        var styles = window.Styles.ToList();
        window.Content = null;
        window.Styles.Clear();
        foreach (var style in styles) content.Styles.Add(style);
        return content;
    }

    /// <summary>A panel carrying the Model Composer window's styles, which every one of its panels is drawn with.</summary>
    private static Control Styled(Control panel)
    {
        var window = new ModelComposerWindow();
        var styles = window.Styles.ToList();
        window.Styles.Clear();
        var host = new Panel { Background = null };
        foreach (var style in styles) host.Styles.Add(style);
        host.Children.Add(panel);
        return host;
    }

    private static void Copy(string from, string to)
    {
        foreach (string d in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, d)));
        foreach (string f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(f, Path.Combine(to, Path.GetRelativePath(from, f)));
    }
}
