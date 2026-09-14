using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Whallee.App.ViewModels;

public sealed class ClipTile : INotifyPropertyChanged
{
    private string? _thumbnailPath;

    public required string Path { get; init; }

    /// <summary>
    /// The clip's real file name, extension included.
    /// </summary>
    /// <remarks>
    /// NOT FOR DISPLAY - use <see cref="DisplayName"/> for that. This value is load-bearing: it is
    /// what ClipRemoval and ClipRename match pins against, and what RenameClipDialog splits into a
    /// base name and an extension. Dropping the extension here would silently break pin matching.
    /// </remarks>
    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>The name shown on the tile: the file name without its extension.</summary>
    /// <remarks>
    /// Added 2026-09-03 on the owner's request. Every clip in the library is a video and the grid
    /// is already visually obvious about that, so ".mp4" on every tile was noise repeated once per
    /// clip. The extension is still available where it is actually useful - the Details dialog
    /// names it explicitly, and shows the full path.
    /// </remarks>
    public string DisplayName => System.IO.Path.GetFileNameWithoutExtension(Path);

    /// <summary>
    /// Null until the thumbnail has been generated. The tile is added to the grid immediately and
    /// this is filled in later, so the library appears at once and fills in as frames arrive.
    /// </summary>
    public string? ThumbnailPath
    {
        get => _thumbnailPath;
        set { _thumbnailPath = value; Raise(); }
    }

    /// <summary>The genre this clip is filed under, or null. Read by the library's grouping.</summary>
    public string? Genre { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
