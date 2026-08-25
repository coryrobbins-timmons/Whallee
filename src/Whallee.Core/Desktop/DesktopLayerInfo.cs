namespace Whallee.Core.Desktop;

public enum DesktopLayerMode
{
    /// <summary>WorkerW is a sibling of the window owning SHELLDLL_DefView.</summary>
    Classic,
    /// <summary>Progman has WS_EX_NOREDIRECTIONBITMAP; WorkerW is a direct child of Progman.</summary>
    RaisedDesktop
}

public sealed record DesktopLayerInfo(
    DesktopLayerMode Mode,
    nint Progman,
    nint WorkerW,
    nint ShellDefView);
