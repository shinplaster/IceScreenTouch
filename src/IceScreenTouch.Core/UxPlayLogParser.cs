using System.Globalization;
using System.Text.RegularExpressions;

namespace IceScreenTouch.Core;

/// <summary>A fact extracted from one line of <c>uxplay -d</c> output.</summary>
public abstract record UxPlayLogEvent;

/// <summary>
/// The real frame size the video sink draws (printed by the patched uxplay, see
/// poc/poc1-uxplay/patches). The pixel aspect ratio is folded into the width. Follows rotation.
/// </summary>
public sealed record SinkSizeEvent(SizeD Size) : UxPlayLogEvent;

/// <summary>The picture size from the stock debug line; only a fallback for <see cref="SinkSizeEvent"/>.</summary>
public sealed record VideoSizeEvent(SizeD Size) : UxPlayLogEvent;

/// <summary>A phone asked to connect.</summary>
public sealed record DeviceConnectedEvent(string Name, string Model) : UxPlayLogEvent;

/// <summary>
/// The mirroring session ended because the phone stopped mirroring: uxplay shut down the video renderer. The picture
/// size and the phone it was showing no longer apply. A dropped connection does not reach this (uxplay's "lost
/// connection with client" path does not call video_reset); see <see cref="NoConnectionsEvent"/>.
/// </summary>
public sealed record MirroringEndedEvent : UxPlayLogEvent;

/// <summary>
/// uxplay has no client connection left (the phone stopped, or the connection was lost). The picture size and the
/// phone of the session no longer apply. After a normal stop this follows <see cref="MirroringEndedEvent"/>.
/// </summary>
public sealed record NoConnectionsEvent : UxPlayLogEvent;

/// <summary>Parses uxplay's console output, ported from poc/poc3-integrated/geometry.py.</summary>
public static partial class UxPlayLogParser
{
    [GeneratedRegex(@"iScrMirror: sink-caps (\d+)x(\d+) par (\d+)/(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex SinkRegex();

    [GeneratedRegex(
        @"raop_rtp_mirror width_source = ([\d.]+) height_source = ([\d.]+) " +
        @"x_offset = ([\d.]+) y_offset = ([\d.]+) width = ([\d.]+) height = ([\d.]+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex SizeRegex();

    [GeneratedRegex(@"connection request from (.+?) \((.+?)\) with deviceID", RegexOptions.CultureInvariant)]
    private static partial Regex DeviceRegex();

    // uxplay -d prints this when TEARDOWN stops the mirror stream. Older AirPlay versions send a video TEARDOWN
    // (110=1) first and the final TEARDOWN after it, so this can appear twice; newer ones (seen with AirPlay/1005.x)
    // send only the final TEARDOWN. The video_reset types of HLS playback are not matched. A dropped connection
    // ("lost connection with client") does not reach video_reset in uxplay; NoConnectionsRegex covers it.
    [GeneratedRegex(@"video_reset: type = RTP_(?:to_HLS_)?Shutdown", RegexOptions.CultureInvariant)]
    private static partial Regex MirroringEndedRegex();

    // uxplay -d prints the count of open AirPlay connections on every open and close. A second phone rejected while
    // one is mirroring only takes it from 2 to 1, so exactly 0 means no client is connected at all (a dropped
    // connection ends with this too). The count must end the line: "10", "0x" and so on do not match.
    [GeneratedRegex(@"\bOpen connections: 0\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex NoConnectionsRegex();

    [GeneratedRegex(@"^\s*([0-9a-fA-F]{2}\s+)*[0-9a-fA-F]{2}\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex HexDumpRegex();

    /// <summary>
    /// A line of a uxplay -d hex dump ("3f a0 12 ..."): the way it prints keys, secrets and pairing proofs.
    /// </summary>
    public static bool IsHexDump(string line) => HexDumpRegex().IsMatch(line);

    [GeneratedRegex(@"\b[0-9A-Fa-f]{2}(?::[0-9A-Fa-f]{2}){5}\b", RegexOptions.CultureInvariant)]
    private static partial Regex MacRegex();

    [GeneratedRegex(@"((?:DACP-ID|Active-Remote)\s*[:=]\s*)\S+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DacpRegex();

    /// <summary>
    /// The line for the UxPlay log file, with the phone's MAC-style IDs (deviceID, macAddress) and the DACP remote
    /// control IDs (which let the LAN control the phone's playback during the session) masked.
    /// </summary>
    public static string Mask(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return DacpRegex().Replace(MacRegex().Replace(line, "xx:xx:xx:xx:xx:xx"), "$1(masked)");
    }

    /// <summary>Returns the fact in <paramref name="line"/>, or null if the line holds nothing of interest.</summary>
    public static UxPlayLogEvent? Parse(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        var m = SinkRegex().Match(line);
        if (m.Success)
        {
            if (TryInt(m.Groups[1].Value, out var w) && TryInt(m.Groups[2].Value, out var h) &&
                TryInt(m.Groups[3].Value, out var pn) && TryInt(m.Groups[4].Value, out var pd) &&
                pn > 0 && pd > 0)
            {
                return new SinkSizeEvent(new SizeD(w * (double)pn / pd, h));
            }

            return null; // an invalid pixel aspect ratio is ignored
        }

        m = SizeRegex().Match(line);
        if (m.Success)
        {
            if (TryDouble(m.Groups[5].Value, out var w) && TryDouble(m.Groups[6].Value, out var h))
            {
                return new VideoSizeEvent(new SizeD(w, h));
            }

            return null;
        }

        m = DeviceRegex().Match(line);
        if (m.Success)
        {
            return new DeviceConnectedEvent(m.Groups[1].Value, m.Groups[2].Value);
        }

        if (MirroringEndedRegex().IsMatch(line))
        {
            return new MirroringEndedEvent();
        }

        return NoConnectionsRegex().IsMatch(line) ? new NoConnectionsEvent() : null;
    }

    private static bool TryInt(string s, out int value) =>
        int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    private static bool TryDouble(string s, out double value) =>
        double.TryParse(s, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
}
