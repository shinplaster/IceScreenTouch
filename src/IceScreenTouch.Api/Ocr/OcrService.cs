// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.Core;

namespace IceScreenTouch.Api.Ocr;

/// <summary>An engine and the language a request will use.</summary>
/// <param name="Engine">The engine.</param>
/// <param name="Info">Its description (checked, see <see cref="OcrOutputGuard.Sanitize(OcrEngineInfo)"/>).</param>
/// <param name="Language">The tag the engine resolved.</param>
public sealed record OcrTarget(IOcrEngine Engine, OcrEngineInfo Info, string Language);

/// <summary>Why OCR cannot serve a request (409 <c>ocr_unavailable</c>, docs/API-DESIGN.md 16.5.6).</summary>
/// <param name="Reason"><c>language</c> or <c>engine</c>.</param>
/// <param name="EngineId">The engine's id (null when there is no engine at all).</param>
/// <param name="Available">The languages of that engine (empty for <c>engine</c>).</param>
/// <param name="Engines">The ids of the engines that can be used.</param>
public sealed record OcrUnavailable(string Reason, string? EngineId, IReadOnlyList<string> Available, IReadOnlyList<string> Engines)
{
    /// <summary>The reason for a missing language.</summary>
    public const string LanguageReason = "language";

    /// <summary>The reason for a missing or stopped engine.</summary>
    public const string EngineReason = "engine";
}

/// <summary>Thrown by <see cref="OcrService"/> when OCR cannot serve the request; the controller answers 409 <c>ocr_unavailable</c>.</summary>
public sealed class OcrUnavailableException(OcrUnavailable details) : Exception("OCR is not available for this request")
{
    /// <summary>What is missing.</summary>
    public OcrUnavailable Details { get; } = details;
}

/// <summary>The text of a picture: lines in reading order, in pixels of the picture.</summary>
/// <param name="Language">The language the recognizer used.</param>
/// <param name="Engine">The engine that read it.</param>
/// <param name="Lines">The lines (only those whose centre is inside the region, when one was given).</param>
public sealed record OcrReading(string Language, OcrEngineInfo Engine, IReadOnlyList<TextLine> Lines);

/// <summary>One engine of the status.</summary>
/// <param name="Info">Its description.</param>
/// <param name="Available">It can be used now (false while a call to it has not returned for <see cref="OcrService.StuckAfter"/>).</param>
public sealed record OcrEngineState(OcrEngineInfo Info, bool Available);

/// <summary>The OCR part of <c>GET /v1/status</c> (docs/API-DESIGN.md, 16.10.4).</summary>
/// <param name="Available">The default engine can be used and has a language.</param>
/// <param name="Engine">The default engine, or null when there is none.</param>
/// <param name="Engines">Every engine.</param>
/// <param name="Languages">The default engine's languages.</param>
/// <param name="DefaultLanguage">The default engine's default language.</param>
public sealed record OcrStatus(bool Available, OcrEngineInfo? Engine, IReadOnlyList<OcrEngineState> Engines, IReadOnlyList<string> Languages, string? DefaultLanguage);

/// <summary>
/// The engine-independent OCR pipeline (docs/API-DESIGN.md, 16.10.3): chooses the engine, resolves the language, prepares the picture, calls the
/// engine, checks its output, maps the boxes back to the picture and orders the lines. It also owns the recognition turn (16.7.1): one recognition at
/// a time over all engines, at most <see cref="MaxWaitingRecognitions"/> waiting; the turn is given back only when the engine's call has really
/// ended, or after <see cref="StuckAfter"/>, when that engine is set aside as stopped until its call returns.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "The semaphore never creates a wait handle, and an engine call that outlives the service still releases it.")]
public sealed class OcrService
{
    /// <summary>Requests that may wait for the turn behind the running recognition; one more is refused (429).</summary>
    public const int MaxWaitingRecognitions = 2;

    /// <summary>An engine call that has not returned after this long no longer holds the turn, and its engine is unavailable until it returns.</summary>
    public static readonly TimeSpan StuckAfter = TimeSpan.FromSeconds(30);

    private readonly List<(IOcrEngine Engine, OcrEngineInfo Info)> _engines;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _turn = new(1, 1);
    private readonly Lock _gate = new();
    private readonly Dictionary<string, int> _stuckCalls = new(StringComparer.Ordinal);
    private int _inTurn; // holding or waiting for the turn

