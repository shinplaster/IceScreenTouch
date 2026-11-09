namespace IceScreenTouch.Video;

/// <summary>An AirPlay receiver the app knows how to find and start.</summary>
/// <param name="Name">Name for the user.</param>
/// <param name="ProcessName">The process whose window shows the picture (without .exe). It may not be the one to start.</param>
/// <param name="InstallPaths">
/// The program to start, relative to Program Files and Program Files (x86), in the order to look. Not always the
/// process that shows the picture: 5KPlayer.exe starts Airplay.exe.
/// </param>
/// <param name="StoreFamily">The Microsoft Store package family, for the receiver that also comes from the Store.</param>
/// <param name="StoreAppId">The app's ID in that package.</param>
public sealed record KnownReceiver(string Name, string ProcessName, IReadOnlyList<string> InstallPaths, string? StoreFamily = null, string? StoreAppId = null)
{
    /// <summary>
    /// What Explorer starts for the Store app (<c>shell:AppsFolder\family!appId</c>), or null for a receiver that is
    /// not in the Store. A packaged app's executable is not startable by its own path.
    /// </summary>
    public string? StoreLaunchAddress => StoreFamily is null || StoreAppId is null ? null : $@"shell:AppsFolder\{StoreFamily}!{StoreAppId}";
}

/// <summary>The receivers the app lists without being told (the setup list of the status tab).</summary>
public static class KnownReceivers
{
    /// <summary>
    /// AirServer comes in two kinds with the same process name, AirServer.exe: the classic installer's, and the
    /// Store's UWP app (package F3F176BD.AirServer, 2026-11-09). They are one row; the classic one is started when
    /// both are installed (it has a window of its own; the UWP one is found by its frame, see
    /// <see cref="Platform.Windows.Win32Windows.LargestVisibleWindowOf"/>).
    /// </summary>
    public static IReadOnlyList<KnownReceiver> All { get; } =
    [
        new("AirServer", "AirServer", [@"App Dynamic\AirServer\AirServer.exe"], "F3F176BD.AirServer_p8qzvses5c8me", "AirServer.Desktop"),
        new("LonelyScreen", "LonelyScreen", [@"LonelyScreen\LonelyScreen.exe", @"Lonely Screen\LonelyScreen.exe"]),
        new("Reflector 4", "Reflector4", [@"Reflector 4\Reflector4.exe"]),
        new("5KPlayer", "Airplay", [@"DearMob\5KPlayer\5KPlayer.exe"]),
    ];

    /// <summary>
    /// The name to show for the process that is followed ("5KPlayer" for Airplay.exe, "Reflector 4" for Reflector4.exe):
    /// the same as in the receiver list. A process that is not known is shown by its own name, without .exe.
    /// </summary>
    public static string DisplayName(string processName)
    {
        ArgumentNullException.ThrowIfNull(processName);
        var name = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? processName[..^4] : processName;
        return All.FirstOrDefault(k => k.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Name ?? name;
    }
}
