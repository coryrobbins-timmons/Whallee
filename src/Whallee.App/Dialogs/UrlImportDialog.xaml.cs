using System.Globalization;
using System.Windows;
using Whallee.Core.Media;

namespace Whallee.App.Dialogs;

/// <summary>
/// "Add from URL": paste a link, look up its details, choose a section, confirm.
/// </summary>
/// <remarks>
/// THIS DIALOG PREDICTS NOTHING, BY DESIGN. It carried a line estimating how much of the request
/// this network would actually supply; it was removed 2026-09-02 because knowing the ~60s ceiling
/// beforehand cannot change what the user does - asking for 5:00 and asking for 1:00 both come back
/// as about the same minute. The limit is now reported AFTER the import, by YouTubeImporter, where
/// it is a report of what happened rather than an estimate. Do not reintroduce an estimate here.
///
/// A consequence worth keeping: nothing on this window touches the network except the lookup -
/// that much is still true. It is no longer true that there is no background task here. The
/// constructor starts one: CheckToolAge, which launches yt-dlp itself (locally, not over the
/// network - ReadAsync runs a process, it does not make a request) to read its version and warn
/// when it is stale. That check fails silently by design, the same as YtDlpVersion.ReadAsync it
/// calls into, so there IS now a failure path, and it is meant to say nothing rather than surface
/// an error. Read CheckToolAge before assuming this window has nothing running once it opens.
/// </remarks>
public partial class UrlImportDialog : Window
{
    private string _ytDlpPath;   // not readonly: UpdateTool_Click re-locates after a winget upgrade
    private VideoMetadata? _metadata;

    public ImportRequest? Result { get; private set; }

    public UrlImportDialog(string ytDlpPath) : this(ytDlpPath, null) { }

    /// <param name="prefillUrl">
    /// A link to start from, used by "Edit…" so the owner need not find the video again.
    /// </param>
    public UrlImportDialog(string ytDlpPath, string? prefillUrl)
    {
        InitializeComponent();

        // Follows this file's stated convention: subscribed in code after
        // InitializeComponent, never in the markup. Apply defers itself to
        // SourceInitialized because there is no HWND yet.
        Interop.DarkTitleBar.Apply(this);
        _ytDlpPath = ytDlpPath;

        // SUBSCRIBED HERE, NOT IN XAML, AND THAT IS LOAD-BEARING. A XAML-wired TextChanged fires
        // while InitializeComponent is still walking the tree - a Text="..." attribute raises it
        // synchronously - and the elements the handler touches (ImportButton, DetailsPanel) are
        // declared later in the file, so their fields are still null at that moment. This app has
        // already been killed once exactly that way. EVERY handler on this window is subscribed
        // here, after InitializeComponent, and none in the markup.
        UrlBox.TextChanged += UrlBox_TextChanged;
        UpdateToolButton.Click += UpdateTool_Click;

        // SET AFTER THE SUBSCRIPTION, NOT AS A XAML ATTRIBUTE - the paragraph above is exactly why.
        // By this point InitializeComponent has finished and every element exists, so the
        // TextChanged this raises is safe, and wanted: it enables the Probe button the same way
        // typing would.
        if (!string.IsNullOrWhiteSpace(prefillUrl))
        {
            UrlBox.Text = prefillUrl;
            UrlBox.CaretIndex = UrlBox.Text.Length;
        }

        CheckToolAge();
    }

