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

/// <summary>
/// A session without the capture border was asked for and cannot be had (Windows older than 10.0.20348, a package that is excluded
/// from it, or Windows refused to switch the border off). The caller uses another way, and does not ask again: it will not change.
/// </summary>
internal sealed class BorderlessCaptureUnavailableException : InvalidOperationException
{
    public BorderlessCaptureUnavailableException(string message)
        : base(message)
    {
    }

    public BorderlessCaptureUnavailableException(string message, Exception inner)
        : base(message, inner)
    {
    }
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

    /// <summary>
    /// The one place to flip: whether a borderless session (<c>IsBorderRequired</c> off) is tried in an MSIX package (the Store
    /// build). Off: in a package without the user's consent (<c>GraphicsCaptureAccess.RequestAccessAsync</c>, which this app does
    /// not ask for) setting the property neither throws nor works, and the yellow border is drawn anyway (verified 2026-11-17 with a
    /// test-signed package). The locating capture therefore uses PrintWindow in a package. A build of your own (no package identity)
    /// is not affected: there the border does go away.
    /// </summary>
    internal const bool AllowBorderlessInPackage = false;

    /// <summary>Starts capturing <paramref name="window"/> (a top-level window).</summary>
    /// <param name="device">The Direct3D device.</param>
    /// <param name="window">The top-level window.</param>
    /// <param name="borderless">Without the capture border (no consent is asked for); the session does not start when that cannot be had.</param>
    /// <exception cref="COMException">The window cannot be captured (or no capture on this system).</exception>
    /// <exception cref="BorderlessCaptureUnavailableException"><paramref name="borderless"/> and the border cannot be switched off.</exception>
    public WgcSession(WgcDevice device, nint window, bool borderless = false)
    {
        if (borderless && !BorderlessAvailable())
        {
            throw new BorderlessCaptureUnavailableException("a borderless capture is not available here (a package, or Windows before 10.0.20348)");
        }

        _device = device;
        var item = CreateItem(window);
        _poolSize = item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device.WinRtDevice, PixelFormat, PoolBuffers, _poolSize);
        try
        {
            _pool.FrameArrived += OnFrameArrived;
            _session = _pool.CreateCaptureSession(item);
            SetOptions(_session, borderless);
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

    /// <summary>
    /// Whether a session without the capture border can be tried at all: Windows 10.0.20348 or later (where <c>IsBorderRequired</c> exists)
    /// and, in a package, only when <see cref="AllowBorderlessInPackage"/>. Answered without touching Windows.Graphics.Capture or Direct3D.
    /// </summary>
    internal static bool BorderlessAvailable() =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348) && BorderlessAllowed(PackageIdentity.IsPackaged, AllowBorderlessInPackage);

    /// <summary>Whether a borderless session may be tried: always in a build of your own, in a package only when <paramref name="allowInPackage"/>.</summary>
    internal static bool BorderlessAllowed(bool packaged, bool allowInPackage) => !packaged || allowInPackage;

    // The cursor is a matter of looks, and a failure there is ignored. The border is not: IsBorderRequired is left at the system default
    // (Windows draws a colored border around the window while the session is open, which tells the user that it is being captured)
    // unless the caller asks for a session without it, and then that must work: the session does not start otherwise (no yellow border
    // while a window is only being looked at). IsBorderRequired = false needs no consent (GraphicsCaptureAccess.RequestAccessAsync is
    // never called) and no capability in the manifest for a build of your own. In a package it is silently ignored without consent, so a
    // package does not get here (AllowBorderlessInPackage).
    private static void SetOptions(GraphicsCaptureSession session, bool borderless)
    {
        // The mouse pointer is not part of a picture (IsCursorCaptureEnabled exists from Windows 10 version 1903).
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

        if (!borderless)
        {
            return;
        }

        // IsBorderRequired exists from Windows 11 / Server 2022 (10.0.20348).
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
        {
            throw new BorderlessCaptureUnavailableException("the capture border cannot be switched off before Windows 10.0.20348");
        }

        try
        {
            session.IsBorderRequired = false;
        }
        catch (Exception e) when (e is COMException or InvalidCastException or UnauthorizedAccessException or InvalidOperationException
            or TypeLoadException or NotSupportedException or NotImplementedException or EntryPointNotFoundException)
        {
            throw new BorderlessCaptureUnavailableException("Windows did not switch the capture border off: " + e.Message, e);
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
