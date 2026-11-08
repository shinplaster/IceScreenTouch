using System.Diagnostics;
using System.Globalization;
using System.Text;
using IceScreenTouch.Core;
using IceScreenTouch.Platform.Windows;

namespace IceScreenTouch.Video;

/// <summary>How to start uxplay.exe.</summary>
public sealed record UxPlayOptions
{
    /// <summary>Path of uxplay.exe. The patched build (poc/poc1-uxplay/patches) reports the real picture size.</summary>
    public required string ExePath { get; init; }

    /// <summary>AirPlay name shown on the iPhone.</summary>
    public string Name { get; init; } = "IceScreen Touch";

    /// <summary>GStreamer video sink.</summary>
    public string Sink { get; init; } = DefaultSink;

    /// <summary>
    /// GStreamer H.264 decoder (uxplay -vd), or null for uxplay's default (decodebin). Defaults to the decoder that
    /// matches <see cref="Sink"/> with <c>compliance=flexible</c>: in the default (auto) mode the decoder held 7-8
    /// frames back for reordering, which made the picture 255 ms late instead of 60 ms (2026-11-06, iPhone 15 Pro).
    /// decodebin cannot take that property.
    /// </summary>
    public string? Decoder { get; init; } = DecoderFor(DefaultSink);

    /// <summary>GStreamer video converter (uxplay -vc), or null for uxplay's default (videoconvert, on the CPU).</summary>
    public string? Converter { get; init; } = ConverterFor(DefaultSink);

    /// <summary>The default <see cref="Sink"/>.</summary>
    public const string DefaultSink = "d3d11videosink";

    /// <summary>The low-latency decoder for a sink: the D3D11 / D3D12 hardware decoder, or null (decodebin) for others.</summary>
    public static string? DecoderFor(string sink) => sink switch
    {
        "d3d11videosink" => "d3d11h264dec compliance=flexible",
        "d3d12videosink" => "d3d12h264dec compliance=flexible",
        _ => null,
    };

    /// <summary>The converter that keeps the frames on the GPU for a sink, or null (videoconvert) for others.</summary>
    public static string? ConverterFor(string sink) => sink switch
    {
        "d3d11videosink" => "d3d11convert",
        "d3d12videosink" => "d3d12convert",
        _ => null,
    };

    /// <summary>Options for <paramref name="sink"/> with its matching decoder and converter.</summary>
    public UxPlayOptions WithSink(string sink) => this with { Sink = sink, Decoder = DecoderFor(sink), Converter = ConverterFor(sink) };

    /// <summary>Extra uxplay arguments, appended as they are.</summary>
    public IReadOnlyList<string> ExtraArguments { get; init; } = [];

    /// <summary>Folder with the MSYS2 runtime DLLs (GStreamer etc.), put first on PATH; null leaves PATH alone.</summary>
    public string? MsysBin { get; init; } = DefaultMsysBin;

    /// <summary>Write every uxplay line, timestamped, to this file.</summary>
    public string? LogPath { get; init; }

    /// <summary>MSYS2 UCRT64 installed with scoop, as in PoC 1.</summary>
    public static string DefaultMsysBin { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "apps", "msys2", "current", "ucrt64", "bin");

    /// <summary>The command line: the PoC 3 arguments, the decoder and converter, then <see cref="ExtraArguments"/>.</summary>
    /// <remarks><c>-d</c> is required: the stock picture size is only printed in debug output.</remarks>
    public IReadOnlyList<string> BuildArguments() =>
    [
        "-n", Name, "-nh", "-vs", Sink, "-vsync", "no", "-d",
        .. Decoder is null ? [] : (string[])["-vd", Decoder],
        .. Converter is null ? [] : (string[])["-vc", Converter],
        .. ExtraArguments,
    ];

    /// <summary>The file at the root of the repository (see <see cref="FindLocalBuild"/>).</summary>
    public const string RepositoryMarker = "IceScreenTouch.slnx";

    /// <summary>
    /// Looks for the locally built uxplay.exe in poc/poc1-uxplay/UxPlay of <paramref name="start"/> or one of its
    /// parents: the patched build-isr first, then the stock build. Returns null if there is none.
    /// </summary>
    /// <remarks>
    /// For development only: a folder counts only if it is this repository (it holds <see cref="RepositoryMarker"/>)
    /// and is not a drive root. Any user may create folders at the root of C:, so walking up to it would run an
    /// uxplay.exe planted there by another account (security review, 2026-11-07).
    /// </remarks>
    public static string? FindLocalBuild(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            if (dir.Parent is null || !File.Exists(Path.Combine(dir.FullName, RepositoryMarker)))
            {
                continue;
            }

            var root = Path.Combine(dir.FullName, "poc", "poc1-uxplay", "UxPlay");
            foreach (var build in (string[])["build-isr", "build"])
            {
                var exe = Path.Combine(root, build, "uxplay.exe");
                if (File.Exists(exe))
                {
                    return exe;
                }
            }
        }