    /// <summary>Creates the service.</summary>
    /// <param name="engines">The engines; the first usable one is the default. One with a malformed id, or an id that was already used, is left out.</param>
    /// <param name="time">The clock of <see cref="StuckAfter"/>.</param>
    public OcrService(IEnumerable<IOcrEngine> engines, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(engines);
        List<(IOcrEngine, OcrEngineInfo)> usable = [];
        foreach (var engine in engines)
        {
            if (engine is not null && OcrOutputGuard.Sanitize(engine.Info) is { } info && usable.All(e => e.Item2.Id != info.Id))
            {
                usable.Add((engine, info));
            }
        }

        _engines = usable;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The engines (checked descriptions), the default first.</summary>
    public IReadOnlyList<OcrEngineInfo> Engines => [.. _engines.Select(e => e.Info)];

    /// <summary>The ids of the engines that can be used now.</summary>
    public IReadOnlyList<string> AvailableEngineIds => [.. _engines.Where(e => !IsStuck(e.Info.Id)).Select(e => e.Info.Id)];

    /// <summary>
    /// The engine (<paramref name="engineId"/> null = the default) and the language (<paramref name="language"/> null = the engine's default) of a
    /// request. Both must be well formed (checked by the caller).
    /// </summary>
    /// <exception cref="OcrUnavailableException">No such engine, the engine is stopped, or it has no such language.</exception>
    /// <exception cref="OcrEngineException">The engine failed while it was asked.</exception>
    public async ValueTask<OcrTarget> ResolveAsync(string? engineId, string? language, CancellationToken cancellationToken)
    {
        var (engine, info) = Find(engineId);
        var tag = await engine.ResolveLanguageAsync(language, cancellationToken).ConfigureAwait(false);
        if (tag is null || !OcrNames.IsWellFormedLanguageTag(tag))
        {
            var languages = await engine.GetLanguagesAsync(cancellationToken).ConfigureAwait(false);
            throw new OcrUnavailableException(new OcrUnavailable(
                OcrUnavailable.LanguageReason, info.Id, [.. (languages?.Available ?? []).Where(OcrNames.IsWellFormedLanguageTag)], AvailableEngineIds));
        }

        return new OcrTarget(engine, info, tag);
    }

    /// <summary>The OCR part of the status. Never throws for an engine's failure (the engine is then shown as without languages).</summary>
    public async ValueTask<OcrStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var states = _engines.Select(e => new OcrEngineState(e.Info, !IsStuck(e.Info.Id))).ToList();
        if (_engines.Count == 0)
        {
            return new OcrStatus(false, null, states, [], null);
        }

        var (engine, info) = _engines[0];
        OcrLanguageSet? languages = null;
        if (states[0].Available)
        {
            try
            {
                languages = await engine.GetLanguagesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                languages = null;
            }
        }

        var available = (languages?.Available ?? []).Where(OcrNames.IsWellFormedLanguageTag).ToList();
        var fallback = languages?.Default is { } d && OcrNames.IsWellFormedLanguageTag(d) ? d : null;
        return new OcrStatus(states[0].Available && available.Count > 0, info, states, available, fallback);
    }

