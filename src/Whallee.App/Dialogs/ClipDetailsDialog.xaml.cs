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

    public ClipDetailsDialog(string clipPath, string? ffmpegPath)
    {
        InitializeComponent();

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

        Loaded += OnLoaded;
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
