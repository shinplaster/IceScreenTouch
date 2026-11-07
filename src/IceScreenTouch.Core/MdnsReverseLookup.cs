using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace IceScreenTouch.Core;

/// <summary>
/// Asks a device on the local network for its host name with a reverse (PTR) mDNS query sent straight to it
/// (unicast to port 5353 from another port: a "legacy" query, answered by unicast). An iPhone answers this with
/// "example15puro.local" (verified on a real device); multicast queries and Windows' own name resolution got nothing.
/// </summary>
public static class MdnsReverseLookup
{
    private const int MdnsPort = 5353;
    private const ushort TypePtr = 12;
    private const ushort ClassIn = 1;

    /// <summary>The host name of <paramref name="address"/> (such as "example15puro.local"), or null without an answer.</summary>
    public static async Task<string?> QueryAsync(IPAddress address, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var name = ReverseName(address);
        var query = BuildQuery(name);
        using var udp = new UdpClient(address.AddressFamily);
        var target = new IPEndPoint(address, MdnsPort);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            // Twice: one datagram may get lost on Wi-Fi.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                await udp.SendAsync(query, target, deadline.Token).ConfigureAwait(false);
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                wait.CancelAfter(timeout / 2);
                try
                {
                    while (true)
                    {
                        var reply = await udp.ReceiveAsync(wait.Token).ConfigureAwait(false);
                        if (reply.RemoteEndPoint.Address.Equals(address) && ParsePtrAnswer(reply.Buffer, name) is { } host)
                        {
                            return host;
                        }
                    }
                }
                catch (OperationCanceledException) when (!deadline.IsCancellationRequested)
                {
                    // Try again.
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Timed out.
        }
        catch (SocketException)
        {
            // Unreachable, or the port is closed (ICMP).
        }

        return null;
    }

    /// <summary>The reverse lookup name: "178.1.168.192.in-addr.arpa" or the nibbles of an IPv6 address + "ip6.arpa".</summary>
    public static string ReverseName(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return string.Join('.', bytes.Reverse().Select(b => b.ToString(CultureInfo.InvariantCulture))) + ".in-addr.arpa";
        }

        var sb = new StringBuilder(72);
        for (var i = bytes.Length - 1; i >= 0; i--)
        {
            sb.Append(CultureInfo.InvariantCulture, $"{bytes[i] & 0xF:x}.{bytes[i] >> 4:x}.");
        }

        return sb.Append("ip6.arpa").ToString();
    }

    /// <summary>A query for the PTR record of <paramref name="name"/> (ID 0, one question, class IN).</summary>
    internal static byte[] BuildQuery(string name)
    {
        var bytes = new List<byte>(12 + name.Length + 6) { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (var label in name.TrimEnd('.').Split('.'))
        {
            var l = Encoding.UTF8.GetBytes(label);
            bytes.Add((byte)l.Length);
            bytes.AddRange(l);
        }

        bytes.AddRange([0, 0, (byte)TypePtr, 0, (byte)ClassIn]);
        return [.. bytes];
    }

    /// <summary>The target of the first PTR record for <paramref name="name"/> in a reply, without the final dot.</summary>
    internal static string? ParsePtrAnswer(ReadOnlySpan<byte> reply, string name)
    {
        try
        {
            if (reply.Length < 12 || (reply[2] & 0x80) == 0)
            {
                return null; // not a response
            }

            int questions = BinaryPrimitives.ReadUInt16BigEndian(reply[4..]);
            var records = BinaryPrimitives.ReadUInt16BigEndian(reply[6..]) + BinaryPrimitives.ReadUInt16BigEndian(reply[8..]) + BinaryPrimitives.ReadUInt16BigEndian(reply[10..]);
            var offset = 12;
            for (var i = 0; i < questions; i++)
            {
                (_, offset) = ReadName(reply, offset);
                offset += 4;
            }

            for (var i = 0; i < records; i++)
            {
                (var owner, offset) = ReadName(reply, offset);
                var type = BinaryPrimitives.ReadUInt16BigEndian(reply[offset..]);
                var length = BinaryPrimitives.ReadUInt16BigEndian(reply[(offset + 8)..]);
                offset += 10;
                if (type == TypePtr && string.Equals(owner, name.TrimEnd('.'), StringComparison.OrdinalIgnoreCase))
                {
                    var (target, _) = ReadName(reply, offset);

                    // Any host on the LAN can send this reply, and the name goes to the log and into the matching of
                    // the input target: only a valid host name counts (review, 2026-11-07).
                    return IsValidHostName(target) ? target : null;
                }

                offset += length;
            }
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or InvalidDataException)
        {
            // Malformed: no answer.
        }

        return null;
    }

    /// <summary>
    /// True for a DNS host name: labels of 1 to 63 ASCII letters, digits and '-' separated by dots, 253 characters at
    /// most ("Alexs-iPhone.local", "iPad-3.local"). No control characters, spaces or non-ASCII letters.
    /// </summary>
    internal static bool IsValidHostName(string name)
    {
        if (name.Length is 0 or > 253)
        {
            return false;
        }

        foreach (var label in name.Split('.'))
        {
            if (label.Length is 0 or > 63 || !label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            {
                return false;
            }
        }

        return true;
    }

    // A (possibly compressed) name at offset; returns it and the offset after it.
    private static (string Name, int Next) ReadName(ReadOnlySpan<byte> data, int offset)
    {
        var labels = new List<string>();
        var next = -1;
        for (var jumps = 0; ; )
        {
            var length = data[offset];
            if (length == 0)
            {
                offset++;
                break;
            }

            if ((length & 0xC0) == 0xC0)
            {
                if (++jumps > 16)
                {
                    throw new InvalidDataException("a name pointer loop");
                }

                if (next < 0)
                {
                    next = offset + 2;
                }

                offset = ((length & 0x3F) << 8) | data[offset + 1];
                continue;
            }

            labels.Add(Encoding.UTF8.GetString(data.Slice(offset + 1, length)));
            offset += 1 + length;
        }

        return (string.Join('.', labels), next < 0 ? offset : next);
    }
}
