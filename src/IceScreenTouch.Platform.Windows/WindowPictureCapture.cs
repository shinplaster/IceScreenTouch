// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using IceScreenTouch.Core;

namespace IceScreenTouch.Platform.Windows;

/// <summary>Which way a window's picture was taken.</summary>
public enum PictureSource
{
    /// <summary>Nothing was taken: the window is hidden, minimized or cloaked, or its frame does not match its geometry.</summary>
    None = 0,

    /// <summary>Windows.Graphics.Capture: the window's own pixels, whatever is above it on the screen.</summary>
    GraphicsCapture,

    /// <summary><c>PrintWindow</c> (only when Windows.Graphics.Capture cannot be used): the window's own pixels too.</summary>
    PrintWindow,
}

/// <summary>The pixels a <see cref="WindowPictureCapture"/> read, and the way they were taken.</summary>
/// <param name="Value">What the reader returned (default when nothing was read).</param>
/// <param name="Source">The way taken (when the capture failed, the way that was tried last).</param>
/// <typeparam name="T">What the reader makes of the pixels.</typeparam>
public readonly record struct PictureCaptureResult<T>(T? Value, PictureSource Source);

/// <summary>Whether a window can be read at all.</summary>
internal enum WindowPresence
{
    /// <summary>Visible and not minimized.</summary>
    Shown,

    /// <summary>Gone, not visible or minimized: there is no picture.</summary>
    Hidden,

    /// <summary>Cloaked by DWM (another virtual desktop): it is not drawn, so there is no picture either.</summary>
    Cloaked,
}

/// <summary>Where a window is: its top-level window, its visible frame and its client area, in screen pixels.</summary>
internal readonly record struct WindowGeometry(nint Root, RectI FrameBounds, RectI Client);

/// <summary>The questions about a window that decide the way of capture (the real ones are answered by Windows).</summary>
internal interface IWindowFacts
{
    WindowPresence PresenceOf(nint window);

    WindowGeometry? GeometryOf(nint window);
}

/// <summary>The PrintWindow capture, the fallback.</summary>
internal interface IPrintCapture
{
    T? Print<T>(nint window, CapturedPixelsReader<T> read);
}

/// <summary>
/// Reads the picture of a receiver window for something that leaves the application (the API's screenshots). Only the window's own
/// pixels are ever read, never the screen: other windows, click-through overlays and notifications cannot appear in the result.
/// </summary>
/// <remarks>
/// <para>The way: Windows.Graphics.Capture (<see cref="WgcSession"/>), a session kept for a window while it is used (a read takes about
/// a millisecond; starting a session about 25 ms; the Direct3D device about 300 ms, once). It does not make the window redraw
/// (PrintWindow made LonelyScreen flicker) and does not see what is above the window. Only when it cannot be used, the window is
/// read with PrintWindow: the system has no Windows.Graphics.Capture, the session or device could not be created, or no first frame
/// came. After such a failure Windows.Graphics.Capture is not tried again for <see cref="RetryAfterFailure"/>. A hidden, minimized
/// or cloaked window has no picture and is never read with PrintWindow either.</para>
/// <para>The reader gets the client area of the window (as <see cref="WindowCapture"/> gives it). In a capture frame, which is the
/// window's visible frame, that is a part at an offset, found with <see cref="CaptureGeometry.ClientInFrame"/>; a frame that does not
/// have the size the window's geometry says (a window being resized) is read again once when a newer frame arrives, and then gives
/// no picture rather than a misaligned one (and the session is closed, so the next shot starts one at the window's size). When that
/// happens <see cref="SystematicMismatches"/> times in a row for a window that did not change, it is how this system's frames are, not
/// a resize: that counts as a failure (PrintWindow for <see cref="RetryAfterFailure"/>). A window that is gone or minimized meanwhile,
/// and a refusal by Windows to capture the window (<see cref="UnauthorizedAccessException"/>: a privacy setting), are no failures, and
/// the second never falls back to PrintWindow. An exception of the caller's reader goes out as it is.</para>
/// <para>Threading: <see cref="Capture{T}"/> may be called from any thread, one call at a time is run (the others wait): the session
/// is kept in a <see cref="CaptureSessionCache{TSession}"/> that closes it after <see cref="CaptureSessionCache{TSession}.IdleTime"/>
/// without use, when another window is asked for, and on <see cref="Dispose"/>.</para>
/// </remarks>
public sealed class WindowPictureCapture : IDisposable
{
    /// <summary>How long a read waits for the first frame of a new session.</summary>
    internal static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>How long a read waits for a newer frame when the frame it has does not match the window (a resize).</summary>
    internal static readonly TimeSpan ResizeTimeout = TimeSpan.FromMilliseconds(300);

