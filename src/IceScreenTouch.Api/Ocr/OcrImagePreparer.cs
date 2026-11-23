// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices;
using SkiaSharp;

namespace IceScreenTouch.Api.Ocr;

/// <summary>
/// The image handed to an engine and the way back from its pixels to the picture's (docs/API-DESIGN.md, 16.2.3 and 16.10.2).
/// </summary>
/// <param name="Bgra">Packed, opaque BGRA (alpha 255). A new array for every request: it is never pooled, so an engine call that does not return
/// can never read another request's picture.</param>
/// <param name="Width">Width of the image.</param>
/// <param name="Height">Height of the image.</param>
/// <param name="Region">For an engine that crops itself: the region in pixels of this image; otherwise null.</param>
/// <param name="Source">The rectangle of the picture the image shows (pixels of the picture).</param>
/// <param name="ContentWidth">Width of the part of the image that shows <paramref name="Source"/> (the rest is padding).</param>
/// <param name="ContentHeight">Height of that part.</param>
public sealed record PreparedOcrImage(byte[] Bgra, int Width, int Height, OcrRect? Region, OcrRect Source, int ContentWidth, int ContentHeight)
{
    /// <summary>The scale from picture pixels to image pixels, horizontally.</summary>
    public double ScaleX => ContentWidth / Source.Width;

    /// <summary>The scale from picture pixels to image pixels, vertically.</summary>
    public double ScaleY => ContentHeight / Source.Height;

    /// <summary>
    /// A box in pixels of the image as pixels of the picture: cut to the part that shows the picture (padding is not the picture), then
    /// scaled and moved back. Null when nothing of it is on the picture.
    /// </summary>
    public OcrRect? ToPicture(OcrRect box)
    {
        var left = Math.Max(box.X, 0);
        var top = Math.Max(box.Y, 0);
        var right = Math.Min(box.Right, ContentWidth);
        var bottom = Math.Min(box.Bottom, ContentHeight);
        if (!(right > left) || !(bottom > top))
        {
            return null;
        }

        return new OcrRect(
            Source.X + (left / ScaleX),
            Source.Y + (top / ScaleY),
            (right - left) / ScaleX,
            (bottom - top) / ScaleY);
    }
}

/// <summary>
/// Cuts, scales and pads the picture for an engine (docs/API-DESIGN.md, 16.2.3): the region (with a margin) when the engine does not crop
/// itself, the scale from the engine's <see cref="OcrImageHints"/>, padding up to its smallest size, and always packed, opaque BGRA (the
/// picture's alpha means nothing and its rows may be padded). SkiaSharp, no Windows.
/// </summary>
public static class OcrImagePreparer
{
    /// <summary>The margin around a region that is cut out (pixels of the picture), so letters on its edge are read whole.</summary>
    public const int RegionMargin = 8;

    /// <summary>The most pixels an image for an engine may have (a little more than 8K, as <see cref="PictureLimits.MaxPixels"/>).</summary>
    public const long MaxPixels = PictureLimits.MaxPixels;

