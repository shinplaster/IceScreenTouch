// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Text.Json;
using IceScreenTouch.Api.Ocr;
using IceScreenTouch.Core;

namespace IceScreenTouch.Api.Tests.Ocr;

/// <summary>What a fake engine saw of one call.</summary>
internal sealed record SeenRequest(int Width, int Height, string Language, OcrRect? Region, bool Packed, bool Opaque, byte[] Copy);

/// <summary>
/// A scripted OCR engine (docs/API-DESIGN.md, 16.10.5): each call takes the next step of the script (a result, a failure, a wait on the fake
/// clock, or a call that never returns until released). It records what it was given and whether its token was cancelled.
/// </summary>
internal sealed class FakeOcrEngine : IOcrEngine
{
    private readonly ConcurrentQueue<Func<OcrRequest, CancellationToken, Task<OcrRecognition>>> _script = new();
    private int _running;

    public FakeOcrEngine(string id = "fake", OcrCapabilities? capabilities = null, IReadOnlyList<string>? languages = null, string? defaultLanguage = "ja")
    {
        Info = new OcrEngineInfo(id, "Fake OCR", "1.0", OcrEngineKind.BuiltIn, capabilities ?? Capabilities());
        Languages = languages ?? ["en-US", "ja"];
        DefaultLanguage = defaultLanguage;
    }

    public OcrEngineInfo Info { get; set; }

    public IReadOnlyList<string> Languages { get; set; }

    public string? DefaultLanguage { get; set; }

    /// <summary>The result returned when the script is empty.</summary>
    public OcrRecognition Fallback { get; set; } = new("ja", []);

    public SyncList<SeenRequest> Requests { get; } = [];

    public int Calls => Requests.Count;

    /// <summary>The most calls that ran at the same time.</summary>
    public int MostAtOnce { get; private set; }

    public bool SawCancellation { get; private set; }

    public static OcrCapabilities Capabilities(bool wordBoxes = true, bool confidence = false, bool region = false, OcrImageHints? hints = null) =>
        new(wordBoxes, confidence, region, hints ?? new OcrImageHints(null, 1, null, 10_000, 1));

    public void Returns(OcrRecognition result) => _script.Enqueue((_, _) => Task.FromResult(result));

    public void Fails(Exception exception) => _script.Enqueue((_, _) => Task.FromException<OcrRecognition>(exception));

    /// <summary>The next call waits on the given task (honouring the token unless <paramref name="ignoreCancellation"/>).</summary>
    public void Waits(Task<OcrRecognition> until, bool ignoreCancellation = false) =>
        _script.Enqueue(async (_, ct) =>
        {
            if (ignoreCancellation)
            {
                return await until.ConfigureAwait(false);
            }

            await using var registration = ct.Register(() => SawCancellation = true);
            return await until.WaitAsync(ct).ConfigureAwait(false);
        });

    /// <summary>When set, the language questions wait for it (an engine that does not answer).</summary>
    public Task? QueriesWaitFor { get; set; }

    public async ValueTask<OcrLanguageSet> GetLanguagesAsync(CancellationToken cancellationToken)
    {
        if (QueriesWaitFor is { } wait)
        {
            await wait.ConfigureAwait(false);
        }

        return new OcrLanguageSet(Languages, DefaultLanguage);
    }

    public async ValueTask<string?> ResolveLanguageAsync(string? tag, CancellationToken cancellationToken)
    {
        if (QueriesWaitFor is { } wait)
        {
            await wait.ConfigureAwait(false);
        }

        return ResolveNow(tag);
    }

    private string? ResolveNow(string? tag) => (tag is null ? DefaultLanguage : Languages.FirstOrDefault(l => string.Equals(l, tag, StringComparison.OrdinalIgnoreCase)));

    public async Task<OcrRecognition> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken)
    {
        var now = Interlocked.Increment(ref _running);
        MostAtOnce = Math.Max(MostAtOnce, now);
        try
        {
            var bytes = request.Bgra.ToArray();
            var opaque = true;
            for (var i = 3; i < bytes.Length; i += 4)
            {
                opaque &= bytes[i] == 255;
            }

            Requests.Add(new SeenRequest(request.Width, request.Height, request.Language, request.Region, bytes.Length == request.Width * request.Height * 4, opaque, bytes));
            return _script.TryDequeue(out var step) ? await step(request, cancellationToken).ConfigureAwait(false) : Fallback;
        }
        finally
        {
            _ = Interlocked.Decrement(ref _running);
        }
    }
}

/// <summary>Replays a recognition recorded by the spike (words and boxes only, no image; docs/API-DESIGN.md 16.10.5).</summary>
internal static class SpikeFixtures
{
    public static readonly OcrEngineInfo Info = new("recorded", "Recorded", "spike", OcrEngineKind.BuiltIn, FakeOcrEngine.Capabilities());

