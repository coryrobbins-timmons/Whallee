using System.IO;
using System.Windows;
using Whallee.Core.Media;

namespace Whallee.App.Dialogs;

/// <summary>
/// "Details": what this clip is. Opens immediately; the probed fields fill in when ffprobe returns.
/// </summary>
/// <remarks>
/// THE PROBE IS NOT AWAITED BEFORE THE WINDOW SHOWS, and that is the whole design. ffprobe on a
/// OneDrive-backed clip is exactly the call that stalls, and this window is opened from the thread
/// that owns host windows parented into Explorer's desktop - see the pump-thread rule. Probing
/// first would put that stall between the click and anything appearing on screen.
///
/// Every handler on this window is subscribed here, after InitializeComponent, and none in the
/// markup - matching UrlImportDialog.
/// </remarks>
public partial class ClipDetailsDialog : Window
{
    private const string Unknown = "—";

    private readonly string _clipPath;
    private readonly string? _ffmpegPath;

    /// <summary>
    /// Called when the owner adds a link to a clip that had none. Null hides the offer entirely.
    /// </summary>
    private readonly Action<Whallee.Core.Configuration.ClipSource>? _onLinkAdded;

    public ClipDetailsDialog(string clipPath, string? ffmpegPath)
        : this(clipPath, ffmpegPath, source: null, onLinkAdded: null) { }

    public ClipDetailsDialog(
        string clipPath, string? ffmpegPath,
        Whallee.Core.Configuration.ClipSource? source,
        Action<Whallee.Core.Configuration.ClipSource>? onLinkAdded)
    {
        _onLinkAdded = onLinkAdded;

        InitializeComponent();

        // Follows this file's stated convention: subscribed in code after
        // InitializeComponent, never in the markup. Apply defers itself to
        // SourceInitialized because there is no HWND yet.
        Interop.DarkTitleBar.Apply(this);

        _clipPath = clipPath;
        _ffmpegPath = ffmpegPath;

        NameText.Text = Path.GetFileName(clipPath);
        PathText.Text = clipPath;

        // WHERE THE EXTENSION LIVES NOW. The library tiles stopped showing it on 2026-09-03 - it was
        // noise repeated once per clip - so this row is what answers "what kind of file is this?".
        // Uppercased and without the dot, because here it reads as a format rather than a suffix.
        var extension = Path.GetExtension(clipPath).TrimStart('.');
        FormatText.Text = extension.Length == 0 ? Unknown : extension.ToUpperInvariant();

        // ACCEPTED, and small: FileInfo.Length reads the directory entry, which is present even for
        // a OneDrive placeholder, so it does not trigger hydration. The heavy call - ffprobe, which
        // reads actual bytes - is the one pushed off this thread below.
        SizeText.Text = ReadSize(clipPath);

        // A CLIP WITH NO RECORDED SOURCE IS NORMAL, not an error: everything imported before
        // 2026-09-11, and everything added from a local file, has no link to show. Say so plainly
        // rather than leaving the row blank, which reads as something failing to load - and offer
        // to fill it in, which is what makes "Edit…" reachable for those clips at all.
        ShowRecorded(source);

        AddLinkButton.Click += (_, _) => BeginEditingLink();
        SaveLinkButton.Click += (_, _) => SaveLink();

        Loaded += OnLoaded;
    }

