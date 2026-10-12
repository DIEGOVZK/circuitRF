using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Interactivity;
using Avalonia.Media;
using CircuitRF.Ui.Markdown;
using CircuitRF.Ui.Updates;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// The <b>Release Notes</b> window — what changed in the version that has just been installed, and,
/// from Help ▸ Release Notes…, what has been released lately.
///
/// <para>It renders a <see cref="ReleaseNotesResult"/> and does nothing else: it does not decide
/// whether to open (<see cref="ReleaseNotesGate"/>), does not fetch (<see cref="ReleaseNotesFetcher"/>)
/// and does not parse (<see cref="SmallMarkdown"/>). All three of those are testable without
/// Avalonia, which is the point of the split — <c>Ui.Tests</c> calls no Avalonia runtime API, so
/// anything decided in this file is decided where no test can reach it.</para>
/// </summary>
public partial class ReleaseNotesDialog : Window
{
    /// <summary>
    /// The per-release banner, above every section when there is more than one — larger than any
    /// heading a release body can contain (15), smaller than the window's own title (18), so the
    /// boundary between two releases never reads as a section inside one of them.
    /// </summary>
    private const double BannerFontSize = 16;

    private string _browseUrl = "";

    /// <summary>Help ▸ Check for Updates…'s own command, which the download button runs.</summary>
    private readonly ICommand? _checkForUpdates;

    /// <summary>
    /// The populate guard, for the same reason <c>UpdateSettingsView</c> has one: setting
    /// <c>IsChecked</c> raises <c>IsCheckedChanged</c>, so without it opening the dialog would write
    /// the preference it just read.
    /// </summary>
    private bool _loading;

    public ReleaseNotesDialog() : this(new ReleaseNotesResult(
        ReleaseNotesOutcome.Unavailable, AppVersion.Display, [],
        ReleaseNotesFetcher.BrowseUrl(GitHubReleasesFeed.DefaultApiUrl)))
    {
        // Parameterless for the XAML previewer only; nothing in the application uses it.
    }

    /// <param name="result">What to render.</param>
    /// <param name="offerOptOut">
    /// Whether to offer the "Always Show New Release Notes" checkbox — <b>true only for the automatic
    /// showing</b>, which is the one thing that preference governs.
    ///
    /// <para>Help ▸ Release Notes… was asked for by hand, and it opens whatever the preference says
    /// (<c>WorkspaceViewModel.ShowReleaseNotes</c>). A checkbox there would sit beneath a window the
    /// user had just opened deliberately and appear to be about it, while actually turning off a
    /// different showing they were not thinking about — and it would read as switched off to everyone
    /// who has ever turned the automatic one off, on a dialog that is plainly showing. Settings ▸
    /// Updates is where it lives for them.</para>
    /// </param>
    /// <param name="checkForUpdates">
    /// Help ▸ Check for Updates…'s command. The download button is shown only when the result names a
    /// newer release AND this command can run — it is disabled when automatic updates are off, and a
    /// button offering a download that will not happen would be worse than none.
    /// </param>
    public ReleaseNotesDialog(ReleaseNotesResult result, bool offerOptOut = true,
                              ICommand? checkForUpdates = null)
    {
        InitializeComponent();
        _checkForUpdates = checkForUpdates;
        Render(result, offerOptOut);
    }

    /// <summary>
    /// Who these notes are about, for the heading and for every failure sentence.
    ///
    /// <para>A result with <b>no version</b> is the Help menu's form — the last few releases, whatever
    /// they are — and naming one of them here would claim the window is about that one. It is not a
    /// missing value to be papered over: see <see cref="ReleaseNotesResult.Version"/>.</para>
    /// </summary>
    private static string Subject(string version)
        => version.Length > 0 ? $"{UpdateApp.Name} {version}" : $"{UpdateApp.Name} Release Notes";

