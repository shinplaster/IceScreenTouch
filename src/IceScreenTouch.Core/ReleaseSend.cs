namespace IceScreenTouch.Core;

/// <summary>
/// Sends that must reach the device even when the work around them is cancelled: lifting a finger, releasing a
/// key. Cancelling a gesture (stop, restart, a cancelled probe) cancels its token; a release sent with that token
/// would not go out, and the device would keep the finger or the key down (iOS repeats a held key, a held Menu
/// key opens Siri). A release report carries the whole state, so sending it twice does no harm.
/// </summary>
public static class ReleaseSend
{
    /// <summary>How long a release may take once the caller's token is cancelled.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Runs <paramref name="send"/> with <paramref name="cancellationToken"/>; if that token is (or becomes)
    /// cancelled, runs it again with a token of its own that ends after <see cref="Grace"/>.
    /// </summary>
    public static async ValueTask RunAsync(Func<CancellationToken, ValueTask> send, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(send);
        if (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await send(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Cancelled on the way: send it again below.
            }
        }

        using var grace = new CancellationTokenSource(Grace);
        await send(grace.Token).ConfigureAwait(false);
    }
}