        return null;
    }
}

/// <summary>
/// Runs uxplay.exe as a child process and tracks what it prints (picture size, connected phone).
/// Port of <c>UxPlayProcess</c> in poc/poc3-integrated/mirror_touch.py. UxPlay draws into its own window;
/// this class only finds that window, it never draws into it or takes it over.
/// </summary>
public sealed class UxPlayVideoSource : IVideoSource, IAsyncDisposable
{
    private const int TailLength = 60;

    private readonly Lock _lock = new();
    private readonly Queue<string> _tail = new();
    private readonly WindowTracker _windows = new();
    private readonly Action<string> _log;
    private readonly bool _verbose;
    private Process? _process;
    private ChildProcessJob? _job;
    private StreamWriter? _logFile;
    private SizeD? _sinkSize;
    private SizeD? _logSize;
    private string? _deviceName;
    private string? _deviceModel;
    private int _codecPackets;

    // Internal so tests can feed lines without a process.
    internal UxPlayVideoSource(Action<string>? log, bool verbose)
    {
        _log = log ?? (_ => { });
        _verbose = verbose;
    }

    /// <inheritdoc/>
    public string Name => "UxPlay";

    /// <summary>Process ID, or 0 if not started.</summary>
    public int ProcessId { get; private set; }

    /// <summary>True while uxplay.exe runs.</summary>
    public bool IsRunning
    {
        get
        {
            var p = _process;
            return p is not null && !p.HasExited;
        }
    }

    /// <summary>Exit code once uxplay.exe has exited.</summary>
    public int? ExitCode => _process is { HasExited: true } p ? p.ExitCode : null;

    /// <summary>The real drawn size (patched uxplay only).</summary>
    public SizeD? SinkSize
    {
        get
        {
            lock (_lock)
            {
                return _sinkSize;
            }
        }
    }

    /// <inheritdoc/>
    public SizeD? VideoSize
    {
        get
        {
            lock (_lock)
            {
                return _sinkSize ?? _logSize;
            }
        }
    }

    /// <summary>Where <see cref="VideoSize"/> comes from: "sink" (patch), "log" (stock line) or "-".</summary>
    public string SizeSource
    {
        get
        {
            lock (_lock)
            {
                return _sinkSize is not null ? "sink" : _logSize is not null ? "log" : "-";
            }
        }
    }

    /// <summary>Name of the phone that connected last, if any.</summary>
    public string? DeviceName
    {
        get
        {
            lock (_lock)
            {
                return _deviceName;
            }
        }
    }

    /// <summary>Model of the phone that connected last (e.g. iPhone16,1), if any.</summary>
    public string? DeviceModel
    {
        get
        {
            lock (_lock)
            {
                return _deviceModel;
            }
        }
    }

    /// <summary>How many times the phone (re)announced the stream geometry.</summary>
    public int CodecPackets
    {
        get
        {
            lock (_lock)
            {
                return _codecPackets;
            }
        }
    }

    /// <summary>
    /// Starts uxplay.exe. Its output (stdout and stderr) is read continuously: an unread pipe fills up and
    /// stops uxplay (seen in PoC 3).
    /// </summary>
    public static UxPlayVideoSource Start(UxPlayOptions options, Action<string>? log = null, bool verbose = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!File.Exists(options.ExePath))
        {
            throw new FileNotFoundException("uxplay.exe not found (build UxPlay first, see poc/poc1-uxplay/README.md)", options.ExePath);
        }

