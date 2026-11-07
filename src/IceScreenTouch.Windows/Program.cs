using Avalonia;
using IceScreenTouch.App;
using IceScreenTouch.App.Logging;
using IceScreenTouch.App.Resources;
using IceScreenTouch.App.Services;
using IceScreenTouch.App.Settings;
using IceScreenTouch.Platform.Windows;

namespace IceScreenTouch.Windows;

internal static class Program
{
    // Only one process can publish the BLE HID service, so only one instance may run.
    private const string InstanceMutexName = @"Local\IceScreenTouch-6d2b0f4e-instance";

    [STAThread]
    public static int Main(string[] args)
    {
        if (args is [SelfCheck.Argument])
        {
            // No window and no instance mutex: safe to run next to a running app.
            return SelfCheck.RunAsync(Console.Out).GetAwaiter().GetResult();
        }

        // Explorer sits outside the package container: it needs the redirected folder (packaged builds only).
        AppPaths.ForExternalProgram = PackagePaths.ForExplorer;

        // Before anything reads the settings: the first start after the rename takes the old settings along.
        var imported = AppPaths.ImportFormerSettings(AppPaths.Root, AppPaths.FormerRoot);

        using var instance = new Mutex(initiallyOwned: true, InstanceMutexName, out var first);
        if (!first)
        {
            // The dialog speaks the language of the settings (the app has not started yet).
            AppLanguage.Apply(new SettingsStore().Load(out _).Language);
            NativeDialog.Info("IceScreen Touch", Strings.AlreadyRunning);
            return 1;
        }

        // Physical pixels everywhere (hook, cursor, windows; Avalonia asks for the same), and a 1 ms timer
        // for the 4 ms bridge loop.
        ProcessSetup.EnablePerMonitorDpiAwareness();
        using var timerResolution = ProcessSetup.HighResolutionTimer();

        using var log = new AppLog(AppPaths.Logs);
        log.Write($"[app ] IceScreen Touch {AppInfo.InformationalVersion} started (pid {Environment.ProcessId}); log: {log.FilePath ?? "-"}");
        if (imported is not null)
        {
            log.Write($"[app ] settings taken over from {imported}");
        }
        AppDomain.CurrentDomain.UnhandledException += (_, e) => log.Write($"[app ] unhandled: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            log.Write($"[app ] unobserved task exception: {e.Exception.GetBaseException()}");
            e.SetObserved();
        };

        var mirror = new WindowsMirrorService(log);
        try
        {
            var probe = new WindowsProbeService(mirror, log);
            // Only the package that came from the Store checks its license. A build of your own, and a test package signed locally
            // (Developer, Enterprise, None, System), have no source and are always fully usable.
            log.Write($"[lic ] package signature: {(PackageIdentity.IsPackaged ? PackageIdentity.SignatureKind?.ToString() ?? "unreadable" : "not packaged")}; license check {(PackageIdentity.UsesStoreLicense ? "on" : "off")}");
            using var license = new LicenseService(PackageIdentity.UsesStoreLicense ? new StoreLicenseSource(log) : null, TimeProvider.System, log);
            return BuildAvaloniaApp(new AppServices(mirror, new SettingsStore(), log, probe, new WindowsReceiverFinder(log, () => mirror.OwnUxPlayProcessId), license)).StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            // Normally stopped already by Exit; this covers the other ways out.
            _ = Task.Run(() => mirror.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(5));
            log.Write("[app ] exited");
        }
    }

    // Used by the Avalonia previewer (no services: the window shows its design-time state).
    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp(null);

    private static AppBuilder BuildAvaloniaApp(AppServices? services) =>
        AppBuilder.Configure(() => new App.App { Services = services })
            .UsePlatformDetect()
            .LogToTrace();
}
