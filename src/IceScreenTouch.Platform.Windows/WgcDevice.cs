// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace IceScreenTouch.Platform.Windows;

/// <summary>Reads the pixels of a captured frame (32-bit BGRA, top-down rows, <c>stride</c> bytes apart); the span is valid only inside the call.</summary>
/// <typeparam name="T">What the reader makes of them.</typeparam>
internal delegate T WgcFrameReader<out T>(ReadOnlySpan<byte> bgra, int width, int height, int stride);

/// <summary>
/// The Direct3D 11 device that Windows.Graphics.Capture draws on, and the copy of a captured frame to CPU memory. Raw COM through
/// function pointers (no Direct3D wrapper library): only the few calls that are needed.
/// </summary>
/// <remarks>
/// Threading: the device itself is free-threaded, but the immediate context (CopyResource, Map, Unmap) is not, so every use of it and
/// of the staging texture is inside <see cref="_context"/>'s lock. One device is shared by all sessions of the process.
/// </remarks>
internal sealed unsafe partial class WgcDevice : IDisposable
{
    // The most that is read back (a window bigger than 16384 x 16384 or 512 MB of pixels is not a receiver window).
    private const int MaxSide = 16_384;
    private const long MaxBytes = 512L * 1024 * 1024;

    private const uint DriverTypeHardware = 1;
    private const uint DriverTypeWarp = 5;
    private const uint CreateBgraSupport = 0x20;
    private const uint SdkVersion = 7;
    private const uint DxgiFormatB8G8R8A8Unorm = 87;
    private const uint UsageStaging = 3;
    private const uint CpuAccessRead = 0x20000;
    private const uint MapRead = 1;

    // Vtable slots (IUnknown has 0 to 2).
    private const int SlotFrameGetSurface = 6; // IDirect3D11CaptureFrame (IInspectable has 0 to 5)
    private const int SlotGetInterface = 3; // IDirect3DDxgiInterfaceAccess
    private const int SlotSetMultithreadProtected = 5; // ID3D11Multithread
    private const int SlotTextureGetDesc = 10; // ID3D11Texture2D
    private const int SlotDeviceCreateTexture2D = 5; // ID3D11Device
    private const int SlotContextMap = 14; // ID3D11DeviceContext
    private const int SlotContextUnmap = 15;
    private const int SlotContextCopyResource = 47;

    private static readonly Guid IidDxgiDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    private static readonly Guid IidDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static readonly Guid IidTexture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    private static readonly Guid IidMultithread = new("9B7E4E00-342C-4106-A19F-4F2704F689F0");

    private readonly object _context = new();
    private nint _device;
    private nint _immediateContext;
    private nint _staging;
    private int _stagingWidth;
    private int _stagingHeight;
    private bool _disposed;

    private WgcDevice(nint device, nint context, IDirect3DDevice winRt)
    {
        _device = device;
        _immediateContext = context;
        WinRtDevice = winRt;
    }

    /// <summary>The device as the capture frame pool takes it.</summary>
    public IDirect3DDevice WinRtDevice { get; }