    /// <summary>
    /// Warns when yt-dlp is old enough to break imports.
    /// </summary>
    /// <remarks>
    /// A DELIBERATE, REASONED EXCEPTION TO THIS FILE'S NO-PREDICTION RULE. DO NOT DELETE IT AS A
    /// VIOLATION. That rule was written on 2026-09-02 when a ~60s estimate was removed, and its
    /// stated reasoning was that knowing the ceiling in advance COULD NOT CHANGE WHAT THE USER DOES.
    /// That reasoning does not reach this case: a stale yt-dlp is a local, checkable fact about the
    /// owner's machine, and knowing it beforehand changes exactly what they do - they go and update
    /// it, and the import then works. The rule stands for predictions about the network; this is
    /// not one.
    ///
    /// async void, like every other awaiting handler on this window, and wrapped in try/catch for
    /// the same reason: an exception escaping one terminates the process regardless of what raised
    /// it. Silence on failure is also the design - see YtDlpVersion.ReadAsync.
    ///
    /// Nothing here blocks the constructor. The check runs off-thread and this continuation resumes
    /// on the dispatcher, so the window opens immediately and the line appears when it is known.
    /// </remarks>
    private async void CheckToolAge()
    {
        try
        {
            var released = await YtDlpHealth.VersionAsync(_ytDlpPath);
            var warning = YtDlpVersion.DialogWarning(
                released,
                DateOnly.FromDateTime(DateTime.Now),
                _ytDlpPath,
                YtDlpLocator.DefaultWingetPackagesRoot);

            if (warning is null) return;

            StaleToolText.Text = warning;
            StaleToolPanel.Visibility = Visibility.Visible;
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// Runs the update the warning names, then re-reads the version to say what actually happened.
    /// </summary>
    /// <remarks>
    /// async void and wrapped, like every awaiting handler here. Nothing blocks: the update runs as a
    /// child process awaited off the dispatcher, so the window keeps pumping for the minute it takes.
    ///
    /// RE-LOCATES AFTERWARDS. A winget upgrade can move yt-dlp to a new folder, leaving _ytDlpPath
    /// pointing at a file that no longer exists; Locate falls through to PATH and the winget tree
    /// when the old path is gone. The import itself re-locates the same way (ImportFromUrlAsync), so
    /// only this dialog's own lookups need the new path carried here.
    /// </remarks>
    private async void UpdateTool_Click(object sender, RoutedEventArgs e)
    {
        UpdateToolButton.IsEnabled = false;
        ProbeButton.IsEnabled = false;
        StaleToolText.Text = "Updating yt-dlp… this can take a minute.";

        try
        {
            var before = await YtDlpHealth.VersionAsync(_ytDlpPath);
            var said = await YtDlpUpdater.RunAsync(_ytDlpPath, YtDlpLocator.DefaultWingetPackagesRoot);

            _ytDlpPath = await Task.Run(() => YtDlpLocator.Locate(_ytDlpPath)) ?? _ytDlpPath;
            YtDlpHealth.Invalidate();
            var after = await YtDlpHealth.VersionAsync(_ytDlpPath);

            if (after is { } now && (before is not { } then || now > then))
            {
                StaleToolText.Text = $"yt-dlp updated to {now:yyyy.MM.dd}.";
                UpdateToolButton.Visibility = Visibility.Collapsed;
                return;
            }

            // Not newer: say what the tool said, and leave the button for another try.
            StaleToolText.Text = "yt-dlp was not updated." + (said is null ? "" : $" {said}");
            UpdateToolButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            StaleToolText.Text = $"Could not update yt-dlp: {ex.Message}";
            UpdateToolButton.IsEnabled = true;
        }
        finally
        {
            ProbeButton.IsEnabled = UrlBox.Text.Trim().Length > 0;
        }
    }

    /// <summary>async void: a Click handler that awaits has no other shape, so the body is wrapped.</summary>
    private async void Probe_Click(object sender, RoutedEventArgs e)
    {
        var url = UrlBox.Text.Trim();
        if (url.Length == 0) return;

        ProbeButton.IsEnabled = false;
        try
        {
            _metadata = await YtDlpProbe.ProbeAsync(_ytDlpPath, url, CancellationToken.None);
            if (_metadata is null)
            {
                ShowError("Could not read that link. Check it is a single video, not a playlist " +
                          "or a live stream.");
                return;
            }

            ShowDetails(_metadata);
        }
        catch (Exception ex)
        {
            ShowError($"Could not look that up: {ex.Message}");
        }
        finally
        {
            ProbeButton.IsEnabled = true;
        }
    }

    private void ShowDetails(VideoMetadata meta)
    {
        TitleText.Text = meta.Title;
        DurationText.Text = $"Duration {Format(meta.Duration)}";

        // SURFACED, NOT ENFORCED - the spec's ruling, unchanged. Only the wording shrank: the value
        // and a flag, with the sentence about YouTube's Terms of Service dropped as reference text.
        LicenceText.Text = meta.IsCreativeCommons
            ? $"Licence: {meta.License} — Creative Commons"
            : $"Licence: {meta.License ?? "not reported"} — not Creative Commons";

        if (ImportPlanner.RequiresSection(meta.Duration))
        {
            SectionHeader.Text = $"Section to import (required — this video is {Format(meta.Duration)})";
            LengthBox.Text = Format(ImportPlanner.DefaultSectionLength);
        }
        else
        {
            SectionHeader.Text = "Section to import";
            LengthBox.Text = Format(meta.Duration);
        }

        DetailsPanel.Visibility = Visibility.Visible;
        ImportButton.IsEnabled = true;
        ErrorText.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Invalidates the last lookup whenever the URL text changes, so Import can never build an
    /// ImportRequest from a URL other than the one ValidateSection's duration bound came from.
    /// ImportButton is otherwise only ever enabled by a successful lookup and never re-disabled -
    /// without this, editing the URL after looking one video up and pressing Import without
    /// looking up again would validate against video A's duration and import video B.
    /// </summary>
    /// <remarks>
    /// A Dispatcher callback like any other handler on this window - wrapped in try/catch for the
    /// same reason as Probe_Click and Import_Click: an exception escaping it terminates the process
    /// regardless of what raised it.
    /// </remarks>
    private void UrlBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        try
        {
            if (_metadata is null) return;   // nothing looked up yet - nothing to invalidate

            _metadata = null;
            ImportButton.IsEnabled = false;
            DetailsPanel.Visibility = Visibility.Collapsed;
            ErrorText.Visibility = Visibility.Collapsed;
        }
        catch (Exception)
        {
            // Fail toward requiring another lookup, never toward silently allowing Import to
            // proceed against stale or indeterminate metadata.
            _metadata = null;
            ImportButton.IsEnabled = false;
        }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_metadata is null) return;

            if (!TryParseClock(StartBox.Text, out var start)) { ShowError("Start time should look like 1:30."); return; }
            if (!TryParseClock(LengthBox.Text, out var length)) { ShowError("Length should look like 5:00."); return; }

            var error = ImportPlanner.ValidateSection(start, length, _metadata.Duration);
            if (error is not null) { ShowError(error); return; }

            Result = new ImportRequest(UrlBox.Text.Trim(), start, length);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            ShowError($"Could not accept that section: {ex.Message}");
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    /// <summary>Accepts m:ss, h:mm:ss, or a bare number of seconds.</summary>
    internal static bool TryParseClock(string text, out TimeSpan value)
    {
        text = text.Trim();
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            value = TimeSpan.FromSeconds(seconds);
            return seconds >= 0;
        }

        var parts = text.Split(':');
        if (parts.Length is 2 or 3 &&
            parts.All(p => int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)))
        {
            var numbers = parts.Select(int.Parse).ToArray();
            value = numbers.Length == 2
                ? new TimeSpan(0, numbers[0], numbers[1])
                : new TimeSpan(numbers[0], numbers[1], numbers[2]);
            return true;
        }

        value = default;
        return false;
    }

    private static string Format(TimeSpan value) =>
        value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"m\:ss");
}
