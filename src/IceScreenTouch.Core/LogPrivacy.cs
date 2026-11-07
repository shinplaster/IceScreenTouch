using System.Security.Cryptography;
using System.Text;

namespace IceScreenTouch.Core;

/// <summary>
/// What may go to the log in place of a device's raw Bluetooth ID (review, 2026-11-07). Such an ID looks like
/// "BluetoothLE#BluetoothLE&lt;PC adapter address&gt;-&lt;phone address&gt;": it names the PC's adapter and the phone's
/// identity address, and a log file is something users paste into bug reports.
/// </summary>
public static class LogPrivacy
{
    /// <summary>
    /// A short token for <paramref name="id"/>: "device-" and six hex digits of its SHA-256. The same ID gives the same
    /// token in every line and every run, so the lines of one device still correlate; the ID itself cannot be read
    /// back from it. Only for logging: keys and matching keep the real ID.
    /// </summary>
    public static string LogId(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return "device-none";
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(id));
        return "device-" + Convert.ToHexStringLower(hash.AsSpan(0, 3));
    }
}