    /// <summary>Creates the device (hardware, else software); about 300 ms.</summary>
    /// <exception cref="COMException">No device could be created.</exception>
    public static WgcDevice Create()
    {
        nint device = 0, context = 0, dxgi = 0, inspectable = 0;
        try
        {
            var hr = D3D11CreateDevice(0, DriverTypeHardware, 0, CreateBgraSupport, 0, 0, SdkVersion, out device, out _, out context);
            if (hr < 0)
            {
                hr = D3D11CreateDevice(0, DriverTypeWarp, 0, CreateBgraSupport, 0, 0, SdkVersion, out device, out _, out context);
            }

            Marshal.ThrowExceptionForHR(hr);
            SetMultithreadProtected(context);
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(device, in IidDxgiDevice, out dxgi));
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out inspectable));
            var winRt = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable); // takes its own reference; ours is released below
            var made = new WgcDevice(device, context, winRt);
            (device, context) = (0, 0); // owned by the instance now
            return made;
        }
        finally
        {
            Release(dxgi);
            Release(inspectable);
            Release(context);
            Release(device);
        }
    }

    /// <summary>
    /// Copies the frame's texture to a staging texture and hands the pixels of its <see cref="Direct3D11CaptureFrame.ContentSize"/> to
    /// <paramref name="read"/>. A texture that is not 32-bit BGRA, or whose size is absurd, is refused.
    /// </summary>
    /// <exception cref="InvalidOperationException">The frame cannot be read back (format, size).</exception>
    /// <exception cref="COMException">Direct3D failed (for example the device was removed).</exception>
    public T ReadFrame<T>(Direct3D11CaptureFrame frame, WgcFrameReader<T> read)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(read);
        var content = frame.ContentSize;
        using var picture = OpenTexture(frame);
        var texture = picture.Texture;
        TextureDesc desc;
        ((delegate* unmanaged[Stdcall]<nint, TextureDesc*, void>)VTable(texture)[SlotTextureGetDesc])(texture, &desc);
        if (desc.Format != DxgiFormatB8G8R8A8Unorm || desc.Width == 0 || desc.Height == 0 || desc.Width > MaxSide || desc.Height > MaxSide
            || (long)desc.Width * desc.Height * 4 > MaxBytes)
        {
            throw new InvalidOperationException($"the capture texture cannot be read ({desc.Width}x{desc.Height}, format {desc.Format})");
        }

        if (content.Width <= 0 || content.Height <= 0 || content.Width > desc.Width || content.Height > desc.Height)
        {
            throw new InvalidOperationException($"the capture content size {content.Width}x{content.Height} does not fit the texture {desc.Width}x{desc.Height}");
        }

        lock (_context)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureStaging(desc);
            ((delegate* unmanaged[Stdcall]<nint, nint, nint, void>)VTable(_immediateContext)[SlotContextCopyResource])(_immediateContext, _staging, texture);
            MappedSubresource mapped;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, MappedSubresource*, int>)VTable(_immediateContext)[SlotContextMap])(
                _immediateContext, _staging, 0, MapRead, 0, &mapped));
            try
            {
                var stride = checked((int)mapped.RowPitch);
                var length = checked(((long)stride * (content.Height - 1)) + ((long)content.Width * 4));
                if (mapped.Data == 0 || stride < (long)content.Width * 4 || length > int.MaxValue)
                {
                    throw new InvalidOperationException("the mapped capture texture is unusable");
                }

                return read(new ReadOnlySpan<byte>((void*)mapped.Data, (int)length), content.Width, content.Height, stride);
            }
            finally
            {
                ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)VTable(_immediateContext)[SlotContextUnmap])(_immediateContext, _staging, 0);
            }
        }
    }

    /// <summary>The size of the texture behind a frame (smaller than the frame's content just after a resize).</summary>
    /// <exception cref="COMException">The texture could not be reached.</exception>
    public static (int Width, int Height) TextureSizeOf(Direct3D11CaptureFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        using var picture = OpenTexture(frame);
        TextureDesc desc;
        ((delegate* unmanaged[Stdcall]<nint, TextureDesc*, void>)VTable(picture.Texture)[SlotTextureGetDesc])(picture.Texture, &desc);
        return (desc.Width > int.MaxValue ? int.MaxValue : (int)desc.Width, desc.Height > int.MaxValue ? int.MaxValue : (int)desc.Height);
    }

    /// <summary>
    /// Drops the staging texture (it is made again at the next read): a texture of a 4K window is some 30 MB that nothing needs while no
    /// window is captured. The device itself stays.
    /// </summary>
    public void ReleaseStaging()
    {
        lock (_context)
        {
            if (_disposed)
            {
                return;
            }

            Release(_staging);
            (_staging, _stagingWidth, _stagingHeight) = (0, 0, 0);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_context)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Release(_staging);
            Release(_immediateContext);
            Release(_device);
            (_staging, _immediateContext, _device) = (0, 0, 0);
        }

        (WinRtDevice as IDisposable)?.Dispose();
    }

    // The staging texture is kept while the frame size stays the same (called inside the lock).
    private void EnsureStaging(TextureDesc source)
    {
        if (_staging != 0 && _stagingWidth == source.Width && _stagingHeight == source.Height)
        {
            return;
        }

        Release(_staging);
        _staging = 0;
        var desc = source;
        desc.Usage = UsageStaging;
        desc.BindFlags = 0;
        desc.CpuAccessFlags = CpuAccessRead;
        desc.MiscFlags = 0;
        nint texture;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, TextureDesc*, nint, nint*, int>)VTable(_device)[SlotDeviceCreateTexture2D])(_device, &desc, 0, &texture));
        _staging = texture;
        (_stagingWidth, _stagingHeight) = ((int)source.Width, (int)source.Height);
    }

    // The frame's surface straight from its ABI (get_Surface), without a projected IDirect3DSurface: a projection would stay alive until
    // the garbage collector found it, and there is one for every frame the window draws. The references are released on Dispose.
    private static FrameTexture OpenTexture(Direct3D11CaptureFrame frame)
    {
        nint surface = 0, access = 0, texture = 0;
        try
        {
            var native = ((IWinRTObject)frame).NativeObject.ThisPtr; // the frame's default interface, IDirect3D11CaptureFrame
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint*, int>)VTable(native)[SlotFrameGetSurface])(native, &surface));
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(surface, in IidDxgiInterfaceAccess, out access));
            var iidTexture = IidTexture2D;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)VTable(access)[SlotGetInterface])(access, &iidTexture, &texture));
            GC.KeepAlive(frame);
            var made = new FrameTexture(surface, access, texture);
            (surface, access, texture) = (0, 0, 0);
            return made;
        }
        finally
        {
            Release(texture);
            Release(access);
            Release(surface);
        }
    }

    // ID3D11Multithread::SetMultithreadProtected(TRUE): the immediate context is entered by reads from any thread and by the capture's
    // own. A device made without D3D11_CREATE_DEVICE_SINGLETHREADED is protected already; this makes it explicit.
    private static void SetMultithreadProtected(nint context)
    {
        nint multithread = 0;
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(context, in IidMultithread, out multithread));
            _ = ((delegate* unmanaged[Stdcall]<nint, int, int>)VTable(multithread)[SlotSetMultithreadProtected])(multithread, 1);
        }
        finally
        {
            Release(multithread);
        }
    }

    private static nint* VTable(nint comObject) => *(nint**)comObject;

    private static void Release(nint comObject)
    {
        if (comObject != 0)
        {
            _ = Marshal.Release(comObject);
        }
    }

    // D3D11_TEXTURE2D_DESC (44 bytes) and D3D11_MAPPED_SUBRESOURCE.
    [StructLayout(LayoutKind.Sequential)]
    private struct TextureDesc
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public uint Format;
        public uint SampleCount;
        public uint SampleQuality;
        public uint Usage;
        public uint BindFlags;
        public uint CpuAccessFlags;
        public uint MiscFlags;
    }

    // The references to a frame's texture and what it was reached through.
    private readonly struct FrameTexture(nint surface, nint access, nint texture) : IDisposable
    {
        public nint Texture { get; } = texture;

        public void Dispose()
        {
            Release(Texture);
            Release(access);
            Release(surface);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MappedSubresource
    {
        public nint Data;
        public uint RowPitch;
        public uint DepthPitch;
    }

    [LibraryImport("d3d11.dll")]
    private static partial int D3D11CreateDevice(nint adapter, uint driverType, nint software, uint flags, nint featureLevels, uint featureLevelCount, uint sdkVersion, out nint device, out uint featureLevel, out nint context);

    [LibraryImport("d3d11.dll")]
    private static partial int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);
}
