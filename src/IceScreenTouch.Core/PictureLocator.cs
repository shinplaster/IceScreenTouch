namespace IceScreenTouch.Core;

/// <summary>How a <see cref="PictureEstimate"/> was chosen.</summary>
public enum PictureFit
{
    /// <summary>A portrait picture scaled to fit and centered in the image (letterboxed).</summary>
    Portrait,

    /// <summary>A landscape picture scaled to fit and centered in the image (letterboxed).</summary>
    Landscape,

    /// <summary>The picture is not centered; the measured rectangle is used as it is.</summary>
    Measured,
}

/// <summary>Where the picture is inside a captured window.</summary>
/// <param name="Picture">The picture, relative to the image's top-left corner.</param>
/// <param name="Fit">Which model explains the measurement.</param>
/// <param name="Ambiguous">
/// Both a portrait and a landscape picture explain the image (a dark screen with content only in the middle).
/// <see cref="Fit"/> is then the previous fit, or portrait.
/// </param>
/// <param name="Measured">The bounding box of the content that differs from the background.</param>
public sealed record PictureEstimate(RectD Picture, PictureFit Fit, bool Ambiguous, RectI Measured);

/// <summary>Tuning of <see cref="PictureLocator"/>.</summary>
public sealed record PictureLocatorOptions
{
    /// <summary>Default options.</summary>
    public static PictureLocatorOptions Default { get; } = new();

    /// <summary>A pixel is content when a channel differs from the background color by more than this.</summary>
    public int ColorTolerance { get; init; } = 40;

    /// <summary>
    /// The tolerance when the background is exactly black. Receivers draw their bars as (0,0,0), while the dark
    /// grays of a dark mode screen (about 30) are picture; with <see cref="ColorTolerance"/> they looked like bars.
    /// </summary>
    public int BlackTolerance { get; init; } = 8;

    /// <summary>
    /// A column (row) belongs to the picture when its share of content pixels is at least this fraction of the
    /// fullest column's (row's). Relative, because a row of a portrait picture in a wide window is mostly bar.
    /// Overlays in the bars, such as a receiver's "TRIAL" badge, cover less than this.
    /// </summary>
    public double LineContent { get; init; } = 0.3;

    /// <summary>An image edge is a letterbox bar when at least this share of it has one color.</summary>
    public double BackgroundShare { get; init; } = 0.9;

    /// <summary>Tolerance of the fit models, as a share of the image size (at least 3 pixels).</summary>
    public double FitTolerance { get; init; } = 0.01;

    /// <summary>Content spanning this share of a candidate's width and height settles the fit (no ambiguity).</summary>
    public double FillShare { get; init; } = 0.9;

    /// <summary>A measured rectangle is accepted as it is when its aspect is this close to the phone's (relative).</summary>
    public double AspectTolerance { get; init; } = 0.03;

    /// <summary>
    /// Take the measured content as the picture whenever it has the phone's shape and spans at least half of the
    /// image one way, before trying the centered models. For receivers that draw the picture with uneven margins
    /// (LonelyScreen: 16 px above, 10 below, and 0 to 12 px off center sideways, 2026-11-06), where a centered
    /// picture is a few pixels off. The centered models still answer when the content does not have that shape
    /// (a dark screen).
    /// </summary>
    public bool TrustMeasuredShape { get; init; }

    /// <summary>Roughly how many rows and columns are sampled.</summary>
    public int Samples { get; init; } = 480;
}

