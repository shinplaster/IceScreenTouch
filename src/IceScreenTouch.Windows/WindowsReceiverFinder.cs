using System.ComponentModel;
using System.Diagnostics;
using IceScreenTouch.App.Logging;
using IceScreenTouch.App.Resources;
using IceScreenTouch.App.Services;
using IceScreenTouch.Video;

namespace IceScreenTouch.Windows;

/// <summary>
/// Finds AirPlay receivers on Windows: the ones running now, the known ones installed in their usual folders,
/// and a locally built uxplay.exe (poc/poc1-uxplay). A receiver it does not know can still be followed by typing
/// its process name in the settings.
/// </summary>
/// <param name="log">The app log.</param>
/// <param name="ownUxPlay">The uxplay.exe the app itself started (0 = none), left out of the running ones.</param>
internal sealed class WindowsReceiverFinder(AppLog log, Func<int>? ownUxPlay = null) : IReceiverFinder
{
    private sealed record Known(string Name, string ProcessName, string[] RelativePaths);

    // Paths relative to Program Files / Program Files (x86).
    private static readonly Known[] KnownReceivers =
    [
        new("AirServer", "AirServer", [@"App Dynamic\AirServer\AirServer.exe"]),
        new("LonelyScreen", "LonelyScreen", [@"LonelyScreen\LonelyScreen.exe", @"Lonely Screen\LonelyScreen.exe"]),
    ];

    public IReadOnlyList<ReceiverCandidate> Find()
    {
        var found = new List<ReceiverCandidate>();
        foreach (var known in KnownReceivers)
        {
            var running = IsRunning(known.ProcessName);
            var exe = InstalledPath(known.RelativePaths);
            if (running || exe is not null)
            {
                found.Add(new ReceiverCandidate(known.Name, ReceiverCandidateKind.Attach, known.ProcessName, exe, running));
            }
        }

        // A uxplay started by hand is followed; the local build is started by the app.
        // Our own child does not count: "use" on it would stop it (the restart) and leave nothing to follow.
        if (IsRunning("uxplay", ownUxPlay?.Invoke() ?? 0))
        {
            found.Add(new ReceiverCandidate(Strings.ReceiverUxPlayRunning, ReceiverCandidateKind.Attach, "uxplay", null, true));
        }

        var local = UxPlayOptions.FindLocalBuild(AppContext.BaseDirectory);
        if (local is not null)
        {
            found.Add(new ReceiverCandidate(Strings.ReceiverUxPlayLocal, ReceiverCandidateKind.StartUxPlay, "uxplay", local, false));
        }

        return [.. found.OrderByDescending(c => c.Running)];
    }

    public bool Launch(ReceiverCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.Kind != ReceiverCandidateKind.Attach || candidate.ExePath is not { } exe || !File.Exists(exe))
        {
            return false;
        }

        try
        {
            log.Write($"[app ] starting {candidate.Name}: {exe}");
            using var process = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) });
            return process is not null;
        }
        catch (Win32Exception ex)
        {
            log.Write($"[app ] cannot start {candidate.Name}: {ex.Message}");
            return false;
        }
    }

    private static bool IsRunning(string processName, int except = 0)
    {
        var processes = Process.GetProcessesByName(processName);
        var any = false;
        foreach (var p in processes)
        {
            any |= p.Id != except;
            p.Dispose();
        }

        return any;
    }

    private static string? InstalledPath(string[] relativePaths)
    {
        foreach (var root in (Environment.SpecialFolder[])[Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86])
        {
            var dir = Environment.GetFolderPath(root);
            if (string.IsNullOrEmpty(dir))
            {
                continue;
            }

            foreach (var relative in relativePaths)
            {
                var path = Path.Combine(dir, relative);
                if (File.Exists(path))
                {
                    return path;
                }
            }
        }

        return null;
    }
}
