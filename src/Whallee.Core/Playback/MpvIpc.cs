using System.Globalization;
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
    ///
    /// PUBLIC, DELIBERATELY: this number feeds two other budgets that read it back. It used to be
    /// exercised only by PlayerSync.Timeout being BELOW it, on purpose, so that abandoning a wait
    /// could cancel a seek before it landed after the reveal - that invariant no longer applies, and
    /// PlayerSync.Timeout (1000 ms) is now comfortably ABOVE this value, not below it, because the
    /// step it bounds pauses rather than merely seeks (see PlayerSync.Timeout's remarks for why that
    /// changes everything). What PlayerSyncTests actually asserts today is a different relationship:
    /// PlayerSync.Timeout exceeds three rounds of ReadTimeoutMs, and PlayerSync.BurstAllowance is
    /// derived from three rounds each of ReadTimeoutMs and this value - so a future change to either
    /// constant here still cannot silently break either budget.
    /// </remarks>
    public const int ConnectTimeoutMs = 500;

    /// <summary>How long to wait for mpv's reply once a query has been written.</summary>
    /// <remarks>
    /// Separate from the connect timeout because they fail for different reasons: a connect fails
    /// when the player is gone, a read fails when the player is alive but not answering. Both end in
    /// the same place - null, and the caller carries on.
    /// </remarks>
    /// <remarks>
    /// MEASURED, NOT GUESSED. A headless mpv answered a time-pos query in 31 ms on 2026-09-21
    /// (connect 8 ms), so 120 ms is roughly four times the observed cost and still leaves room for
    /// several queries inside PlayerSync.Timeout's 1000 ms Phase-1 budget.
    ///
    /// THIS VALUE MUST STAY WELL UNDER PlayerSync.Timeout. It was first set to 400 ms, above the
    /// 300 ms cap PlayerSync.Timeout used to be, and a single unanswered query then consumed the
    /// whole alignment budget - every sync timed out, the players stopped being aligned at all, and
    /// the log reported no measurements. PlayerSync.Timeout is now 1000 ms, so the live guard in
    /// PlayerSyncTests is `PlayerSync.Timeout > ReadTimeoutMs * 3` - Timeout exceeding three rounds
    /// of this value, comfortably above the single round that caused the 2026-09-21 regression. This
    /// value also feeds PlayerSync.BurstAllowance, the separate budget for the pause/seek/resume/
    /// verify burst that runs after Phase 1 succeeds.
    /// </remarks>
    public const int ReadTimeoutMs = 120;

    /// <summary>
    /// Asks a player where it is in its clip, in seconds. Null when the answer could not be had.
    /// </summary>
    /// <remarks>
    /// DIAGNOSTIC, added 2026-09-21. Until now this class could only WRITE: it sent bytes and hoped,
    /// and nothing in Whallee could confirm that any IPC command took effect. That blind spot is
    /// exactly what stalled a real investigation - the owner reported three displays out of step, the
    /// log proved all three seeks were accepted, and there was no way to see whether the players
    /// actually moved.
    ///
    /// The request carries a request_id so the reply can be told apart from mpv's unsolicited event
    /// lines, which arrive on the same pipe and would otherwise be read as the answer.
    /// </remarks>
    public const string TimePosRequest =
        "{\"command\":[\"get_property\",\"time-pos\"],\"request_id\":7}";

    /// <summary>Marks the reply that answers <see cref="TimePosRequest"/>.</summary>
    public const string TimePosRequestId = "\"request_id\":7";

    /// <summary>The JSON line that pauses or resumes a player.</summary>
    /// <remarks>
    /// THE BOOLEAN IS WRITTEN OUT BY HAND rather than interpolated. Interpolating a bool yields
    /// .NET's "True"/"False", which is not valid JSON; mpv rejects it, and it fails SILENTLY
    /// because the pipe write itself still succeeds.
    /// </remarks>
    public static string PauseCommand(bool paused) =>
        "{\"command\":[\"set_property\",\"pause\"," + (paused ? "true" : "false") + "]}";

    /// <summary>The JSON line that rewinds a player to the beginning of its clip.</summary>
    /// <remarks>
    /// A CONSTANT, AND DELIBERATELY NOT A METHOD TAKING A POSITION. The only caller ever seeks to
    /// zero, and a parameter would invite formatting a number into JSON - which is exactly the trap
    /// PauseCommand's remarks above record. A .NET bool interpolated there yielded "True"/"False",
    /// mpv rejected it, and IT FAILED SILENTLY because the pipe write itself still succeeded. A
    /// double carries the same hazard in another form: on a machine with a comma decimal separator
    /// 0.5 would be written "0,5" and rejected just as quietly. A constant cannot have that bug.
    ///
    /// If a future caller genuinely needs an arbitrary position, add the parameter then - with
    /// CultureInfo.InvariantCulture, and a test that fails without it.
    /// </remarks>
    public const string SeekToStartCommand = "{\"command\":[\"seek\",0,\"absolute\"]}";

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

    /// <summary>
    /// Pulls the seconds out of mpv's reply to <see cref="TimePosRequest"/>. Null when the line is
    /// not that reply, or carries no usable number.
    /// </summary>
    /// <remarks>
    /// PARSED BY HAND rather than with a JSON reader, to match how the commands are built in this
    /// file and to keep Whallee.Core free of a dependency for one number.
    ///
    /// InvariantCulture IS LOAD-BEARING. mpv always emits a '.' decimal separator; parsing with the
    /// current culture on a machine set to a comma separator would read "12.5" as unparseable and
    /// this would silently report "no answer" on every query. That is the same class of bug the
    /// SeekToStartCommand comment above exists to prevent, in the opposite direction.
    /// </remarks>
    public static double? ParseTimePos(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return null;

        const string key = "\"data\":";
        var at = reply.IndexOf(key, StringComparison.Ordinal);
        if (at < 0) return null;

        var from = at + key.Length;
        var to = from;
        while (to < reply.Length && reply[to] != ',' && reply[to] != '}') to++;

        var value = reply[from..to].Trim();
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? seconds
            : null;   // "null" when the player has no position yet - not an error, just no answer
    }

    /// <summary>
    /// Asks one player for its playback position. Null when it could not be reached or did not
    /// answer in time. Never throws.
    /// </summary>
    /// <remarks>
    /// BLOCKS - CALL IT FROM A BACKGROUND THREAD, for the same reason <see cref="Send"/> says so,
    /// and more so: this one waits for a reply as well as for a connect.
    ///
    /// Reads LINES UNTIL THE ONE THAT ANSWERS, because mpv pushes unsolicited event objects down the
    /// same pipe. Taking the first line that arrives would read an event as the answer and report a
    /// position that was never asked for.
    /// </remarks>
    public static async Task<double?> QueryTimePosAsync(string pipeName, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ReadTimeoutMs);
            var token = timeout.Token;

            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
            await pipe.ConnectAsync(ConnectTimeoutMs, token).ConfigureAwait(false);

            var request = Encoding.UTF8.GetBytes(TimePosRequest + "\n");
            await pipe.WriteAsync(request, token).ConfigureAwait(false);
            await pipe.FlushAsync(token).ConfigureAwait(false);

            var line = new StringBuilder();
            var one = new byte[1];

            // Bounded twice over: by the linked token's deadline, and by a ceiling on how much we
            // will read before giving up. A player streaming events faster than we can match them
            // must not keep this loop alive indefinitely.
            for (var read = 0; read < 64 * 1024; read++)
            {
                if (await pipe.ReadAsync(one.AsMemory(0, 1), token).ConfigureAwait(false) == 0)
                    return null;   // pipe closed mid-answer

                if (one[0] != (byte)'\n')
                {
                    line.Append((char)one[0]);
                    continue;
                }

                var candidate = line.ToString();
                line.Clear();

                if (candidate.Contains(TimePosRequestId, StringComparison.Ordinal))
                    return ParseTimePos(candidate);
            }

            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
