// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using IceScreenTouch.Api.Ocr;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using WinOcrEngine = Windows.Media.Ocr.OcrEngine;

namespace IceScreenTouch.Platform.Windows;

/// <summary>
/// The built-in OCR engine: Windows.Media.Ocr, part of Windows (docs/API-DESIGN.md, 16.2 and 16.10). It recognizes on this PC with the OCR
/// languages Windows has installed; nothing is shipped with the application. The Windows types stay in this class.
/// </summary>
/// <remarks>
/// The languages are read again on every request (a language can be installed while the application runs). A recognizer is kept per language
/// tag. Calls to <see cref="RecognizeAsync"/> never overlap (the host allows one recognition at a time); the other members may be called meanwhile.
/// </remarks>
public sealed class WindowsMediaOcrEngine : IOcrEngine
{
    /// <summary>The engine's id in the API.</summary>
    public const string EngineId = "windows";

    /// <summary>The short side the picture is enlarged to (the spike: small text needs it; 16.2.3).</summary>
    public const int TargetShortSide = 1300;

    /// <summary>Enlarge at most this many times.</summary>
    public const double MaxUpscale = 3;

    /// <summary>A short side above this is shrunk to it (keeps the time and memory of a large picture known).</summary>
    public const int MaxShortSide = 2600;

    /// <summary>
    /// The shortest side handed to the recognizer (smaller images are padded by the host). Windows.Media.Ocr reads nothing in a very small
    /// image; this keeps a small region readable.
    /// </summary>
    public const int MinDimension = 64;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, WinOcrEngine> _recognizers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The engine, or null when Windows.Media.Ocr cannot be used on this system (for example an edition without the media features): OCR is then
    /// unavailable instead of failing every request.
    /// </summary>
    public static WindowsMediaOcrEngine? TryCreate()
    {
        try
        {
            var engine = new WindowsMediaOcrEngine();
            _ = WinOcrEngine.AvailableRecognizerLanguages.Count;
            return engine;
        }
        catch (Exception ex) when (ex is COMException or TypeLoadException or InvalidCastException or PlatformNotSupportedException or TypeInitializationException)
        {
            return null;
        }
    }

    /// <summary>Creates the engine (throws when Windows.Media.Ocr cannot be used; see <see cref="TryCreate"/>).</summary>
    public WindowsMediaOcrEngine()
    {
        var os = Environment.OSVersion.Version;
        Info = new OcrEngineInfo(
            EngineId,
            "Windows.Media.Ocr",
            $"{os.Major}.{os.Minor}.{os.Build}",
            OcrEngineKind.BuiltIn,
            new OcrCapabilities(
                WordBoxes: true,
                Confidence: false,
                Region: false,
                new OcrImageHints(TargetShortSide, MaxUpscale, MaxShortSide, (int)Math.Min(WinOcrEngine.MaxImageDimension, int.MaxValue), MinDimension)));
    }

    /// <inheritdoc/>
    public OcrEngineInfo Info { get; }

    /// <inheritdoc/>
    public ValueTask<OcrLanguageSet> GetLanguagesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var available = WinOcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToList();
        return ValueTask.FromResult(new OcrLanguageSet(available, DefaultTag(available)));
    }

    /// <inheritdoc/>
    public ValueTask<string?> ResolveLanguageAsync(string? tag, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (tag is null)
        {
            return ValueTask.FromResult(DefaultTag(WinOcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToList()));
        }

        return ValueTask.FromResult(ToLanguage(tag) is { } language && WinOcrEngine.IsLanguageSupported(language) ? language.LanguageTag : null);
    }

    /// <inheritdoc/>
    public async Task<OcrRecognition> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var recognizer = Recognizer(request.Language) ?? throw new OcrEngineException(OcrFailure.LanguageUnavailable);
        var segment = MemoryMarshal.TryGetArray(request.Bgra, out var s) ? s : new ArraySegment<byte>(request.Bgra.ToArray());

        // The bitmap is a copy: the request's pixels are not used after this line.
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
            segment.Array!.AsBuffer(segment.Offset, segment.Count), BitmapPixelFormat.Bgra8, request.Width, request.Height, BitmapAlphaMode.Premultiplied);
        var result = await recognizer.RecognizeAsync(bitmap).AsTask(cancellationToken).ConfigureAwait(false);

        // When Windows finds the text skewed (it does on images with few words), the boxes are in the deskewed image: turned back here.
        var unskew = Unskew(result.TextAngle ?? 0, request.Width, request.Height);
        List<OcrLine> lines = [];
        foreach (var line in result.Lines)
        {
            var words = line.Words
                .Select(w => new OcrWord(w.Text, unskew(new OcrRect(w.BoundingRect.X, w.BoundingRect.Y, w.BoundingRect.Width, w.BoundingRect.Height)), null))
                .ToList();
            if (words.Count == 0)
            {
                continue;
            }

            // Windows gives a line no box: it is the words' (the host checks it again).
            var box = words.Skip(1).Aggregate(words[0].Box, (a, w) => a.Union(w.Box));
            lines.Add(new OcrLine(line.Text, box, words, null));
        }

        return new OcrRecognition(recognizer.RecognizerLanguage.LanguageTag, lines);
    }

    /// <summary>
    /// Maps a box of the deskewed image back to the image: Windows reports <c>TextAngle</c>, the clockwise rotation of the text around the image's
    /// centre, and gives the boxes as if the image had been turned back by it. The box's corners are turned by the angle (clockwise, y down) and
    /// the box that holds them is the answer (the contract asks for axis-aligned boxes).
    /// </summary>
    internal static Func<OcrRect, OcrRect> Unskew(double angleDegrees, int width, int height)
    {
        if (!double.IsFinite(angleDegrees) || Math.Abs(angleDegrees) < 0.01)
        {
            return box => box;
        }

        var radians = angleDegrees * Math.PI / 180;
        var (sin, cos) = Math.SinCos(radians);
        var (cx, cy) = (width / 2.0, height / 2.0);
        return box =>
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var (x, y) in new[] { (box.X, box.Y), (box.Right, box.Y), (box.X, box.Bottom), (box.Right, box.Bottom) })
            {
                var (dx, dy) = (x - cx, y - cy);
                var rx = cx + (dx * cos) - (dy * sin);
                var ry = cy + (dx * sin) + (dy * cos);
                (minX, minY, maxX, maxY) = (Math.Min(minX, rx), Math.Min(minY, ry), Math.Max(maxX, rx), Math.Max(maxY, ry));
            }

            return new OcrRect(minX, minY, maxX - minX, maxY - minY);
        };
    }

    // The user's languages first (the first that has OCR), else the first installed OCR language.
    private static string? DefaultTag(List<string> available) =>
        WinOcrEngine.TryCreateFromUserProfileLanguages()?.RecognizerLanguage.LanguageTag ?? (available.Count > 0 ? available[0] : null);

    // A well-formed tag that Windows does not take as a language (Language throws) is a language it does not have, never an error.
    private static Language? ToLanguage(string tag)
    {
        try
        {
            return new Language(tag);
        }
        catch (Exception ex) when (ex is ArgumentException or COMException or FormatException)
        {
            return null;
        }
    }

    private WinOcrEngine? Recognizer(string tag)
    {
        lock (_gate)
        {
            if (_recognizers.TryGetValue(tag, out var cached))
            {
                return cached;
            }
        }

        if (ToLanguage(tag) is not { } language || WinOcrEngine.TryCreateFromLanguage(language) is not { } created)
        {
            return null;
        }

        lock (_gate)
        {
            _recognizers[tag] = created;
        }

        return created;
    }
}
