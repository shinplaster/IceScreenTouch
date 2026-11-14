// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.Core;

namespace IceScreenTouch.Api;

/// <summary>
/// The sinks a REST operation sends to, with one extra rule: once the user has taken over (the lease's
/// <see cref="IInputLease.UserInput"/> is cancelled) no report that puts a finger or a key down goes out any more. The check is
/// synchronous, so it does not wait for the cancellation of the operation's own token (which is queued to the thread pool);
/// lifts and releases always pass. A refused send throws <see cref="OperationCanceledException"/>, which the operation turns
/// into <c>user_interrupted</c> after its own lift.
/// </summary>
internal sealed class InputGuard
{
    private volatile IInputLease? _lease;

    public InputGuard(IApiHost host)
    {
        Touch = new GuardedTouch(host.Touch, this);
        Keyboard = new GuardedKeyboard(host.Keyboard, this);
        Home = new GuardedHome(host.HomeButton, this);
    }

    /// <summary>The input lease of the operation; set once it has been taken.</summary>
    public IInputLease? Lease
    {
        set => _lease = value;
    }

    public ITouchSink Touch { get; }

    public IKeyboardSink Keyboard { get; }

    public IHomeButtonSink Home { get; }

    private void ThrowIfInterrupted()
    {
        if (_lease is { } lease && lease.UserInput.IsCancellationRequested)
        {
            throw new OperationCanceledException(lease.UserInput);
        }
    }

    private sealed class GuardedTouch(ITouchSink inner, InputGuard guard) : ITouchSink
    {
        public bool IsSubscribed => inner.IsSubscribed;

        public ValueTask SendAsync(ReadOnlyMemory<TouchContact> contacts, CancellationToken cancellationToken)
        {
            foreach (var contact in contacts.Span)
            {
                if (contact.Tip)
                {
                    guard.ThrowIfInterrupted();
                    break;
                }
            }

            return inner.SendAsync(contacts, cancellationToken);
        }
    }

    private sealed class GuardedKeyboard(IKeyboardSink inner, InputGuard guard) : IKeyboardSink
    {
        public bool HasKeyboard => inner.HasKeyboard;

        public ValueTask SendKeyboardAsync(byte modifiers, ReadOnlyMemory<byte> keys, CancellationToken cancellationToken)
        {
            if (modifiers != 0 || !keys.IsEmpty)
            {
                guard.ThrowIfInterrupted();
            }

            return inner.SendKeyboardAsync(modifiers, keys, cancellationToken);
        }
    }

    private sealed class GuardedHome(IHomeButtonSink inner, InputGuard guard) : IHomeButtonSink
    {
        public bool HasHomeButton => inner.HasHomeButton;

        public ValueTask PressHomeAsync(CancellationToken cancellationToken)
        {
            guard.ThrowIfInterrupted();
            return inner.PressHomeAsync(cancellationToken);
        }
    }
}
