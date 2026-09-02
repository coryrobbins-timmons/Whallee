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
/// A consequence worth keeping: nothing on this window touches the network except the lookup, so
/// there is no background task, no second yt-dlp launch, and no failure path for either.
/// </remarks>
public partial class UrlImportDialog : Window
{
    private readonly string _ytDlpPath;
    private VideoMetadata? _metadata;

    public ImportRequest? Result { get; private set; }

    public UrlImportDialog(string ytDlpPath)
    {
        InitializeComponent();
        _ytDlpPath = ytDlpPath;

        // SUBSCRIBED HERE, NOT IN XAML, AND THAT IS LOAD-BEARING. A XAML-wired TextChanged fires
        // while InitializeComponent is still walking the tree - a Text="..." attribute raises it
        // synchronously - and the elements the handler touches (ImportButton, DetailsPanel) are
        // declared later in the file, so their fields are still null at that moment. This app has
        // already been killed once exactly that way. EVERY handler on this window is subscribed
        // here, after InitializeComponent, and none in the markup.
        UrlBox.TextChanged += UrlBox_TextChanged;
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
