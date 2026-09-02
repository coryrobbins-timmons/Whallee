using System.Globalization;
using System.Windows;
using Whallee.Core.Media;

namespace Whallee.App.Dialogs;

/// <summary>
/// "Add from URL": paste a link, look up its details, choose a section, confirm.
/// </summary>
/// <remarks>
/// The predicted-length line is PURE ARITHMETIC on the probe's own metadata
/// (<see cref="ImportPlanner.PredictedObtainable"/>), not a second network round trip. An earlier
/// design resolved a direct media URL in the background to read its byte length and did bitrate
/// arithmetic on that - obsoleted 2026-09-01 when a real measurement showed the network's limit is
/// a ~60s DURATION ceiling, independent of bitrate and file size (see
/// <see cref="ImportPlanner.ContentCeiling"/>). There is nothing left here to resolve, so there is
/// no background task, no second yt-dlp launch, and no failure path for either.
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
        // while InitializeComponent is still walking the tree - the Text="5:00" attribute raises it
        // synchronously - and PredictionText is declared later in the file, so its field is still
        // null at that moment. This app has already been killed once by a handler firing mid-
        // InitializeComponent before the element it touched existed.
        LengthBox.TextChanged += LengthBox_TextChanged;

        // Same reasoning as LengthBox above: subscribed here, not in XAML, so a TextChanged wired
        // during InitializeComponent's tree-walk can never fire before ImportButton and
        // DetailsPanel - which the handler below touches - exist.
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

        // SURFACED, NOT ENFORCED - the spec's ruling. State the fact and let the user decide.
        LicenceText.Text = meta.IsCreativeCommons
            ? $"Licence: {meta.License} — Creative Commons."
            : $"Licence: {meta.License ?? "not reported"}. Downloading is contrary to YouTube's " +
              "Terms of Service except for Creative Commons material.";

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

        // Setting LengthBox.Text above already raises TextChanged, which calls UpdatePrediction();
        // this call covers the case where the new text is identical to the old (no event fires).
        UpdatePrediction();
    }

    /// <summary>
    /// Recomputes the predicted-obtainable line from the current length field and metadata. Pure
    /// arithmetic - see the type-level remarks for why nothing here touches the network.
    /// </summary>
    /// <remarks>
    /// A Dispatcher callback, same as any other event handler on this window - an exception
    /// escaping it terminates the process regardless of what raised it. Subscribing in code
    /// (constructor, after InitializeComponent) already removes today's ordering hazard; this
    /// try/catch is the safety net for whatever the next edit introduces here, matching
    /// Probe_Click and Import_Click.
    /// </remarks>
    private void LengthBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        try
        {
            UpdatePrediction();
        }
        catch (Exception)
        {
            PredictionText.Text = string.Empty;   // a missing prediction must never crash the dialog
        }
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
    /// same reason as LengthBox_TextChanged, Probe_Click and Import_Click: an exception escaping
    /// it terminates the process regardless of what raised it.
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

    private void UpdatePrediction()
    {
        if (_metadata is null) { PredictionText.Text = string.Empty; return; }
        if (!TryParseClock(LengthBox.Text, out var length) || length <= TimeSpan.Zero)
        {
            PredictionText.Text = string.Empty;
            return;
        }

        var predicted = ImportPlanner.PredictedObtainable(length, _metadata.Duration);

        // Independent of bitrate and file size - true for every video, so the copy says so plainly
        // rather than implying quality or size play a role.
        PredictionText.Text =
            $"This network reliably supplies only about a minute of any video, regardless of its " +
            $"quality or file size — a longer request comes back shorter than asked. For this " +
            $"{Format(length)} request, expect roughly {Format(predicted)}. A start time other " +
            "than 0:00 is honoured only when a direct section download succeeds, which this " +
            "network usually refuses; when it does, the import falls back to a prefix from the " +
            "very beginning instead.";
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