    /// <summary>How long Windows.Graphics.Capture is left alone after it failed.</summary>
    internal static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(30);

    /// <summary>How many shots in a row may have a frame that does not match a window that did not change, before that is taken for how this system is.</summary>
    internal const int SystematicMismatches = 3;

    private readonly TimeProvider _time;
    private readonly IWindowFacts _facts;
    private readonly IPrintCapture _print;
    private readonly Func<bool> _isSupported;
    private readonly Action<string, string>? _report;
    private readonly CaptureSessionCache<IWgcSession> _sessions;
    private readonly object _state = new();
    private WgcDevice? _device;
    private bool? _supported;
    private long _failedAt;
    private bool _failed;
    private (WindowGeometry Geometry, (int Width, int Height) FrameSize)? _lastMismatch;
    private int _mismatches;
    private bool _disposed;

    /// <summary>Creates the capture.</summary>
    /// <param name="time">The clock (default: the system's).</param>
    /// <param name="report">Told (kind, message) when something is worth a log line: Windows.Graphics.Capture could not be used, the frame did not match. The caller throttles.</param>
    public WindowPictureCapture(TimeProvider? time = null, Action<string, string>? report = null)
        : this(time ?? TimeProvider.System, new WindowFacts(), new PrintFallback(), null, null, report)
    {
    }

    /// <summary>For tests: every seam replaced.</summary>
    /// <param name="time">The clock.</param>
    /// <param name="facts">What Windows says about a window.</param>
    /// <param name="print">The PrintWindow capture.</param>
    /// <param name="isSupported">Whether Windows.Graphics.Capture can be used (default: asked from the system, once).</param>
    /// <param name="startSession">Starts a session for a top-level window (default: a real one).</param>
    /// <param name="report">The log.</param>
    internal WindowPictureCapture(TimeProvider time, IWindowFacts facts, IPrintCapture print, Func<bool>? isSupported, Func<nint, IWgcSession>? startSession, Action<string, string>? report)
    {
        _time = time;
        _facts = facts;
        _print = print;
        _isSupported = isSupported ?? WgcSession.IsSupported;
        _report = report;
        _sessions = new CaptureSessionCache<IWgcSession>(time, startSession ?? (root => new WgcSession(EnsureDevice(), root)), onEmpty: ReleaseStaging);
    }

    /// <summary>Windows.Graphics.Capture sessions that are open (for tests).</summary>
    internal int OpenSessions => _sessions.Count;

    /// <summary>
    /// Captures the client area of <paramref name="window"/> and hands the pixels to <paramref name="read"/>. The value is default when
    /// there is no picture (hidden, minimized, cloaked, the frame does not match the window, or the capture failed).
    /// </summary>
    /// <param name="window">The window (the receiver's window; for a window drawn inside a frame, the top-level window is captured).</param>
    /// <param name="read">Reads the pixels of the client area (valid only inside the call).</param>
    public PictureCaptureResult<T> Capture<T>(nint window, CapturedPixelsReader<T> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        ObjectDisposedException.ThrowIf(_disposed, this);

        // A window that is not drawn has no picture. PrintWindow would ask it to draw one (and Windows.Graphics.Capture would wait for a frame).
        switch (_facts.PresenceOf(window))
        {
            case WindowPresence.Hidden:
                return new PictureCaptureResult<T>(default, PictureSource.None);
            case WindowPresence.Cloaked:
                Report("cloaked", "the receiver window is cloaked (on another virtual desktop?): there is no picture");
                return new PictureCaptureResult<T>(default, PictureSource.None);
        }

        if (CanUseGraphicsCapture())
        {
            if (_facts.GeometryOf(window) is { } geometry)
            {
                if (TryGraphicsCapture(window, geometry, read) is { } done)
                {
                    return done;
                }
            }
            else
            {
                Report("geometry", "the window's frame could not be measured: reading it with PrintWindow");
            }
        }

        return new PictureCaptureResult<T>(_print.Print(window, read), PictureSource.PrintWindow);
    }