    private static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear, SKMipmapMode.None);

    /// <summary>
    /// The scale for an image whose content is <paramref name="width"/> x <paramref name="height"/>: enlarge to
    /// <see cref="OcrImageHints.TargetShortSide"/> (at most <see cref="OcrImageHints.MaxUpscale"/> times), shrink a short side above
    /// <see cref="OcrImageHints.MaxShortSide"/>, then shrink until the long side fits <see cref="OcrImageHints.MaxDimension"/> and the pixels
    /// fit <see cref="MaxPixels"/>.
    /// </summary>
    public static double ScaleFor(int width, int height, OcrImageHints hints)
    {
        ArgumentNullException.ThrowIfNull(hints);
        double shortSide = Math.Min(width, height);
        double longSide = Math.Max(width, height);
        var scale = 1.0;
        if (hints.TargetShortSide is { } target && shortSide < target)
        {
            scale = Math.Min(target / shortSide, Math.Max(hints.MaxUpscale, 1.0));
        }
        else if (hints.MaxShortSide is { } maxShort && shortSide > maxShort)
        {
            scale = maxShort / shortSide;
        }

        if (longSide * scale > hints.MaxDimension)
        {
            scale = hints.MaxDimension / longSide;
        }

        if ((double)width * height * scale * scale > MaxPixels)
        {
            scale = Math.Sqrt(MaxPixels / ((double)width * height));
        }

        return scale;
    }

    /// <summary>Prepares <paramref name="frame"/> for an engine.</summary>
    /// <param name="frame">The picture.</param>
    /// <param name="region">The part to read, in pixels of the picture (checked by the caller: inside the picture, not empty); null = all.</param>
    /// <param name="capabilities">The engine's capabilities and image hints (already within the host's limits, see <see cref="OcrOutputGuard.Sanitize(OcrEngineInfo)"/>).</param>
    /// <exception cref="InvalidOperationException">Skia could not read or scale the picture.</exception>
    public static PreparedOcrImage Prepare(ApiPictureFrame frame, OcrRect? region, OcrCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(capabilities);

        // The rectangle of the picture the engine gets: the region with a margin when the engine does not crop itself, else the whole picture.
        var source = new SKRectI(0, 0, frame.Width, frame.Height);
        if (region is { } r && !capabilities.Region)
        {
            source = SKRectI.Intersect(
                source,
                SKRectI.Create(
                    (int)Math.Floor(r.X) - RegionMargin,
                    (int)Math.Floor(r.Y) - RegionMargin,
                    (int)Math.Ceiling(r.Width) + (2 * RegionMargin) + 1,
                    (int)Math.Ceiling(r.Height) + (2 * RegionMargin) + 1));
            if (source.IsEmpty)
            {
                throw new ArgumentOutOfRangeException(nameof(region), "the region is outside the picture");
            }
        }

        var hints = capabilities.Image;
        var scale = ScaleFor(source.Width, source.Height, hints);
        var contentWidth = Math.Clamp((int)Math.Round(source.Width * scale), 1, hints.MaxDimension);
        var contentHeight = Math.Clamp((int)Math.Round(source.Height * scale), 1, hints.MaxDimension);

        // Too small for the engine: pad right and bottom with the edge pixels (no new edges that could read as a letter).
        var width = Math.Max(contentWidth, hints.MinDimension);
        var height = Math.Max(contentHeight, hints.MinDimension);

        var pixels = Render(frame, source, contentWidth, contentHeight, width, height);
        var sourceRect = new OcrRect(source.Left, source.Top, source.Width, source.Height);
        OcrRect? engineRegion = null;
        if (region is { } whole && capabilities.Region)
        {
            engineRegion = new OcrRect(whole.X * contentWidth / source.Width, whole.Y * contentHeight / source.Height, whole.Width * contentWidth / source.Width, whole.Height * contentHeight / source.Height);
        }

        return new PreparedOcrImage(pixels, width, height, engineRegion, sourceRect, contentWidth, contentHeight);
    }

    private static byte[] Render(ApiPictureFrame frame, SKRectI source, int contentWidth, int contentHeight, int width, int height)
    {
        // Opaque: Skia ignores the alpha byte of the picture (a receiver's window may leave it 0).
        var info = new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        var output = new byte[(long)width * height * 4];
        var handle = GCHandle.Alloc(frame.Bgra, GCHandleType.Pinned);
        var outputHandle = GCHandle.Alloc(output, GCHandleType.Pinned);
        try
        {
            using var bitmap = new SKBitmap();
            if (!bitmap.InstallPixels(info, handle.AddrOfPinnedObject(), frame.Stride))
            {
                throw new InvalidOperationException("the picture could not be read");
            }

            using var image = SKImage.FromPixels(bitmap.PeekPixels())?.Subset(source)
                ?? throw new InvalidOperationException("the picture could not be read");
            var target = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
            using (var surface = SKSurface.Create(target, outputHandle.AddrOfPinnedObject(), width * 4)
                ?? throw new InvalidOperationException("the picture could not be scaled"))
            {
                var matrix = SKMatrix.CreateScale((float)contentWidth / source.Width, (float)contentHeight / source.Height);
                using var shader = image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, Sampling, matrix);
                using var paint = new SKPaint { Shader = shader, BlendMode = SKBlendMode.Src };
                surface.Canvas.DrawRect(0, 0, width, height, paint);
                surface.Canvas.Flush();
            }
        }
        finally
        {
            outputHandle.Free();
            handle.Free();
        }

        // The contract says opaque: whatever Skia wrote into the alpha byte, it is 255 now.
        for (var i = 3; i < output.Length; i += 4)
        {
            output[i] = 255;
        }

        return output;
    }
}