    /// <summary>
    /// Takes the recognition turn, waiting behind the running one; null when <see cref="MaxWaitingRecognitions"/> are already waiting (the caller
    /// answers 429 or tries again later). Take it before capturing the picture, so no picture is held while waiting.
    /// </summary>
    public async ValueTask<RecognitionTurn?> EnterTurnAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _inTurn) > 1 + MaxWaitingRecognitions)
        {
            _ = Interlocked.Decrement(ref _inTurn);
            return null;
        }

        try
        {
            await _turn.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _ = Interlocked.Decrement(ref _inTurn);
            throw;
        }

        return new RecognitionTurn(this);
    }

    /// <summary>
    /// Reads the text of <paramref name="frame"/> with the turn held: prepares the picture, calls the engine (the turn goes with the call, see
    /// <see cref="RecognitionTurn"/>), checks the output and maps it back to the picture.
    /// </summary>
    /// <param name="turn">The turn (one recognition per turn).</param>
    /// <param name="target">The engine and the language.</param>
    /// <param name="frame">The picture.</param>
    /// <param name="region">The part to read in pixels of the picture (inside it, not empty), or null.</param>
    /// <param name="cancellationToken">Ends the wait for the engine (the engine's call may go on; the turn waits for it).</param>
    /// <exception cref="OcrUnavailableException">The engine was set aside as stopped.</exception>
    /// <exception cref="OcrEngineException">The engine named a failure.</exception>
    public async Task<OcrReading> RecognizeAsync(RecognitionTurn turn, OcrTarget target, ApiPictureFrame frame, OcrRect? region, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(turn);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(frame);
        if (IsStuck(target.Info.Id))
        {
            throw new OcrUnavailableException(new OcrUnavailable(OcrUnavailable.EngineReason, target.Info.Id, [], AvailableEngineIds));
        }

        var prepared = OcrImagePreparer.Prepare(frame, region, target.Info.Capabilities);
        var request = new OcrRequest(prepared.Bgra, prepared.Width, prepared.Height, target.Language, prepared.Region);

        // Started on the thread pool: an engine that blocks before it returns its task must not block the request.
        var engine = target.Engine;
        var call = Task.Run(() => engine.RecognizeAsync(request, cancellationToken), CancellationToken.None);
        turn.HandOver(call, target.Info.Id);
        var recognition = await call.WaitAsync(cancellationToken).ConfigureAwait(false);
        return ToReading(OcrOutputGuard.Sanitize(recognition, prepared.Width, prepared.Height), prepared, target.Info, region);
    }

    /// <summary>The engine's output (checked) as lines of the picture in reading order, only those whose centre is inside the region.</summary>
    internal static OcrReading ToReading(OcrRecognition recognition, PreparedOcrImage prepared, OcrEngineInfo info, OcrRect? region)
    {
        List<TextLine> lines = [];
        foreach (var line in recognition.Lines)
        {
            List<TextWord> words = [];
            foreach (var word in line.Words)
            {
                if (prepared.ToPicture(word.Box) is { } box)
                {
                    words.Add(new TextWord(word.Text, box, word.Confidence));
                }
            }

            if (line.Words.Count > 0 && words.Count == 0)
            {
                continue; // every word was on the padding
            }

            var lineBox = words.Count > 0 ? words.Skip(1).Aggregate(words[0].Box, (a, w) => a.Union(w.Box)) : prepared.ToPicture(line.Box);
            if (lineBox is not { } b)
            {
                continue;
            }

            if (region is { } r && !(b.CenterX >= r.X && b.CenterX <= r.Right && b.CenterY >= r.Y && b.CenterY <= r.Bottom))
            {
                continue;
            }

            lines.Add(TextLayout.CreateLine(line.Text, b, words, line.Confidence));
        }

        return new OcrReading(recognition.Language, info, TextLayout.Order(lines));
    }

    private (IOcrEngine Engine, OcrEngineInfo Info) Find(string? engineId)
    {
        if (_engines.Count == 0)
        {
            throw new OcrUnavailableException(new OcrUnavailable(OcrUnavailable.EngineReason, null, [], []));
        }

        var found = engineId is null ? _engines[0] : _engines.FirstOrDefault(e => e.Info.Id == engineId);
        if (found.Engine is null || IsStuck(found.Info.Id))
        {
            throw new OcrUnavailableException(new OcrUnavailable(OcrUnavailable.EngineReason, engineId ?? found.Info?.Id, [], AvailableEngineIds));
        }

        return found;
    }

    private bool IsStuck(string id)
    {
        lock (_gate)
        {
            return _stuckCalls.GetValueOrDefault(id) > 0;
        }
    }

    private void CountStuck(string id, int delta)
    {
        lock (_gate)
        {
            var count = _stuckCalls.GetValueOrDefault(id) + delta;
            if (count > 0)
            {
                _stuckCalls[id] = count;
            }
            else
            {
                _ = _stuckCalls.Remove(id);
            }
        }
    }

    private void ExitTurn()
    {
        _ = Interlocked.Decrement(ref _inTurn);
        _ = _turn.Release();
    }

    // Gives the turn back when the engine's call ends, or after StuckAfter (the engine is then stopped until the call returns).
    private async Task ReleaseAfterAsync(RecognitionTurn turn, Task call, string engineId)
    {
        var finished = await TaskWait.CompletesWithinAsync(call, StuckAfter, _time).ConfigureAwait(false);
        if (finished)
        {
            _ = call.Exception; // observed here when the request stopped waiting for it
            turn.ReleaseOnce();
            return;
        }

        CountStuck(engineId, 1);
        turn.ReleaseOnce();
        try
        {
            await call.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The request that made the call has long been answered.
        }
        finally
        {
            CountStuck(engineId, -1);
        }
    }

    /// <summary>
    /// The right to run one recognition. Dispose it when the request ends: if an engine call was started with it, the turn is given back only
    /// when that call ends (or is set aside as stopped), never earlier.
    /// </summary>
    public sealed class RecognitionTurn : IDisposable
    {
        private readonly OcrService _owner;
        private int _released;
        private int _handedOver;

        internal RecognitionTurn(OcrService owner) => _owner = owner;

        /// <summary>Gives the turn back unless an engine call holds it.</summary>
        public void Dispose()
        {
            if (Volatile.Read(ref _handedOver) == 0)
            {
                ReleaseOnce();
            }
        }

        internal void HandOver(Task call, string engineId)
        {
            if (Interlocked.Exchange(ref _handedOver, 1) != 0)
            {
                throw new InvalidOperationException("a turn runs one recognition");
            }

            _ = _owner.ReleaseAfterAsync(this, call, engineId);
        }

        internal void ReleaseOnce()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _owner.ExitTurn();
            }
        }
    }
}
