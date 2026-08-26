namespace Whallee.Core.Interop;

internal static class NativeConstants
{
    public const uint WM_SPAWN_WORKER = 0x052C;
    public const int  WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
    public const int  WS_EX_LAYERED   = 0x00080000;
    public const int  WS_EX_TOOLWINDOW = 0x00000080;
    public const int  WS_CHILD        = 0x40000000;
    public const int  WS_VISIBLE      = 0x10000000;
    public const int  WS_POPUP        = unchecked((int)0x80000000);
    public const int  GWL_STYLE       = -16;
    public const int  GWL_EXSTYLE     = -20;
    public const uint SWP_NOACTIVATE  = 0x0010;
    public const uint SWP_NOSIZE      = 0x0001;
    public const uint SWP_NOMOVE      = 0x0002;
    public const uint SWP_NOZORDER    = 0x0004;
    public const uint SMTO_NORMAL     = 0x0000;
    public const uint WM_DISPLAYCHANGE = 0x007E;
    public const uint WM_QUIT          = 0x0012;
}
