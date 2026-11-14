// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using WinRT;

namespace IceScreenTouch.Platform.Windows;

/// <summary>What a read of a capture session found.</summary>
internal enum WgcReadStatus
{
    /// <summary>The reader ran on the most recent frame.</summary>
    Ok,

    /// <summary>No frame arrived in time (none at all yet, or none newer than the one asked to be replaced).</summary>
    NoFrame,
}

/// <summary>A running capture of one window: keeps the most recent frame and reads it on demand.</summary>
internal interface IWgcSession : IDisposable
{
    /// <summary>
    /// Hands the pixels of the most recent frame (its content size, not the pool's) to <paramref name="read"/>. Waits up to
    /// <paramref name="timeout"/> for a frame: for the first one, or, with <paramref name="requireNewFrame"/>, for one newer than the
    /// frame the window had when the call began (a resized window delivers a new frame at once; a still one delivers none).
    /// </summary>
    /// <param name="read">Reads the pixels (valid only inside the call).</param>
    /// <param name="timeout">The longest wait for a frame.</param>
    /// <param name="requireNewFrame">Do not use a frame that was already there.</param>
    /// <param name="result">What the reader returned (default unless <see cref="WgcReadStatus.Ok"/>).</param>
    /// <exception cref="ObjectDisposedException">The session is closed.</exception>
    /// <exception cref="InvalidOperationException">The frame could not be read back.</exception>
    /// <exception cref="COMException">Direct3D failed.</exception>
    WgcReadStatus Read<T>(WgcFrameReader<T> read, TimeSpan timeout, bool requireNewFrame, out T? result);
}

/// <summary>
/// A Windows.Graphics.Capture session on one top-level window. The frame pool is free-threaded: <c>FrameArrived</c> comes on a
/// thread of the system's, while reads come from any thread of ours; both only touch the state inside <see cref="_gate"/>. The
/// session keeps the most recent frame (a static window delivers no new ones) and drops the one it replaces. Only the window's own
/// pixels are captured: windows above it, click-through overlays and toasts are not in the frame.
/// </summary>
internal sealed partial class WgcSession : IWgcSession
{
    private const int PoolBuffers = 2;
    private const DirectXPixelFormat PixelFormat = DirectXPixelFormat.B8G8R8A8UIntNormalized;

    // An object, not a Lock: Monitor.Wait/PulseAll signal a new frame.
    private readonly object _gate = new();
    private readonly WgcDevice _device;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private Direct3D11CaptureFrame? _latest;
    private long _sequence;
    private SizeInt32 _poolSize;
    private bool _disposed;