/// <summary>
/// Finds the phone picture inside a captured receiver window (for receivers that do not report it). Receivers
/// scale the picture to fit their window and center it, filling the rest with a uniform (black) background.
/// The locator measures the content that differs from the background, then picks the fitted portrait or
/// landscape rectangle that contains it. A dark screen may be explained by both; the previous fit wins then.
/// </summary>
public static class PictureLocator
{
    /// <summary>
    /// Locates the picture in a 32-bit BGRA image. <paramref name="portraitAspect"/> is width / height of the phone
    /// screen in portrait (393 / 852 for an iPhone 15 Pro). Returns null when nothing in the image tells where the
    /// picture is (it is all background, or the content fits no model).
    /// </summary>
    public static PictureEstimate? Locate(
        ReadOnlySpan<byte> bgra,
        int width,
        int height,
        int stride,
        double portraitAspect,
        PictureFit? previous = null,
        PictureLocatorOptions? options = null)
    {
        options ??= PictureLocatorOptions.Default;
        if (width < 8 || height < 8 || stride < width * 4 || bgra.Length < ((height - 1) * stride) + (width * 4) || portraitAspect <= 0)
        {
            return null;
        }

        var image = new Image(bgra, width, height, stride);
        var measured = new RectI(0, 0, width, height);
        if (Background(image, options) is { } background)
        {
            var (columns, rows) = Profiles(image, background, options);
            if (DominantRun(columns, options.LineContent) is not { } x || DominantRun(rows, options.LineContent) is not { } y)
            {
                return null; // only background (or the picture is black)
            }

            measured = new RectI(x.Start, y.Start, x.End - x.Start, y.End - y.Start);
            if (options.TrustMeasuredShape && HasPhoneShape(measured, portraitAspect, options)
                && (measured.Width >= width / 2 || measured.Height >= height / 2))
            {
                return new PictureEstimate(new RectD(measured.Left, measured.Top, measured.Width, measured.Height), PictureFit.Measured, false, measured);
            }
        }

        var tolX = Math.Max(3, options.FitTolerance * width);
        var tolY = Math.Max(3, options.FitTolerance * height);
        var portrait = Geometry.VideoRect(width, height, new SizeD(portraitAspect, 1));
        var landscape = Geometry.VideoRect(width, height, new SizeD(1, portraitAspect));
        var inPortrait = Contains(portrait, measured, tolX, tolY);
        var inLandscape = Contains(landscape, measured, tolX, tolY);

        // Content that fills one candidate decides it, whatever the previous fit was (a rotation back to portrait).
        var fillsPortrait = inPortrait && Fills(measured, portrait, options.FillShare);
        var fillsLandscape = inLandscape && Fills(measured, landscape, options.FillShare);
        if (fillsPortrait != fillsLandscape)
        {
            return fillsPortrait
                ? new PictureEstimate(portrait, PictureFit.Portrait, false, measured)
                : new PictureEstimate(landscape, PictureFit.Landscape, false, measured);
        }

        if (inPortrait && inLandscape)
        {
            // Content that reaches both bars of exactly one candidate band decides it: a landscape picture in a tall
            // window whose sides are dark still spans the band from bar to bar, and a portrait picture would have to
            // be black everywhere but in those very rows (seen on the iPhone with dark mode apps and maps).
            var spansPortrait = SpansBand(measured, portrait, width, height, tolX, tolY);
            var spansLandscape = SpansBand(measured, landscape, width, height, tolX, tolY);
            if (spansPortrait != spansLandscape)
            {
                return spansPortrait
                    ? new PictureEstimate(portrait, PictureFit.Portrait, false, measured)
                    : new PictureEstimate(landscape, PictureFit.Landscape, false, measured);
            }

            var fit = previous == PictureFit.Landscape ? PictureFit.Landscape : PictureFit.Portrait;
            return new PictureEstimate(fit == PictureFit.Landscape ? landscape : portrait, fit, true, measured);
        }

        if (inPortrait || inLandscape)
        {
            return new PictureEstimate(inPortrait ? portrait : landscape, inPortrait ? PictureFit.Portrait : PictureFit.Landscape, false, measured);
        }

        // Not centered (a receiver with its own toolbar, say): trust the measurement if it has the phone's shape.
        if (HasPhoneShape(measured, portraitAspect, options))
        {
            return new PictureEstimate(new RectD(measured.Left, measured.Top, measured.Width, measured.Height), PictureFit.Measured, false, measured);
        }

        return null;
    }

