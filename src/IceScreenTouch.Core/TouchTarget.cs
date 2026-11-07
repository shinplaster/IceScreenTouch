using System.Text;

namespace IceScreenTouch.Core;

/// <summary>A device subscribed to the HID touch report.</summary>
/// <param name="Id">The Bluetooth device ID Windows gives the connection (stable for one adapter and device).</param>
/// <param name="Name">The device's Bluetooth name (its name in iOS settings), or null until it is known.</param>
/// <param name="Order">Larger = subscribed later.</param>
/// <param name="Model">The model number the device reports (Device Information), such as "iPad13,1", once known.</param>
public sealed record HidClient(string Id, string? Name, long Order, string? Model = null);

/// <summary>Why <see cref="TargetChoice.Client"/> gets the input.</summary>
public enum TargetReason
{
    /// <summary>No device is connected.</summary>
    None,

    /// <summary>The user chose it.</summary>
    Chosen,

    /// <summary>It is the device the mirror window shows (matched by name).</summary>
    Mirrored,

    /// <summary>Nothing else decided: the device that connected last.</summary>
    Latest,
}

/// <summary>The one device that gets touches and keys.</summary>
/// <param name="Client">The device, or null when none is connected.</param>
/// <param name="Reason">How it was chosen.</param>
public sealed record TargetChoice(HidClient? Client, TargetReason Reason)
{
    /// <summary>No device.</summary>
    public static TargetChoice None { get; } = new(null, TargetReason.None);
}

/// <summary>What the receiver tells about the device it shows.</summary>
/// <param name="DeviceName">The device's own name (UxPlay prints it when the device connects), if known.</param>
/// <param name="HostNames">
/// Host names of the devices connected to the receiver (mDNS, without ".local"), such as "example15puro" for a
/// device named "example15ぷろ": iOS spells the name in ASCII for its host name.
/// </param>
public sealed record MirrorPeer(string? DeviceName, IReadOnlyList<string> HostNames)
{
    /// <summary>Nothing known.</summary>
    public static MirrorPeer Unknown { get; } = new(null, []);
}

/// <summary>
/// Picks the one device that gets the input when several are connected over Bluetooth (they all hear every report
/// otherwise, which confuses more than it helps): the device the user chose, else the device the mirror window
/// shows, else the one that connected last.
/// </summary>
public static class TouchTargetSelector
{
    /// <summary>Picks the target among <paramref name="clients"/>.</summary>
    /// <param name="clients">The connected devices.</param>
    /// <param name="chosenId">The device the user chose (<see cref="HidClient.Id"/>), or null for automatic.</param>
    /// <param name="peer">What the receiver tells about the device it shows.</param>
    public static TargetChoice Select(IReadOnlyCollection<HidClient> clients, string? chosenId, MirrorPeer? peer)
    {
        ArgumentNullException.ThrowIfNull(clients);
        if (clients.Count == 0)
        {
            return TargetChoice.None;
        }

        if (chosenId is not null && clients.FirstOrDefault(c => c.Id == chosenId) is { } chosen)
        {
            return new TargetChoice(chosen, TargetReason.Chosen);
        }

        if (peer is not null && Mirrored(clients, peer) is { } mirrored)
        {
            return new TargetChoice(mirrored, TargetReason.Mirrored);
        }

        return new TargetChoice(Latest(clients)!, TargetReason.Latest);
    }

    /// <summary>
    /// How well a Bluetooth name matches the device name a receiver reported: 2 = the same (ignoring case and
    /// surrounding spaces), 1 = the same letters and digits, 0 = no match.
    /// </summary>
    public static int NameMatch(string? bluetoothName, string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(bluetoothName) || string.IsNullOrWhiteSpace(deviceName))
        {
            return 0;
        }

        if (string.Equals(bluetoothName.Trim(), deviceName.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        var a = Fold(bluetoothName);
        return a.Length > 0 && a == Fold(deviceName) ? 1 : 0;
    }

    /// <summary>
    /// How well a Bluetooth name matches an mDNS host name: 2 = the same ASCII letters and digits ("Alex's iPhone"
    /// and "Alexs-iPhone"), 1 = the host name starts with them ("example15ぷろ" and "example15puro", where iOS
    /// spelled the kana in ASCII), 0 = no match. Names without enough ASCII never match.
    /// </summary>
    public static int HostMatch(string? bluetoothName, string? hostName)
    {
        if (string.IsNullOrWhiteSpace(bluetoothName) || string.IsNullOrWhiteSpace(hostName))
        {
            return 0;
        }

        var host = hostName.Trim().TrimEnd('.');
        if (host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            host = host[..^".local".Length];
        }

        var b = Fold(bluetoothName);
        var h = Fold(host);
        if (b.Length < MinFoldedLength || h.Length == 0)
        {
            return 0;
        }

        return h == b ? 2 : h.StartsWith(b, StringComparison.Ordinal) ? 1 : 0;
    }

    // A prefix this short ("ip") would match too much.
    private const int MinFoldedLength = 3;

    /// <summary>The ASCII letters (lower case) and digits of <paramref name="s"/>.</summary>
    internal static string Fold(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                sb.Append(ch);
            }
            else if (ch is >= 'A' and <= 'Z')
            {
                sb.Append((char)(ch + ('a' - 'A')));
            }
        }

        return sb.ToString();
    }

    private static HidClient? Mirrored(IReadOnlyCollection<HidClient> clients, MirrorPeer peer)
    {
        // The device name is exact when the receiver gives it; host names are a guess, so they come second.
        if (peer.DeviceName is { } name && Best(clients, c => NameMatch(c.Name, name)) is { } byName)
        {
            return byName;
        }

        return Best(clients, c => peer.HostNames.Count == 0 ? 0 : peer.HostNames.Max(h => HostMatch(c.Name, h)));
    }

    // The clients with the best score above 0; the latest of them when several tie (two devices of the same name).
    private static HidClient? Best(IReadOnlyCollection<HidClient> clients, Func<HidClient, int> score)
    {
        var scored = clients.Select(c => (Client: c, Score: score(c))).Where(x => x.Score > 0).ToList();
        if (scored.Count == 0)
        {
            return null;
        }

        var top = scored.Max(x => x.Score);
        return Latest(scored.Where(x => x.Score == top).Select(x => x.Client).ToList());
    }

    private static HidClient? Latest(IReadOnlyCollection<HidClient> clients) =>
        clients.Count == 0 ? null : clients.MaxBy(c => c.Order);
}