    /// <summary>Starts capturing <paramref name="window"/> (a top-level window).</summary>
    /// <exception cref="COMException">The window cannot be captured (or no capture on this system).</exception>
    public WgcSession(WgcDevice device, nint window)
    {
        _device = device;
        var item = CreateItem(window);
        _poolSize = item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device.WinRtDevice, PixelFormat, PoolBuffers, _poolSize);
        try
        {
            _pool.FrameArrived += OnFrameArrived;
            _session = _pool.CreateCaptureSession(item);
            SetOptions(_session);
            _session.StartCapture();
        }
        catch
        {
            _pool.FrameArrived -= OnFrameArrived;
            _session?.Dispose();
            _pool.Dispose();
            throw;
        }
    }

    /// <summary>Whether Windows.Graphics.Capture works on this system (Windows 10 version 1803 and later; false on any doubt).</summary>
    public static bool IsSupported()
    {
        try
        {
            return GraphicsCaptureSession.IsSupported();
        }
        catch (Exception e) when (e is COMException or InvalidCastException or TypeLoadException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public WgcReadStatus Read<T>(WgcFrameReader<T> read, TimeSpan timeout, bool requireNewFrame, out T? result)
    {
        ArgumentNullException.ThrowIfNull(read);
        result = default;
        var deadline = Environment.TickCount64 + (long)Math.Max(timeout.TotalMilliseconds, 0);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var wanted = requireNewFrame ? _sequence + 1 : 1;
            while (_sequence < wanted || _latest is null)
            {
                var left = deadline - Environment.TickCount64;
                if (left <= 0 || !Monitor.Wait(_gate, (int)left))
                {
                    return WgcReadStatus.NoFrame;
                }

                ObjectDisposedException.ThrowIf(_disposed, this);
            }

            // Inside the lock: a frame that a newer one replaces is disposed by the handler only after it left the lock, and it can
            // not take the lock before this read is done.
            result = _device.ReadFrame(_latest, read);
            return WgcReadStatus.Ok;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Monitor.PulseAll(_gate); // a reader that is waiting finds it closed
        }

        // Each step runs whatever the one before did: a session that fails to close must not keep the pool and the frame alive.
        try
        {
            _pool.FrameArrived -= OnFrameArrived;
            _session.Dispose();
        }
        finally
        {
            try
            {
                _pool.Dispose();
            }
            finally
            {
                lock (_gate)
                {
                    var latest = _latest;
                    _latest = null;
                    latest?.Dispose();
                }
            }
        }
    }

    // The frame of the window's own pixels; on a resize the pool is recreated at the new size (frames keep the size they had).
    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        Direct3D11CaptureFrame? frame = null;
        Direct3D11CaptureFrame? replaced = null;
        try
        {
            frame = sender.TryGetNextFrame();
            if (frame is null)
            {
                return;
            }

            var content = frame.ContentSize;
            bool resized;
            SizeInt32 previous;
            lock (_gate)
            {
                previous = _poolSize;
                resized = !_disposed && (content.Width != _poolSize.Width || content.Height != _poolSize.Height);
                if (resized)
                {
                    _poolSize = content;
                }
            }

            if (resized)
            {
                try
                {
                    sender.Recreate(_device.WinRtDevice, PixelFormat, PoolBuffers, content); // not inside the lock: it may wait for the pool
                }
                catch
                {
                    // The pool still has its old size: the next frame of the new size must try again, not take it for done.
                    lock (_gate)
                    {
                        if (_poolSize.Width == content.Width && _poolSize.Height == content.Height)
                        {
                            _poolSize = previous;
                        }
                    }

                    throw;
                }
            }

            // The first frame after a resize has the new content size but still a texture of the old pool size, which cuts the content
            // off: it is no use. The pool is recreated above, and the frames that follow are whole; the frame kept stays the last whole one.
            var (textureWidth, textureHeight) = WgcDevice.TextureSizeOf(frame);
            if (content.Width > textureWidth || content.Height > textureHeight)
            {
                return; // the frame is disposed below
            }

            lock (_gate)
            {
                if (_disposed)
                {
                    return; // the frame is disposed below
                }

                replaced = _latest;
                _latest = frame;
                frame = null; // kept
                _sequence++;
                Monitor.PulseAll(_gate);
            }
        }
        catch (Exception e) when (e is COMException or ObjectDisposedException or InvalidOperationException or ArgumentException or InvalidCastException
            or UnauthorizedAccessException or NotImplementedException or OutOfMemoryException)
        {
            // The pool or the window went away while the frame was taken (an exception on the system's thread would end the process):
            // the next read finds no (new) frame, or the session is closed.
        }
        finally
        {
            replaced?.Dispose();
            frame?.Dispose();
        }
    }

    // Options that only matter for looks; the capture works without them, so a failure is ignored.
    // IsBorderRequired is deliberately left at the system default: Windows draws a colored border around the window while the session
    // is open, which tells the user that the window is being captured. A borderless capture would need the user's consent
    // (GraphicsCaptureAccess.RequestAccessAsync, a prompt) and, in a package, a capability; this does not ask for either.
    private static void SetOptions(GraphicsCaptureSession session)
    {
        // The mouse pointer is not part of a screenshot (IsCursorCaptureEnabled exists from Windows 10 version 1903).
        try
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362))
            {
                session.IsCursorCaptureEnabled = false;
            }
        }
        catch (Exception e) when (e is COMException or InvalidCastException or UnauthorizedAccessException or InvalidOperationException)
        {
        }
    }

    private static unsafe GraphicsCaptureItem CreateItem(nint window)
    {
        const string ClassName = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(ClassName, ClassName.Length, out var name));
        nint factory = 0, item = 0;
        try
        {
            var iidInterop = new Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"); // IGraphicsCaptureItemInterop
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(name, in iidInterop, out factory));
            var iidItem = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760"); // IGraphicsCaptureItem
            // IGraphicsCaptureItemInterop derives from IUnknown: CreateForWindow is slot 3.
            var createForWindow = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)(*(nint**)factory)[3];
            nint created;
            Marshal.ThrowExceptionForHR(createForWindow(factory, window, &iidItem, &created));
            item = created;
            return GraphicsCaptureItem.FromAbi(item);
        }
        finally
        {
            _ = WindowsDeleteString(name);
            if (item != 0)
            {
                _ = Marshal.Release(item);
            }

            if (factory != 0)
            {
                _ = Marshal.Release(factory);
            }
        }
    }

    [LibraryImport("combase.dll")]
    private static partial int RoGetActivationFactory(nint classId, in Guid iid, out nint factory);

    [LibraryImport("combase.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int WindowsCreateString(string source, int length, out nint hstring);

    [LibraryImport("combase.dll")]
    private static partial int WindowsDeleteString(nint hstring);
}
