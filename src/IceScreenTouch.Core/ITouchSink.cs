namespace IceScreenTouch.Core;

/// <summary>Where touch reports go: the BLE HID peripheral in production, a recorder in tests.</summary>
public interface ITouchSink
{
    /// <summary>True when the phone has subscribed to the touch report (notifications reach it).</summary>
    bool IsSubscribed { get; }

    /// <summary>
    /// Sends one touch report with exactly these contacts (empty = no finger). The sink only encodes
    /// and transmits; the lift rules live in <see cref="TouchPlayer"/>.
    /// </summary>
    ValueTask SendAsync(ReadOnlyMemory<TouchContact> contacts, CancellationToken cancellationToken);
}

/// <summary>
/// A sink that can also press the device's home button (a HID consumer key). iPadOS 18 took the home swipe from
/// the bottom edge for an ordinary scroll, while the consumer "Menu" key went home (2026-11-06).
/// </summary>
public interface IHomeButtonSink
{
    /// <summary>The home button can be pressed (the published HID map has the consumer keys).</summary>
    bool HasHomeButton { get; }

    /// <summary>Presses and releases the home button.</summary>
    ValueTask PressHomeAsync(CancellationToken cancellationToken);
}
