// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Diagnostics;
using IceScreenTouch.App.Logging;
using IceScreenTouch.App.Resources;
using IceScreenTouch.App.Services;
using IceScreenTouch.Video;
using Microsoft.Win32;

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
    private const string ShellPrefix = "shell:";

    public IReadOnlyList<ReceiverCandidate> Find()
    {
        var found = new List<ReceiverCandidate>();
        foreach (var known in KnownReceivers.All)
        {
            var running = IsRunning(known.ProcessName);

            // The classic installer's program first; else a Store app, which Explorer starts by its shell address.
            var exe = InstalledPath(known.InstallPaths) ?? (StoreAppInstalled(known.StoreFamily) ? known.StoreLaunchAddress : null);
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
        if (candidate.Kind != ReceiverCandidateKind.Attach || candidate.ExePath is not { } exe)
        {
            return false;
        }

        // A Store app: Explorer starts it (it does not always leave a process to hold on to).
        var store = exe.StartsWith(ShellPrefix, StringComparison.OrdinalIgnoreCase);
        if (store ? !IsKnownStoreAddress(exe) : !File.Exists(exe))
        {
            return false;
        }

        try
        {
            log.Write($"[app ] starting {candidate.Name}: {exe}");
            using var process = store
                ? Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), exe) { UseShellExecute = true })
                : Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) });
            return store || process is not null;
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

    // Only the shell addresses of the known Store receivers are started this way, not an arbitrary shell: string.
    private static bool IsKnownStoreAddress(string address) =>
        KnownReceivers.All.Any(k => string.Equals(k.StoreLaunchAddress, address, StringComparison.OrdinalIgnoreCase));

    // A Store app of this package family is installed for the user: its package is in the user's package repository
    // (the registry), and its data folder is under %LOCALAPPDATA%\Packages. The program files are not listable.
    private static bool StoreAppInstalled(string? family)
    {
        if (string.IsNullOrEmpty(family))
        {
            return false;
        }

        try
        {
            if (Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", family)))
            {
                return true;
            }

            // The repository's keys are full package names: Name_Version_Architecture_ResourceId_PublisherId.
            var split = family.IndexOf('_', StringComparison.Ordinal);
            if (split <= 0)
            {
                return false;
            }

            var (name, publisher) = (family[..(split + 1)], family[split..]);
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
            return key?.GetSubKeyNames().Any(n => n.StartsWith(name, StringComparison.OrdinalIgnoreCase) && n.EndsWith(publisher, StringComparison.OrdinalIgnoreCase)) ?? false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static string? InstalledPath(IReadOnlyList<string> relativePaths)
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
