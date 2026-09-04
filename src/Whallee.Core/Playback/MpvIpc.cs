using System.IO.Pipes;
using System.Text;

namespace Whallee.Core.Playback;

/// <summary>
/// Sends commands to a running mpv over the pipe it was launched with.
/// </summary>
/// <remarks>
/// VERIFIED BEFORE ANY OF THIS WAS BUILT. Every player has always been launched with
/// --input-ipc-server (see MpvOptions), but nothing had ever written to one, so the mechanism was
/// proved on 2026-09-04 against a STANDALONE headless mpv - "--idle --no-video --no-audio", so no
/// window and no contact with the wallpaper players:
///
///   sent   {"command":["set_property","pause",true]}
///   reply  {"request_id":0,"error":"success"}
///   sent   {"command":["get_property","pause"]}
///   reply  {"data":true,"request_id":0,"error":"success"}
///
/// The second exchange is the one that matters: it confirms the pause TOOK EFFECT, rather than
/// merely that the command was accepted.
/// </remarks>
public static class MpvIpc
{
    /// <summary>How long to wait for a player's pipe before giving up.</summary>
    /// <remarks>
    /// SHORT ON PURPOSE. A wedged or dying mpv must not hold up the caller, and the cost of giving
    /// up is only that the wallpaper keeps playing - which is what it was doing anyway.
    /// </remarks>
    private const int ConnectTimeoutMs = 500;

    /// <summary>The JSON line that pauses or resumes a player.</summary>
    /// <remarks>
    /// THE BOOLEAN IS WRITTEN OUT BY HAND rather than interpolated. Interpolating a bool yields
    /// .NET's "True"/"False", which is not valid JSON; mpv rejects it, and it fails SILENTLY
    /// because the pipe write itself still succeeds.
    /// </remarks>
    public static string PauseCommand(bool paused) =>
        "{\"command\":[\"set_property\",\"pause\"," + (paused ? "true" : "false") + "]}";

    /// <summary>
    /// Writes one command line to a player's pipe. Returns false when the player could not be
    /// reached.
    /// </summary>
    /// <remarks>
    /// NEVER THROWS. Every caller is a best-effort background operation whose failure mode is "the
    /// wallpaper carries on". A player that has exited, or whose pipe is not up yet, is a normal
    /// thing to meet during a restart - not an error worth propagating.
    ///
    /// BLOCKS - CALL IT FROM A BACKGROUND THREAD. Connecting to a named pipe is I/O, and the UI
    /// thread here owns windows re-parented into Explorer's desktop.
    ///
    /// mpv reads one JSON object per line, so exactly one newline is appended.
    /// </remarks>
    public static bool Send(string pipeName, string command)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
            pipe.Connect(ConnectTimeoutMs);

            var bytes = Encoding.UTF8.GetBytes(command + "\n");
            pipe.Write(bytes, 0, bytes.Length);
            pipe.Flush();

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