    private static bool HasPhoneShape(RectI r, double portraitAspect, PictureLocatorOptions options)
    {
        if (r.Width <= 0 || r.Height <= 0)
        {
            return false;
        }

        var aspect = (double)Math.Min(r.Width, r.Height) / Math.Max(r.Width, r.Height);
        return Math.Abs(aspect - portraitAspect) <= options.AspectTolerance * portraitAspect;
    }

    // The candidate leaves bars on two opposite sides of the image, and the content touches both of them.
    private static bool SpansBand(RectI measured, RectD candidate, int width, int height, double tolX, double tolY)
    {
        if (candidate.Width < width - (2 * tolX))
        {
            return Math.Abs(measured.Left - candidate.Left) <= tolX
                && Math.Abs(measured.Left + measured.Width - (candidate.Left + candidate.Width)) <= tolX;
        }

        if (candidate.Height < height - (2 * tolY))
        {
            return Math.Abs(measured.Top - candidate.Top) <= tolY
                && Math.Abs(measured.Top + measured.Height - (candidate.Top + candidate.Height)) <= tolY;
        }

        return false; // no bars: nothing to touch
    }

    private static bool Fills(RectI measured, RectD candidate, double share) =>
        measured.Width >= share * candidate.Width && measured.Height >= share * candidate.Height;

    private static bool Contains(RectD outer, RectI inner, double tolX, double tolY) =>
        outer.HasArea
        && inner.Left >= outer.Left - tolX
        && inner.Top >= outer.Top - tolY
        && inner.Left + inner.Width <= outer.Left + outer.Width + tolX
        && inner.Top + inner.Height <= outer.Top + outer.Height + tolY;

    // The letterbox color. A bar covers a whole image edge with one color, and a centered picture has bars on two
    // opposite edges; the picture's own edges are rarely one color, let alone the same one on both sides. So: the
    // color of a pair of opposite one-color edges that agree, else the darkest one-color edge (bars are black).
    private static (int B, int G, int R)? Background(Image image, PictureLocatorOptions options)
    {
        var step = Math.Max(1, Math.Max(image.Width, image.Height) / (options.Samples * 2));
        var top = EdgeColor(image, true, 0, step, options);
        var bottom = EdgeColor(image, true, image.Height - 1, step, options);
        var left = EdgeColor(image, false, 0, step, options);
        var right = EdgeColor(image, false, image.Width - 1, step, options);

        (int B, int G, int R)? best = null;
        foreach (var (a, b) in new[] { (left, right), (top, bottom) })
        {
            if (a is { } ca && b is { } cb && Near(ca, cb, options.ColorTolerance) && (best is null || Luma(ca) < Luma(best.Value)))
            {
                best = ca;
            }
        }

        if (best is not null)
        {
            return best;
        }

        foreach (var c in new[] { top, bottom, left, right })
        {
            if (c is { } color && (best is null || Luma(color) < Luma(best.Value)))
            {
                best = color;
            }
        }

        return best;
    }

    // The dominant color of one image edge, if it covers the edge.
    private static (int B, int G, int R)? EdgeColor(Image image, bool horizontal, int at, int step, PictureLocatorOptions options)
    {
        var counts = new Dictionary<int, (int Count, long B, long G, long R)>();
        var length = horizontal ? image.Width : image.Height;
        var total = 0;
        for (var i = 0; i < length; i += step)
        {
            var (b, g, r) = horizontal ? image.Pixel(i, at) : image.Pixel(at, i);
            var key = ((b >> 4) << 8) | ((g >> 4) << 4) | (r >> 4);
            counts.TryGetValue(key, out var c);
            counts[key] = (c.Count + 1, c.B + b, c.G + g, c.R + r);
            total++;
        }

        var mode = counts.Values.MaxBy(c => c.Count);
        return mode.Count >= options.BackgroundShare * total
            ? ((int)(mode.B / mode.Count), (int)(mode.G / mode.Count), (int)(mode.R / mode.Count))
            : null;
    }