        var source = new UxPlayVideoSource(log, verbose);
        try
        {
            source.StartProcess(options);
            return source;
        }
        catch
        {
            source.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    /// <summary>The last lines uxplay printed.</summary>
    public IReadOnlyList<string> Tail()
    {
        lock (_lock)
        {
            return [.. _tail];
        }
    }

    /// <summary>Puts a labelled marker into the log file, e.g. right after rotating the phone.</summary>
    public bool Mark(string label)
    {
        lock (_lock)
        {
            if (_logFile is null)
            {
                return false;
            }

            WriteLog("### MARK " + label);
            return true;
        }
    }

    /// <inheritdoc/>
    public nint FindWindow() => IsRunning ? _windows.Find(ProcessId) : 0;

    /// <summary>Stops uxplay.exe (and its children) and closes the log.</summary>
    public async ValueTask DisposeAsync()
    {
        var p = _process;
        if (p is not null)
        {
            try
            {
                if (!p.HasExited)
                {
                    p.Kill(entireProcessTree: true);
                }

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception or AggregateException)
            {
                // Already gone, not stopping, or part of its tree would not end (Kill then throws an aggregate):
                // the job below ends what is left.
            }

            p.Dispose();
        }

        // Closing the job also kills anything uxplay left behind.
        _job?.Dispose();
        _job = null;

        lock (_lock)
        {
            _logFile?.Dispose();
            _logFile = null;
        }
    }

    /// <summary>Handles one line of uxplay output (called on the reader threads).</summary>
    internal void OnLine(string line)
    {
        // uxplay -d dumps the session keys (aeskey, ecdh_secret, ekey, aesiv) and pairing proofs as lines of hex
        // bytes. With them, a recording of the AirPlay stream could be decrypted, so they never reach the log
        // file or the tail (security review, 2026-11-07); nothing the app reads is in such a line.
        if (UxPlayLogParser.IsHexDump(line))
        {
            return;
        }

        string? message = null;
        lock (_lock)
        {
            _tail.Enqueue(line);
            while (_tail.Count > TailLength)
            {
                _tail.Dequeue();
            }

            WriteLog(line);
            switch (UxPlayLogParser.Parse(line))
            {
                case SinkSizeEvent s:
                    if (s.Size != _sinkSize)
                    {
                        message = string.Create(CultureInfo.InvariantCulture, $"[video] sink size {s.Size.Width:0}x{s.Size.Height:0}");
                    }

                    _sinkSize = s.Size;
                    break;
                case VideoSizeEvent v:
                    _logSize = v.Size;
                    _codecPackets++;
                    if (_verbose)
                    {
                        message = string.Create(CultureInfo.InvariantCulture, $"[video] codec packet #{_codecPackets}: {v.Size.Width:0}x{v.Size.Height:0}");
                    }

                    break;
                case DeviceConnectedEvent d:
                    _deviceName = d.Name;
                    _deviceModel = d.Model;
                    message = $"[video] connection from {d.Name} ({d.Model})";
                    break;
                case MirroringEndedEvent:
                    // Older AirPlay versions make uxplay print this twice (video TEARDOWN, then the final one),
                    // newer ones once: say it, and forget the session, only the first time. Without this the old size and phone name outlive the
                    // session (Status tab, input target "Mirrored").
                    if (_sinkSize is not null || _logSize is not null || _deviceName is not null)
                    {
                        message = "[video] mirroring ended";
                    }

                    _sinkSize = null;
                    _logSize = null;
                    _deviceName = null;
                    _deviceModel = null;
                    break;
            }
        }

        if (message is not null)
        {
            _log(message);
        }
    }

    private void StartProcess(UxPlayOptions options)
    {
        if (options.LogPath is { } path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            _logFile = new StreamWriter(path, append: false, new UTF8Encoding(false)) { AutoFlush = true };
        }

        var psi = new ProcessStartInfo(options.ExePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(options.ExePath))!,
        };
        foreach (var arg in options.BuildArguments())
        {
            psi.ArgumentList.Add(arg);
        }

        if (options.MsysBin is { } msys)
        {
            psi.Environment["PATH"] = msys + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
        }

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => OnData(e.Data);
        p.ErrorDataReceived += (_, e) => OnData(e.Data);
        p.Start();
        _process = p;
        ProcessId = p.Id;
        TryAddToJob(p);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
    }

    // uxplay must not outlive the app, even if the app is killed. Not fatal if Windows refuses.
    private void TryAddToJob(Process p)
    {
        try
        {
            _job = new ChildProcessJob();
            _job.Add(p);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            _job?.Dispose();
            _job = null;
            _log($"[video] uxplay is not tied to this process ({ex.Message}); it may outlive a crash");
        }
    }

    private void OnData(string? data)
    {
        if (data is not null)
        {
            OnLine(data.TrimEnd());
        }
    }

    // Caller holds _lock.
    private void WriteLog(string text)
    {
        if (_logFile is not { } file)
        {
            return;
        }

        try
        {
            file.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {UxPlayLogParser.Mask(text)}");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // A full disk must not end the app: this runs on the process output callback, where an exception is
            // unhandled. Stop writing the UxPlay log (the app log still gets the parsed lines).
            _logFile = null;
            _log($"[video] cannot write the UxPlay log any more: {ex.Message}");
        }
    }
}