    private void Render(ReleaseNotesResult result, bool offerOptOut)
    {
        _browseUrl = result.BrowseUrl;

        string subject = Subject(result.Version);
        HeadingText.Text = subject;

        if (result.NewerVersion.Length > 0 && _checkForUpdates?.CanExecute(null) == true)
        {
            DownloadButton.Content   = $"Download {UpdateApp.Name} {result.NewerVersion}";
            DownloadButton.IsVisible = true;
        }

        // Hidden rather than disabled, and the preference is not even read: on the requested form it
        // is not a control the user may not use, it is a question this window is not asking.
        AlwaysShowCheck.IsVisible = offerOptOut;

        if (offerOptOut)
        {
            _loading = true;
            try { AlwaysShowCheck.IsChecked = ReleaseNotesGate.ShowPreference; }
            finally { _loading = false; }
        }

        NotesText.Inlines?.Clear();

        switch (result.Outcome)
        {
            case ReleaseNotesOutcome.Found:
                // Banners on the version-less form even when it carried a single section: there, the
                // heading does not name a version, so without one nothing on screen says which
                // release is being read.
                AppendSections(result.Sections, banners: result.Sections.Count > 1
                                                         || result.Version.Length == 0);
                break;

            case ReleaseNotesOutcome.NotPublished:
                BrowseButton.IsVisible = true;
                AppendFailure(
                    result.Version.Length > 0
                        ? $"No release notes have been published for {subject}."
                        : $"No {UpdateApp.Name} release notes have been published yet.",
                    result.BrowseUrl);
                break;

            case ReleaseNotesOutcome.Blocked:
                // Nothing was tried, so it does not say the repository could not be reached — on an
                // administered machine that would send the user to diagnose a working network.
                BrowseButton.IsVisible = true;
                AppendFailure(
                    $"{UpdateApp.Name} on this machine is configured not to contact the update host, "
                    + "so the release notes were not downloaded.",
                    result.BrowseUrl);
                break;

            default:
                BrowseButton.IsVisible = true;
                AppendFailure(
                    result.Version.Length > 0
                        ? $"The release notes for {subject} could not be downloaded. "
                          + "The repository may be unreachable from this network."
                        : $"The {UpdateApp.Name} release notes could not be downloaded. "
                          + "The repository may be unreachable from this network.",
                    result.BrowseUrl);
                break;
        }
    }

    /// <summary>
    /// Renders every release the user has not been shown, newest at the top, into the one selectable
    /// block — which is why they are appended to a single <c>Inlines</c> collection rather than
    /// stacked as one control per release: a selection cannot cross two controls, and the owner's
    /// requirement is that the whole thing drags and copies in one go.
    ///
    /// <para><b>The version banner is the caller's decision</b> (<see cref="Render"/>). With a single
    /// release under a heading that already names it, a banner would restate that heading immediately
    /// beneath itself; with several it is the only thing separating one release's notes from the next,
    /// since a body's own headings render at the same weight whichever release they came from — and on
    /// the Help menu's form the heading names no version at all, so it is needed even for one.</para>
    /// </summary>
    private void AppendSections(IReadOnlyList<ReleaseNoteSection> sections, bool banners)
    {
        InlineCollection? inlines = NotesText.Inlines;
        if (inlines is null) return;

        for (int i = 0; i < sections.Count; i++)
        {
            if (i > 0)
            {
                // Two breaks, not one: a blank row between releases, matching the gap the parser
                // already puts between paragraphs.
                inlines.Add(new LineBreak());
                inlines.Add(new LineBreak());
            }

            if (banners)
            {
                inlines.Add(new Run($"{UpdateApp.Name} {sections[i].Version}")
                {
                    FontSize   = BannerFontSize,
                    FontWeight = FontWeight.Bold,
                });
                inlines.Add(new LineBreak());
                inlines.Add(new LineBreak());
            }

            MarkdownInlines.Append(inlines, SmallMarkdown.Parse(sections[i].Markdown),
                                   MarkdownInlines.ReleaseNotesBodyFontSize);
        }
    }

    /// <summary>
    /// The two failure forms. The URL is written into the selectable block as well as being behind the
    /// button, because a user on a machine with no browser association still has to be able to read
    /// and copy it — which is the whole reason this text is selectable.
    /// </summary>
    private void AppendFailure(string message, string url)
    {
        InlineCollection? inlines = NotesText.Inlines;
        if (inlines is null) return;

        inlines.Add(new Run(message));
        inlines.Add(new LineBreak());
        inlines.Add(new LineBreak());
        inlines.Add(new Run("They can be read at:"));
        inlines.Add(new LineBreak());
        inlines.Add(new Run(url) { FontWeight = FontWeight.Bold });
    }

    private void OnAlwaysShowChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;
        ReleaseNotesGate.SetShowPreference(AlwaysShowCheck.IsChecked == true);
    }

    /// <summary>
    /// Opens the releases page in the default browser. Mirrors <c>DocLauncher</c>'s cross-platform
    /// open, and swallows everything for the same reason it does: a dialog offering a link must not be
    /// able to take the application down with it.
    /// </summary>
    private void OnBrowseClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                Process.Start(new ProcessStartInfo("open", _browseUrl) { UseShellExecute = false });
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Process.Start(new ProcessStartInfo(_browseUrl) { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo("xdg-open", _browseUrl) { UseShellExecute = false });
        }
        catch (Exception) { /* no browser, no association — the URL is on screen either way */ }
    }

    /// <summary>
    /// Starts the download through Check for Updates…, whose progress row in the Messages panel
    /// carries the Cancel. The button stays on screen, disabled, saying where the download went.
    /// </summary>
    private void OnDownloadClicked(object? sender, RoutedEventArgs e)
    {
        if (_checkForUpdates?.CanExecute(null) != true) return;
        _checkForUpdates.Execute(null);
        DownloadButton.IsEnabled = false;
        DownloadButton.Content   = "Downloading — see Messages";
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();
}
