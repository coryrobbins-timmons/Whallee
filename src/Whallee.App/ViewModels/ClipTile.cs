using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Whallee.App.ViewModels;

public sealed class ClipTile : INotifyPropertyChanged
{
    private string? _thumbnailPath;

    public required string Path { get; init; }
    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>
    /// Null until the thumbnail has been generated. The tile is added to the grid immediately and
    /// this is filled in later, so the library appears at once and fills in as frames arrive.
    /// </summary>
    public string? ThumbnailPath
    {
        get => _thumbnailPath;
        set { _thumbnailPath = value; Raise(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