    /// <summary>Shows the recorded link, or the offer to add one.</summary>
    private void ShowRecorded(Whallee.Core.Configuration.ClipSource? source)
    {
        SourceError.Visibility = Visibility.Collapsed;
        SaveLinkButton.Visibility = Visibility.Collapsed;

        SourceText.IsReadOnly = true;
        SourceText.BorderThickness = new Thickness(0);
        SourceText.Background = System.Windows.Media.Brushes.Transparent;
        SourceText.Padding = new Thickness(0);

        if (source is null)
        {
            SourceText.Text = "Not recorded";
            AddLinkButton.Visibility = _onLinkAdded is null ? Visibility.Collapsed : Visibility.Visible;
            return;
        }

        SourceText.Text = source.Start > TimeSpan.Zero
            ? $"{source.Url}  (from {Clock(source.Start)})"
            : source.Url;

        AddLinkButton.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Turns the Source row into an input.
    /// </summary>
    /// <remarks>
    /// The local values set in <see cref="ShowRecorded"/> are CLEARED rather than overwritten, so
    /// the box falls back to the themed TextBox style and looks like every other field in the app.
    /// Setting a border and background by hand here would drift from the theme the first time it
    /// changed.
    /// </remarks>
    private void BeginEditingLink()
    {
        SourceText.Text = string.Empty;
        SourceText.IsReadOnly = false;
        SourceText.ClearValue(BorderThicknessProperty);
        SourceText.ClearValue(BackgroundProperty);
        SourceText.ClearValue(PaddingProperty);

        AddLinkButton.Visibility = Visibility.Collapsed;
        SaveLinkButton.Visibility = Visibility.Visible;

        SourceText.Focus();
    }

    /// <summary>
    /// Records what was typed, if it looks like a link.
    /// </summary>
    /// <remarks>
    /// VALIDATED, NOT VERIFIED. This checks the shape of the text; it does not ask yt-dlp whether
    /// the video exists, which would mean a network round trip inside a details dialog. A link that
    /// is well-formed but wrong fails later, in "Edit…", where there is already a place to report
    /// it - and where the owner can simply correct it.
    /// </remarks>
    private void SaveLink()
    {
        var url = SourceText.Text.Trim();

        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            SourceError.Text = "That does not look like a link. Paste the video's full address.";
            SourceError.Visibility = Visibility.Visible;
            return;
        }

        // Start and length are unknown for a link added after the fact - nothing recorded what was
        // originally asked for. Zero is honest: "Edit…" prefills the link and leaves the section
        // fields for the owner to fill in, which is the whole point of pressing it.
        var source = new Whallee.Core.Configuration.ClipSource(
            url, TimeSpan.Zero, TimeSpan.Zero, DateTimeOffset.Now);

        try
        {
            _onLinkAdded?.Invoke(source);
        }
        catch (Exception ex)
        {
            SourceError.Text = $"Could not save that link. {ex.Message}";
            SourceError.Visibility = Visibility.Visible;
            return;
        }

        ShowRecorded(source);
    }

    /// <summary>"1:30" for a start offset. Local to this dialog; nothing else formats one.</summary>
    private static string Clock(TimeSpan value)
    {
        var seconds = (int)Math.Round(value.TotalSeconds);
        return value.TotalHours >= 1
            ? $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}"
            : $"{seconds / 60}:{seconds % 60:00}";
    }

    /// <summary>
    /// async void, because that is what a Loaded handler must be - which is exactly why the body is
    /// wrapped. An exception escaping one reaches the Dispatcher unhandled and terminates the
    /// process, and this launches a child process against a cloud-backed file.
    /// </summary>
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrEmpty(_ffmpegPath))
            {
                ShowProbed(MediaInfo.None);
                return;
            }

            // Task.Run wraps an already-async method deliberately: ProbeAsync has a synchronous
            // prologue (two File.Exists calls) that would otherwise run on this thread, and on
            // OneDrive-backed files those are precisely the calls that stall.
            var info = await Task.Run(
                () => MediaProbe.ProbeAsync(_ffmpegPath, _clipPath, CancellationToken.None));

            ShowProbed(info);
        }
        catch (Exception)
        {
            // There is nothing useful to report here: a details window that cannot probe simply
            // says it does not know. It must not take the app down trying.
            ShowProbed(MediaInfo.None);
        }
    }

    private void ShowProbed(MediaInfo info)
    {
        DurationText.Text = info.HasDuration ? Format(info.Duration) : Unknown;
        ResolutionText.Text = info.HasResolution ? $"{info.Width} × {info.Height}" : Unknown;
    }

    /// <summary>h:mm:ss past an hour, m:ss below it.</summary>
    private static string Format(TimeSpan duration) =>
        duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes}:{duration.Seconds:00}";

    private static string ReadSize(string clipPath)
    {
        try
        {
            var bytes = new FileInfo(clipPath).Length;
            return bytes >= 1024L * 1024 * 1024
                ? $"{bytes / 1024d / 1024 / 1024:F2} GiB"
                : $"{bytes / 1024d / 1024:F1} MiB";
        }
        catch (Exception)
        {
            return Unknown;
        }
    }
}
