using System.Globalization;

namespace IceScreenTouch.Core;

/// <summary>
/// Keeps track of where the picture is in a receiver window that is captured now and then (see
/// <see cref="PictureLocator"/>). <see cref="Update"/> runs on the capture thread; <see cref="Locate"/> answers the
/// bridge loop at once from the last result. Until a capture of the current window size exists (the window was
/// just resized or rotated), <see cref="Locate"/> guesses: a client area with the phone's shape is all picture,
/// anything else holds a centered picture in the last known orientation.
/// </summary>
/// <remarks>
/// The phone's aspect ratio is fixed. Learning it from clearly bounded pictures was tried and dropped: a 16:9
/// video played in landscape has black bars of its own and would teach a wrong ratio.
/// </remarks>
public sealed class PictureTracker
{
    private readonly Action<string> _log;
    private readonly PictureLocatorOptions _options;
    private volatile Detection? _last;
    private volatile int _fit = (int)PictureFit.Portrait;
    private volatile int _bottomInset;
    private double _aspect;

    /// <summary>Uses <paramref name="portraitAspect"/> (width / height of the phone screen in portrait).</summary>
    public PictureTracker(double portraitAspect, Action<string>? log = null, PictureLocatorOptions? options = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(portraitAspect);
        _aspect = portraitAspect;
        _log = log ?? (_ => { });
        _options = options ?? PictureLocatorOptions.Default;
    }

    private sealed record Detection(nint Window, int Width, int Height, RectD Picture, PictureFit Fit);

    /// <summary>
    /// The phone's width / height in portrait. Setting another one (the input went to a device of another shape)
    /// forgets the captures.
    /// </summary>
    public double PortraitAspect
    {
        get => _aspect;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            if (value != _aspect)
            {
                _aspect = value;
                _last = null;
            }
        }
    }

    /// <summary>The orientation of the last located picture.</summary>
    public PictureFit Fit => (PictureFit)_fit;

    /// <summary>The picture found by the last successful capture (relative to that window's client area).</summary>
    public RectD? LastPicture => _last?.Picture;

    /// <summary>
    /// Pixels at the bottom of the client area that belong to the receiver, not to the picture (LonelyScreen's
    /// toolbar): left out when looking for the picture and when guessing it. A full-width bar there looked like
    /// picture and threw the measurement off (2026-11-06).
    /// </summary>
    public int BottomInset { get => _bottomInset; set => _bottomInset = Math.Max(0, value); }

    /// <summary><paramref name="client"/> without <see cref="BottomInset"/> (at least one pixel high).</summary>
    public SizeD PictureArea(SizeD client) => new(client.Width, Math.Max(Math.Min(client.Height, 1), client.Height - BottomInset));

    /// <summary>
    /// Feeds a capture of the client area of <paramref name="window"/> (capture thread). Returns what was found,
    /// or null when the image tells nothing (the previous result then stays).
    /// </summary>
    public PictureEstimate? Update(nint window, ReadOnlySpan<byte> bgra, int width, int height, int stride)
    {
        // Rows are top-down: leaving out the bottom rows is a shorter image.
        var area = (int)PictureArea(new SizeD(width, height)).Height;
        var estimate = PictureLocator.Locate(bgra[..Math.Min(bgra.Length, area * stride)], width, area, stride, PortraitAspect, Fit, _options);
        if (estimate is null)
        {
            return null;
        }

        var previous = _last;
        var detection = new Detection(window, width, height, estimate.Picture, estimate.Fit);
        _last = detection;
        _fit = (int)(estimate.Fit == PictureFit.Measured ? (estimate.Picture.IsLandscape ? PictureFit.Landscape : PictureFit.Portrait) : estimate.Fit);
        if (previous is null || previous.Fit != detection.Fit || previous.Window != window || !Close(previous.Picture, detection.Picture))
        {
            _log(string.Create(
                CultureInfo.InvariantCulture,
                $"[video] picture {estimate.Fit.ToString().ToLowerInvariant()}{(estimate.Ambiguous ? " (dark screen: kept)" : string.Empty)} at ({estimate.Picture.Left:0},{estimate.Picture.Top:0}) {estimate.Picture.Width:0}x{estimate.Picture.Height:0} in {width}x{height}"));
        }

        return estimate;
    }

    /// <summary>
    /// The picture inside a client area of size <paramref name="client"/> (bridge loop). <paramref name="fresh"/> is
    /// false when the answer is a guess because no capture of this window at this size exists yet.
    /// </summary>
    public RectD Locate(nint window, SizeD client, out bool fresh)
    {
        var last = _last;
        if (last is not null && last.Window == window && last.Width == (int)client.Width && last.Height == (int)client.Height)
        {
            fresh = true;
            return last.Picture;
        }

        fresh = false;
        return Guess(PictureArea(client));
    }

    /// <summary>Forgets the captures (another window).</summary>
    public void Reset() => _last = null;

    private RectD Guess(SizeD client)
    {
        if (client.Width <= 0 || client.Height <= 0)
        {
            return RectD.Empty;
        }

        var aspect = PortraitAspect;
        var shape = Math.Min(client.Width, client.Height) / Math.Max(client.Width, client.Height);
        if (Math.Abs(shape - aspect) <= _options.AspectTolerance * aspect)
        {
            return new RectD(0, 0, client.Width, client.Height); // a window shaped like the picture
        }

        var picture = Fit == PictureFit.Landscape ? new SizeD(1, aspect) : new SizeD(aspect, 1);
        return Geometry.VideoRect(client.Width, client.Height, picture);
    }

    private static bool Close(RectD a, RectD b) =>
        Math.Abs(a.Left - b.Left) < 2 && Math.Abs(a.Top - b.Top) < 2 && Math.Abs(a.Width - b.Width) < 2 && Math.Abs(a.Height - b.Height) < 2;
}