    private static bool Near((int B, int G, int R) a, (int B, int G, int R) b, int tolerance) =>
        Math.Abs(a.B - b.B) <= tolerance && Math.Abs(a.G - b.G) <= tolerance && Math.Abs(a.R - b.R) <= tolerance;

    private static int Luma((int B, int G, int R) c) => (c.R * 3) + (c.G * 6) + c.B;

    // Share of content pixels per column and per row (sampled).
    private static (double[] Columns, double[] Rows) Profiles(Image image, (int B, int G, int R) background, PictureLocatorOptions options)
    {
        var columns = new double[image.Width];
        var rows = new double[image.Height];
        var stepX = Math.Max(1, image.Width / options.Samples);
        var stepY = Math.Max(1, image.Height / options.Samples);
        var columnSamples = (image.Height + stepY - 1) / stepY;
        var rowSamples = (image.Width + stepX - 1) / stepX;
        var tol = background == (0, 0, 0) ? options.BlackTolerance : options.ColorTolerance;

        // Every column against sampled rows, and every row against sampled columns, so that edges are exact.
        for (var y = 0; y < image.Height; y++)
        {
            var sampledRow = y % stepY == 0;
            var line = image.Row(y);
            var rowCount = 0;
            var advance = sampledRow ? 1 : stepX; // other rows only need the sampled columns
            for (var x = 0; x < image.Width; x += advance)
            {
                var sampledColumn = x % stepX == 0;
                var i = x * 4;
                var content = Math.Abs(line[i] - background.B) > tol
                    || Math.Abs(line[i + 1] - background.G) > tol
                    || Math.Abs(line[i + 2] - background.R) > tol;
                if (!content)
                {
                    continue;
                }

                if (sampledRow)
                {
                    columns[x]++;
                }

                if (sampledColumn)
                {
                    rowCount++;
                }
            }

            rows[y] = (double)rowCount / rowSamples;
        }

        for (var x = 0; x < image.Width; x++)
        {
            columns[x] /= columnSamples;
        }

        return (columns, rows);
    }

    private readonly record struct Run(int Start, int End, double Mass);

    // The run of lines at or above the threshold with the most content. Short gaps (dark stripes inside the
    // picture) are bridged; a separate overlay in a bar is a different run and loses.
    private static Run? DominantRun(double[] profile, double relativeThreshold)
    {
        var max = profile.Max();
        if (max <= 0)
        {
            return null;
        }

        var threshold = relativeThreshold * max;
        var gap = Math.Max(2, profile.Length / 50);
        Run? best = null;
        var start = -1;
        var end = -1;
        var mass = 0.0;
        for (var i = 0; i <= profile.Length; i++)
        {
            var on = i < profile.Length && profile[i] >= threshold;
            if (on)
            {
                if (start < 0 || i - end > gap)
                {
                    Close();
                    start = i;
                    mass = 0;
                }

                end = i + 1;
                mass += profile[i];
            }
        }

        Close();
        return best;

        void Close()
        {
            if (start >= 0 && (best is null || mass > best.Value.Mass))
            {
                best = new Run(start, end, mass);
            }
        }
    }

    private readonly ref struct Image(ReadOnlySpan<byte> data, int width, int height, int stride)
    {
        private readonly ReadOnlySpan<byte> _data = data;

        public int Width { get; } = width;

        public int Height { get; } = height;

        public ReadOnlySpan<byte> Row(int y) => _data.Slice(y * stride, Width * 4);

        public (int B, int G, int R) Pixel(int x, int y)
        {
            var i = (y * stride) + (x * 4);
            return (_data[i], _data[i + 1], _data[i + 2]);
        }
    }
}
