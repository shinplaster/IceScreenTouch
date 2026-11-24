// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json;
using IceScreenTouch.Api;

namespace IceScreenTouch.App.Services;

/// <summary>
/// The quota of the automation API in the Store trial (docs/API-DESIGN.md, chapter 5): <see cref="DefaultOperationLimit"/> operations
/// and <see cref="DefaultCaptureLimit"/> captures over the whole trial (no reset), and a WebSocket connection open at most
/// <see cref="DefaultStreamLimit"/>. It applies only while <c>isTrial</c> says so: a purchase lifts it at once, a build of your own
/// never has it.
/// <para>
/// The counts are kept in <c>trial-usage.json</c> in the data folder, written after every count, so restarting the app does not reset
/// them. A file that is missing or cannot be read counts from 0 (a user who deletes it gets the quota again; the source is open and a
/// build of your own is unlimited, so this is not defended). A file that cannot be written leaves the counts in memory and says so in
/// the log once. Thread-safe.
/// </para>
/// </summary>
public sealed class TrialApiQuota : IApiQuota
{
    /// <summary>Operations in the whole trial.</summary>
    public const int DefaultOperationLimit = 300;

    /// <summary>Screenshots and text readings in the whole trial.</summary>
    public const int DefaultCaptureLimit = 300;

    /// <summary>The name of the file in the data folder.</summary>
    public const string FileName = "trial-usage.json";

    /// <summary>The longest a WebSocket connection may stay open in the trial.</summary>
    public static readonly TimeSpan DefaultStreamLimit = TimeSpan.FromMinutes(5);

    private readonly Func<bool> _isTrial;
    private readonly Action<string>? _log;
    private readonly Lock _lock = new();
    private readonly int _operationLimit;
    private readonly int _captureLimit;
    private readonly int _streamSeconds;
    private int _operations;
    private int _captures;
    private bool _writeFailed;

    /// <summary>Reads the counts so far (a missing or broken file is 0).</summary>
    /// <param name="dataDirectory">The app's data folder.</param>
    /// <param name="isTrial">True while the quota applies (the Store trial).</param>
    /// <param name="log">The app log (lines start with <c>[api]</c>).</param>
    /// <param name="operationLimit">Operations in the whole trial (tests).</param>
    /// <param name="captureLimit">Captures in the whole trial (tests).</param>
    /// <param name="streamLimit">The longest a WebSocket connection may stay open (tests).</param>
    public TrialApiQuota(string dataDirectory, Func<bool> isTrial, Action<string>? log = null, int operationLimit = DefaultOperationLimit, int captureLimit = DefaultCaptureLimit, TimeSpan? streamLimit = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataDirectory);
        ArgumentNullException.ThrowIfNull(isTrial);
        ArgumentOutOfRangeException.ThrowIfNegative(operationLimit);
        ArgumentOutOfRangeException.ThrowIfNegative(captureLimit);
        Path = System.IO.Path.Combine(dataDirectory, FileName);
        _isTrial = isTrial;
        _log = log;
        _operationLimit = operationLimit;
        _captureLimit = captureLimit;
        _streamSeconds = (int)Math.Max(1, Math.Round((streamLimit ?? DefaultStreamLimit).TotalSeconds));
        (_operations, _captures) = Read();
    }

    /// <summary>Where the counts are kept.</summary>
    public string Path { get; }

    /// <inheritdoc />
    public ApiTrialInfo? Current
    {
        get
        {
            if (!_isTrial())
            {
                return null;
            }

            lock (_lock)
            {
                return new ApiTrialInfo(
                    Math.Max(0, _operationLimit - _operations),
                    _operationLimit,
                    Math.Max(0, _captureLimit - _captures),
                    _captureLimit,
                    _streamSeconds);
            }
        }
    }

    /// <inheritdoc />
    public bool TryTake(ApiQuotaCounter counter, out int limit)
    {
        limit = counter == ApiQuotaCounter.Operations ? _operationLimit : _captureLimit;
        if (!_isTrial())
        {
            return true;
        }

        lock (_lock)
        {
            ref var used = ref counter == ApiQuotaCounter.Operations ? ref _operations : ref _captures;
            if (used >= limit)
            {
                return false;
            }

            used++;
            Write();
            return true;
        }
    }

    private (int Operations, int Captures) Read()
    {
        try
        {
            if (!File.Exists(Path))
            {
                return (0, 0);
            }

            using var document = JsonDocument.Parse(File.ReadAllBytes(Path));
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object ? (Count(root, "operations"), Count(root, "captures")) : Broken();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _log?.Invoke($"[api] cannot read {FileName} ({ex.GetType().Name}); the trial counts start from 0");
            return (0, 0);
        }

        (int, int) Broken()
        {
            _log?.Invoke($"[api] {FileName} is not valid; the trial counts start from 0");
            return (0, 0);
        }
    }

    // A count that is missing, not a whole number or negative is 0.
    private static int Count(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) && n > 0 ? n : 0;

    // Called with the lock held.
    private void Write()
    {
        var temp = Path + ".tmp";
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var json = $"{{\"operations\":{_operations},\"captures\":{_captures}}}\n";
            File.WriteAllBytes(temp, Encoding.UTF8.GetBytes(json));
            File.Move(temp, Path, overwrite: true);
            _writeFailed = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // Overwritten by the next write.
            }

            if (!_writeFailed)
            {
                _writeFailed = true;
                _log?.Invoke($"[api] cannot write {FileName} ({ex.GetType().Name}); the trial counts are kept until the app exits");
            }
        }
    }
}
