// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Runtime.ExceptionServices;

namespace IceScreenTouch.Core;

/// <summary>
/// Sits in front of the real sink of a session and remembers the last touch and keyboard report that went through it, whoever
/// sent it (the bridge, the keyboard relay, a lease of the automation API). When the session ends, <see cref="ReleaseAllAsync"/>
/// turns whatever is still held on the device into a neutral state: a finger that a script put down is lifted at its LAST
/// position (never at (0, 0), see <see cref="TouchPlayer.LiftAsync"/>) and a key it holds is released. The script's own cleanup
/// goes through the host, which has no session left at that moment, so this is what keeps the device from a stuck finger or key.
/// After the release the tracker is closed: a send that arrives later (an API thread that read the session just before it went away
/// and was paused) is dropped when it would press a finger, a key or the home button again; a neutral send (every finger up, no key)
/// still goes through, it cannot press anything.
/// </summary>
public sealed class ReportTracker : ITouchSink, IKeyboardSink, IHomeButtonSink
{
    private static readonly TouchContact[] NoContacts = [];

    private readonly ITouchSink _touch;
    private readonly IKeyboardSink? _keyboard;
    private readonly IHomeButtonSink? _home;
    private readonly Lock _gate = new();
    private TouchContact[] _lastContacts = NoContacts;
    private bool _keysHeld;
    private bool _closed;

    /// <summary>
    /// Creates a tracker over <paramref name="inner"/>. Its keyboard and home button are those of <paramref name="inner"/> when it
    /// implements <see cref="IKeyboardSink"/> / <see cref="IHomeButtonSink"/> (otherwise <see cref="HasKeyboard"/> and
    /// <see cref="HasHomeButton"/> are false and those sends do nothing).
    /// </summary>
    public ReportTracker(ITouchSink inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _touch = inner;
        _keyboard = inner as IKeyboardSink;
        _home = inner as IHomeButtonSink;
    }

    /// <inheritdoc/>
    public bool IsSubscribed => _touch.IsSubscribed;

    /// <inheritdoc/>
    public bool HasKeyboard => _keyboard?.HasKeyboard ?? false;

    /// <inheritdoc/>
    public bool HasHomeButton => _home?.HasHomeButton ?? false;

    /// <inheritdoc/>
    public ValueTask SendAsync(ReadOnlyMemory<TouchContact> contacts, CancellationToken cancellationToken)
    {
        // Recorded before the send, like TouchPlayer: a send that failed may still have reached the device.
        var copy = contacts.IsEmpty ? NoContacts : contacts.ToArray();
        lock (_gate)
        {
            if (_closed)
            {
                // After the release only a send that cannot press a finger goes on (and it leaves nothing held to remember).
                return copy.Any(c => c.Tip) ? ValueTask.CompletedTask : _touch.SendAsync(contacts, cancellationToken);
            }

            _lastContacts = copy;
        }

        return _touch.SendAsync(contacts, cancellationToken);
    }

    /// <inheritdoc/>
    public ValueTask SendKeyboardAsync(byte modifiers, ReadOnlyMemory<byte> keys, CancellationToken cancellationToken)
    {
        if (_keyboard is not { } keyboard)
        {
            return ValueTask.CompletedTask;
        }

        var held = modifiers != 0 || keys.Span.ContainsAnyExcept((byte)0);
        lock (_gate)
        {
            if (_closed)
            {
                return held ? ValueTask.CompletedTask : keyboard.SendKeyboardAsync(modifiers, keys, cancellationToken);
            }

            _keysHeld = held;
        }

        return keyboard.SendKeyboardAsync(modifiers, keys, cancellationToken);
    }

    /// <inheritdoc/>
    public ValueTask PressHomeAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_closed)
            {
                return ValueTask.CompletedTask;
            }
        }

        return _home?.PressHomeAsync(cancellationToken) ?? ValueTask.CompletedTask;
    }

    /// <summary>What the last reports left held: a finger down, a key or modifier down (for the log).</summary>
    public (bool Fingers, bool Keys) Held
    {
        get
        {
            lock (_gate)
            {
                return (_lastContacts.Any(c => c.Tip), _keysHeld);
            }
        }
    }

    /// <summary>
    /// Sends the neutral state for whatever the last reports left held: the lift of every finger (Tip = 0 at its last position,
    /// then the empty report) and the release of every key (an empty keyboard report). Sends nothing for a part that is already
    /// neutral. Both parts are tried; the first failure is thrown after. The sends also go out when <paramref name="cancellationToken"/>
    /// is cancelled (<see cref="ReleaseSend"/>). It also closes the tracker (see the class text); calling it again is harmless.
    /// </summary>
    public async ValueTask ReleaseAllAsync(CancellationToken cancellationToken = default)
    {
        TouchContact[] contacts;
        bool keys;
        lock (_gate)
        {
            contacts = _lastContacts;
            keys = _keysHeld;
            _closed = true;
            _lastContacts = NoContacts;
            _keysHeld = false;
        }

        Exception? failure = null;
        if (contacts.Length > 0)
        {
            try
            {
                if (contacts.Any(c => c.Tip))
                {
                    var lifted = Array.ConvertAll(contacts, c => c.Lifted());
                    await ReleaseSend.RunAsync(ct => _touch.SendAsync(lifted, ct), cancellationToken).ConfigureAwait(false);
                }

                await ReleaseSend.RunAsync(ct => _touch.SendAsync(ReadOnlyMemory<TouchContact>.Empty, ct), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failure = ex;
            }
        }

        if (keys && _keyboard is { HasKeyboard: true } keyboard)
        {
            try
            {
                await ReleaseSend.RunAsync(ct => keyboard.SendKeyboardAsync(0, ReadOnlyMemory<byte>.Empty, ct), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failure ??= ex;
            }
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Throw(failure);
        }
    }
}
