using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace Whallee.App.Converters;

/// <summary>
/// Turns a thumbnail file path into an image, or nothing at all when there is not one yet.
/// </summary>
/// <remarks>
/// Binding Image.Source straight to a string does mostly work through WPF's built-in converter, but
/// two things make it worth doing explicitly:
///
/// CACHEOPTION.ONLOAD. Without it WPF keeps the jpg open behind a lazy stream, holding a file lock
/// on every thumbnail in the cache for as long as the app runs. Thumbnails are content-addressed by
/// path-plus-mtime so they are rarely overwritten in place, but a locked cache directory is a
/// nuisance the moment anything tries to clean it.
///
/// A NULL OR MISSING PATH RENDERS AS AN EMPTY TILE rather than a binding error. Tiles are added to
/// the grid before their thumbnails exist - that is the whole point of the lazy fill - so null is
/// the normal state for a second or two, not an error case.
/// </remarks>
public sealed class PathToImageConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            if (!File.Exists(path)) return null;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();   // shared across threads and cheaper for WPF to render
            return image;
        }
        catch (Exception)
        {
            // A corrupt or half-written jpg costs one blank tile, not an exception on the UI thread.
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