    public static readonly string[] HomeScreen = ["メモ", "天気", "株価", "マップ", "写真", "ミュージック", "ホーム", "ファイル", "検索"];

    /// <summary>The text a person reads on each picture (the time first), as the spike counted.</summary>
    public static string[] Truth(string image) => image switch
    {
        "ja-dark-mirror-uxplay" => ["22:23", .. HomeScreen],
        "en-dark-mirror-uxplay" => ["22:36", .. HomeScreen],
        "en-dark-typing" => ["22:34", "IceScreen Touch", "Typed with the PC keyboard."],
        "ja-dark-typing" => ["22:20", "IceScreen Touchで", "PCのキーボードから入力しています。"],
        _ => throw new ArgumentOutOfRangeException(nameof(image)),
    };

    /// <summary>The recorded recognition and the size of the image it was made from.</summary>
    public static (OcrRecognition Recognition, int Width, int Height) Load(string name)
    {
        using var stream = typeof(SpikeFixtures).Assembly.GetManifestResourceStream($"IceScreenTouch.Api.Tests.Ocr.Fixtures.{name}.json")
            ?? throw new InvalidOperationException($"no fixture {name}");
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        List<OcrLine> lines = [];
        foreach (var line in root.GetProperty("lines").EnumerateArray())
        {
            var words = line.GetProperty("words").EnumerateArray().Select(w =>
            {
                var b = w.GetProperty("box").EnumerateArray().Select(v => v.GetDouble()).ToArray();
                return new OcrWord(w.GetProperty("text").GetString()!, new OcrRect(b[0], b[1], b[2], b[3]), null);
            }).ToList();
            var box = words.Skip(1).Aggregate(words[0].Box, (a, w) => a.Union(w.Box));
            lines.Add(new OcrLine(string.Join(' ', words.Select(w => w.Text)), box, words, null));
        }

        return (new OcrRecognition(root.GetProperty("language").GetString()!, lines), root.GetProperty("width").GetInt32(), root.GetProperty("height").GetInt32());
    }

    /// <summary>The recorded lines as the host orders them (boxes as recorded: the image is taken as the picture).</summary>
    public static IReadOnlyList<TextLine> Lines(string name)
    {
        var (recognition, width, height) = Load(name);
        var prepared = new PreparedOcrImage([], width, height, null, new OcrRect(0, 0, width, height), width, height);
        return OcrService.ToReading(OcrOutputGuard.Sanitize(recognition, width, height), prepared, Info, null).Lines;
    }

    public static string Image(string name) => name[..name.IndexOf("__", StringComparison.Ordinal)];

    public static IEnumerable<string> Names => typeof(SpikeFixtures).Assembly.GetManifestResourceNames()
        .Where(n => n.StartsWith("IceScreenTouch.Api.Tests.Ocr.Fixtures.", StringComparison.Ordinal))
        .Select(n => n["IceScreenTouch.Api.Tests.Ocr.Fixtures.".Length..^".json".Length]);
}

/// <summary>Builds lines and pictures for the tests.</summary>
internal static class OcrBuild
{
    /// <summary>A line of words placed left to right: each word <c>(text, x, width)</c> on a row at <paramref name="y"/> of height <paramref name="height"/>.</summary>
    public static TextLine Line(double y, double height, params (string Text, double X, double Width)[] words)
    {
        var list = words.Select(w => new TextWord(w.Text, new OcrRect(w.X, y, w.Width, height), null)).ToList();
        var box = list.Skip(1).Aggregate(list[0].Box, (a, w) => a.Union(w.Box));
        return TextLayout.CreateLine(string.Join(' ', words.Select(w => w.Text)), box, list, null);
    }

    /// <summary>A picture of the given size filled with one BGRA colour, rows padded to <paramref name="stride"/>.</summary>
    public static ApiPictureFrame Frame(int width, int height, int? stride = null, byte alpha = 0, (byte B, byte G, byte R)? color = null, bool isLandscape = false)
    {
        var s = stride ?? (width * 4);
        var bytes = new byte[s * height];
        var (b, g, r) = color ?? ((byte)200, (byte)100, (byte)50);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * s) + (x * 4);
                bytes[i] = b;
                bytes[i + 1] = g;
                bytes[i + 2] = r;
                bytes[i + 3] = alpha;
            }
        }

        return new ApiPictureFrame(bytes, width, height, s, isLandscape, isLandscape ? Rotation.R90 : Rotation.R0);
    }

    public static OcrRecognition Recognition(params OcrLine[] lines) => new("ja", lines);

    public static OcrLine EngineLine(string text, OcrRect box, params OcrWord[] words) => new(text, box, words, null);
}