    /// <summary>Closes every session and the Direct3D device.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _sessions.Dispose();
        DisposeDevice();
    }

    // Null: Windows.Graphics.Capture failed, use PrintWindow.
    private PictureCaptureResult<T>? TryGraphicsCapture<T>(nint window, WindowGeometry geometry, CapturedPixelsReader<T> read)
    {
        var frameSize = (Width: 0, Height: 0);
        var mismatch = false;

        // An exception of the caller's reader is the caller's, not a failure of Windows.Graphics.Capture: it is kept here and thrown again
        // after the session's use (so it neither closes the session, nor starts the back-off, nor makes the reader run again on PrintWindow).
        ExceptionDispatchInfo? readerFailure = null;
        T reader(ReadOnlySpan<byte> bgra, int width, int height, int stride)
        {
            frameSize = (width, height);
            if (CaptureGeometry.ClientInFrame(geometry.FrameBounds, geometry.Client, width, height) is not { } client)
            {
                mismatch = true;
                return default!;
            }

            mismatch = false;
            // The span ends where the frame's last row does, so a part of it that lies inside the frame lies inside the span.
            var offset = ((long)client.Top * stride) + ((long)client.Left * 4);
            try
            {
                return read(bgra[(int)offset..], client.Width, client.Height, stride);
            }
#pragma warning disable CA1031 // rethrown as it was, after the session's use
            catch (Exception e)
#pragma warning restore CA1031
            {
                readerFailure = ExceptionDispatchInfo.Capture(e);
                return default!;
            }
        }

        (WgcReadStatus Status, T? Value, bool FirstFrameMissing) outcome;
        try
        {
            outcome = _sessions.Use(geometry.Root, session =>
            {
                var first = session.Read(reader, FirstFrameTimeout, false, out var picture);
                if (first == WgcReadStatus.NoFrame)
                {
                    return (first, picture, true);
                }

                if (!mismatch)
                {
                    return (first, picture, false);
                }

                // The frame is of another size than the window has now: a resize is on its way, and a resize delivers a new frame.
                var second = session.Read(reader, ResizeTimeout, true, out picture);
                return (second, picture, false);
            });
        }
        catch (UnauthorizedAccessException e)
        {
            // Windows refuses to let this window be captured (a privacy setting or a policy): that is the user's decision, and PrintWindow
            // must not get round it. There is no picture; the next shot asks again.
            Report("wgc-denied", $"Windows does not allow this window to be captured ({e.Message}): there is no picture");
            return new PictureCaptureResult<T>(default, PictureSource.None);
        }
        catch (Exception e) when (IsGraphicsCaptureFailure(e))
        {
            // The session has been closed by the cache. A window that went away between the check and the session is no failure.
            if (_facts.PresenceOf(window) == WindowPresence.Hidden)
            {
                return new PictureCaptureResult<T>(default, PictureSource.None);
            }

            // A failed Direct3D call may mean a lost device: start over with a new one (no session can start on the old one in between).
            _sessions.Clear(DisposeDevice);
            Fail(e.GetType().Name + ": " + e.Message);
            return null;
        }

        readerFailure?.Throw();

        if (outcome.FirstFrameMissing)
        {
            Fail("no first frame within " + (int)FirstFrameTimeout.TotalMilliseconds + " ms");
            return null;
        }

        if (mismatch || outcome.Status != WgcReadStatus.Ok)
        {
            // No newer frame came (a window that was resized once and stays still may deliver none): start afresh at the next shot, whose
            // first frame has the size the window has.
            _sessions.Drop(geometry.Root);
            var why = $"the capture frame ({frameSize.Width}x{frameSize.Height}) does not match the window (frame {geometry.FrameBounds.Width}x{geometry.FrameBounds.Height}, client {geometry.Client.Width}x{geometry.Client.Height})";
            if (NoteMismatch(geometry, frameSize))
            {
                // The same frame size for a window that did not change, again and again, with a fresh session each time: not a resize but
                // a system whose capture frames are not the size of the window's frame. PrintWindow can still take the picture.
                Fail(why + " every time");
                return null;
            }

            Report("geometry", why + ": there is no picture");
            return new PictureCaptureResult<T>(default, PictureSource.None);
        }

        ClearMismatches();
        return new PictureCaptureResult<T>(outcome.Value, PictureSource.GraphicsCapture);
    }

    // What Windows.Graphics.Capture or Direct3D can throw when they do not work. Never the caller's reader (see TryGraphicsCapture).
    // The HRESULTs map to ArgumentException (E_INVALIDARG), OutOfMemoryException (E_OUTOFMEMORY) and NotImplementedException (E_NOTIMPL).
    private static bool IsGraphicsCaptureFailure(Exception e) =>
        e is COMException or InvalidOperationException or ObjectDisposedException or InvalidCastException or ArgumentException or NotSupportedException
            or TypeLoadException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or OutOfMemoryException
            or NotImplementedException;

    // A frame that did not match a window that did not change since the last mismatch counts up; true at the third in a row.
    private bool NoteMismatch(WindowGeometry geometry, (int Width, int Height) frameSize)
    {
        lock (_state)
        {
            var key = (geometry, frameSize);
            _mismatches = _lastMismatch is { } last && last.Equals(key) ? _mismatches + 1 : 1;
            _lastMismatch = key;
            if (_mismatches < SystematicMismatches)
            {
                return false;
            }

            (_mismatches, _lastMismatch) = (0, null);
            return true;
        }
    }

    private void ClearMismatches()
    {
        lock (_state)
        {
            (_mismatches, _lastMismatch) = (0, null);
        }
    }

    private bool CanUseGraphicsCapture()
    {
        lock (_state)
        {
            _supported ??= _isSupported();
            if (_supported != true)
            {
                return false;
            }

            if (_failed && _time.GetElapsedTime(_failedAt) < RetryAfterFailure)
            {
                return false;
            }

            _failed = false;
            return true;
        }
    }

    private void Fail(string why)
    {
        lock (_state)
        {
            _failed = true;
            _failedAt = _time.GetTimestamp();
        }

        Report("wgc-failed", $"Windows.Graphics.Capture could not be used ({why}): reading the window with PrintWindow for {(int)RetryAfterFailure.TotalSeconds} s");
    }

    private void Report(string kind, string message) => _report?.Invoke(kind, message);

    private WgcDevice EnsureDevice()
    {
        lock (_state)
        {
            return _device ??= WgcDevice.Create();
        }
    }

    // No session is open any more: the staging texture (tens of MB for a big window) is not needed. The device stays.
    private void ReleaseStaging()
    {
        WgcDevice? device;
        lock (_state)
        {
            device = _device;
        }

        device?.ReleaseStaging();
    }

    private void DisposeDevice()
    {
        WgcDevice? device;
        lock (_state)
        {
            device = _device;
            _device = null;
        }

        device?.Dispose();
    }

    // What Windows says about a window.
    private sealed class WindowFacts : IWindowFacts
    {
        public WindowPresence PresenceOf(nint window) =>
            !Win32Windows.IsShown(window) ? WindowPresence.Hidden : Win32Windows.IsCloaked(window) ? WindowPresence.Cloaked : WindowPresence.Shown;

        public WindowGeometry? GeometryOf(nint window)
        {
            var root = Win32Windows.RootOf(window);
            return root != 0 && Win32Windows.ExtendedFrameBoundsOf(window) is { } frame && Win32Windows.ClientRectOnScreen(window) is { } client
                ? new WindowGeometry(root, frame, client)
                : null;
        }
    }

    private sealed class PrintFallback : IPrintCapture
    {
        public T? Print<T>(nint window, CapturedPixelsReader<T> read)
        {
            // One bitmap for a call: PrintWindow is the rare path, and WindowCapture is not thread-safe.
            using var capture = new WindowCapture { Mode = WindowCaptureMode.PrintWindowOnly };
            return capture.Capture(window, read);
        }
    }
}
