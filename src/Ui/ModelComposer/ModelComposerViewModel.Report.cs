// The Model Validation Report from the window (brief-modelcomposer-15 R-mc15-7a). The options are the document's
// (Steps.Report, through ModelValidationReport.WithOptions — one undo step); Generate is MC-11's function on the file, off the
// UI thread, followed by the folder and its Data Display document — the calls `model report` makes, in the same order —
// and Export PDF draws that document through ModelReportDisplayPreset, so both PDFs are the same bytes.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircuitRF.Design.Modeling;
using CircuitRF.Render.DataDisplay;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ModelComposer;

public sealed partial class ModelComposerViewModel
{
    /// <summary>Shows the report dialog, modally — the window's one call to it.</summary>
    public Func<ModelReportViewModel, Task>? ShowReportDialog { get; set; }

    /// <summary>Asks where a PDF goes, given its suggested name; null when cancelled.</summary>
    public Func<string, Task<string?>>? PickPdfPath { get; set; }

    /// <summary>The last generated report's Data Display document, or null.</summary>
    public string? ReportDocument { get; private set; }

    /// <summary>The last generated report, or null.</summary>
    public ModelReport? Report { get; private set; }

    [RelayCommand]
    private async Task OpenReport()
    {
        if (!IsActive) return;
        var dialog = new ModelReportViewModel(this);
        if (ShowReportDialog is { } show) await show(dialog);
    }

    /// <summary>Writes <paramref name="options"/> to <c>Steps.Report</c>; false when the document refuses them.</summary>
    public bool SetReportOptions(ReportOptions options) =>
        ModelValidationReport.Options(Document).ToJson().ToJsonString() == options.ToJson().ToJsonString()
        || EditPart("Report options", ModelValidationReport.Options, ModelValidationReport.WithOptions, options);

    /// <summary>
    /// Generates the report of the document's current state (MC-11 R-mc11-6) off the UI thread and writes it into its default
    /// folder, with the report's Data Display document beside it. A cancelled report writes nothing.
    /// </summary>
    public async Task<bool> GenerateReportAsync()
    {
        if (IsRunning && !_isRefresh) { Warn($"{TaskStage} is running; cancel it or wait for it to finish."); return false; }
        if (!await EnsureWritten()) return false;
        string path = DocumentPath!;
        return await StartTask("Generating the report", refresh: false, writesFile: false, rc =>
        {
            var report = ModelValidationReport.Generate(path, null, null, rc);
            rc.ThrowIfCancellationRequested();
            string folder = ModelValidationReport.DefaultFolder(path);
            ModelValidationReport.Write(report, folder);
            return (report, ModelReportDisplayPreset.WriteDocument(report, folder));
        }, r =>
        {
            var (report, cdd) = ((ModelReport, string))r!;
            Report = report;
            ReportDocument = cdd;
            var scores = report.Card.Scores;
            string counts = string.Join(", ", Enum.GetValues<Grade>().Select(g => (g, n: scores.Count(r => r.Grade == g)))
                                                  .Where(x => x.n > 0).Select(x => $"{x.n} {x.g}"));
            Info($"Report: overall {report.Card.Overall}" + (counts.Length > 0 ? $" ({counts})" : "") +
                 $" · {ModelReportDisplayPreset.Tabs(report).Count} section(s) · {Path.GetFileName(cdd)}.");
        });
    }

    /// <summary>Export PDF: every tab of the report's display, one page each, to a file the user picks.</summary>
    public async Task<bool> ExportReportPdfAsync(string? pdfPath = null)
    {
        if (ReportDocument is not { } cdd) { Warn("Generate the report first."); return false; }
        pdfPath ??= PickPdfPath is { } pick ? await pick(Path.GetFileNameWithoutExtension(cdd)) : null;
        if (pdfPath is null) return false;
        try
        {
            ModelReportDisplayPreset.WritePdf(cdd, pdfPath);
            Info($"Wrote {Path.GetFileName(pdfPath)}.");
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Error(ex.Message);
            return false;
        }
    }
}
