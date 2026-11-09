// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using IceScreenTouch.App.Logging;
using IceScreenTouch.App.Resources;
using IceScreenTouch.App.Services;
using IceScreenTouch.App.Settings;
using IceScreenTouch.App.ViewModels;

namespace IceScreenTouch.App;

/// <summary>What a platform head hands to the app.</summary>
/// <param name="Mirror">The session (Bluetooth, hook, receiver) for this OS.</param>
/// <param name="Store">Where the settings live.</param>
/// <param name="Log">The app log.</param>
/// <param name="Probe">The probe tab's service, if the platform has one.</param>
/// <param name="Receivers">Finds the AirPlay receivers on this PC, if the platform can.</param>
/// <param name="License">The Store license check, if this build has one (null = a build of your own, nothing is checked).</param>
public sealed record AppServices(IMirrorService Mirror, SettingsStore Store, AppLog Log, IProbeService? Probe = null, IReceiverFinder? Receivers = null, LicenseService? License = null);

/// <summary>
/// Avalonia application shared by every platform head. Lives in the notification area: closing the window
/// hides it (unless turned off in the settings); Exit in the tray menu stops the session and quits.
/// </summary>
public partial class App : Application
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private MainViewModel? _vm;
    private MainWindow? _window;
    private bool _windowClosed;
    private TrayIcon? _tray;
    private DispatcherTimer? _timer;
    private bool _exiting;

    /// <summary>Set by the platform head before the app starts (null in the previewer).</summary>
    public AppServices? Services { get; init; }

    /// <inheritdoc/>
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <inheritdoc/>
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (Services is { } services)
            {
                Start(desktop, services);
            }
            else
            {
                desktop.MainWindow = new MainWindow(); // previewer
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void Start(IClassicDesktopStyleApplicationLifetime desktop, AppServices services)
    {
        var settings = services.Store.Load(out var problem);
        if (problem is not null)
        {
            services.Log.Write("[app ] " + problem);
        }

        // Before the first window: the views take their text when they are created.
        AppLanguage.Apply(settings.Language);

        RequestedThemeVariant = AppTheme.ToVariant(settings.Theme);

        // First run (no settings file yet): pick a receiver that is there. The status tab shows the setup steps.
        var firstRun = !File.Exists(services.Store.Path);
        if (firstRun)
        {
            settings = FirstRunSettings(services, settings);
        }

        services.Log.Write($"[app ] settings: {services.Store.Path}{(firstRun ? " (first run)" : string.Empty)}");
        _vm = new MainViewModel(services.Mirror, services.Store, services.Log, settings, a => Dispatcher.UIThread.Post(a), services.Probe, services.Receivers, services.License);
        services.Log.LineAdded += (_, line) => Dispatcher.UIThread.Post(() => _vm.AppendLog(line));
        _vm.SettingsApplied += (_, _) => RequestedThemeVariant = AppTheme.ToVariant(_vm.Settings.Theme);

        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _window = NewWindow(desktop, settings.Window);
        _window.Show();
        AttachLicenseWindow(services.License, _window);

        _tray = CreateTray();
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => _vm.Refresh());
        _timer.Start();

        // Session end (log off, shut down) without our Exit: stop what we can.
        desktop.Exit += (_, _) => StopBlocking(services);

        _ = BeginAsync(services);
    }

    // Reads the license before the session starts (an ended trial never gets a session), then starts it. The view model
    // refuses the start while the license says the trial has ended.
    private async Task BeginAsync(AppServices services)
    {
        if (services.License is { } license)
        {
            await license.RefreshAsync().ConfigureAwait(true);
            license.StartMonitoring();
        }

        if (!_exiting)
        {
            await _vm!.StartAsync().ConfigureAwait(true);
        }
    }

    // The Store shows its dialogs over this window (it must be told before a purchase).
    private static void AttachLicenseWindow(LicenseService? license, Window window)
    {
        if (license is not null && window.TryGetPlatformHandle() is { } handle)
        {
            license.AttachWindow(handle.Handle);
        }
    }

    private static AppSettings FirstRunSettings(AppServices services, AppSettings settings)
    {
        if (services.Receivers is not { } finder)
        {
            return settings;
        }

        IReadOnlyList<ReceiverCandidate> found;
        try
        {
            found = finder.Find();
        }
#pragma warning disable CA1031 // Without receivers the defaults stay; the setup tab says what to do.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            services.Log.Write($"[app ] cannot look for receivers: {ex.Message}");
            return settings;
        }

        services.Log.Write($"[app ] receivers found: {(found.Count == 0 ? "none" : string.Join(", ", found.Select(c => $"{c.Name}{(c.Running ? " (running)" : string.Empty)}")))}");
        if (SetupViewModel.ChooseFirst(found) is not { } choice)
        {
            return settings;
        }

        services.Log.Write($"[app ] first run: using {choice.Name}");
        settings = SetupViewModel.Apply(settings, choice);
        try
        {
            services.Store.Save(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            services.Log.Write($"[app ] cannot save the settings: {ex.Message}");
        }

        return settings;
    }

    private TrayIcon CreateTray()
    {
        var vm = _vm!;

        // The state comes first, as a line that does nothing (the tooltip says the same).
        var state = new NativeMenuItem(vm.Headline) { IsEnabled = false };
        var show = new NativeMenuItem(Strings.TrayShow);
        show.Click += (_, _) => ShowWindow();
        var home = new NativeMenuItem(Strings.ButtonHome) { Command = vm.HomeCommand };
        var rotate = new NativeMenuItem(Strings.ButtonRotate) { Command = vm.ToggleRotationCommand };
        var startStop = new NativeMenuItem(vm.StartStopText) { Command = vm.StartStopCommand };
        var restart = new NativeMenuItem(Strings.ButtonRestart) { Command = vm.RestartCommand };
        var exit = new NativeMenuItem(Strings.TrayExit);
        exit.Click += async (_, _) => await ExitAsync();

        var tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://IceScreenTouch.App/Assets/icon.ico"))),
            ToolTipText = Loc.Format(Strings.FormatTrayTooltip, vm.Headline),
            Menu = [state, new NativeMenuItemSeparator(), show, home, rotate, new NativeMenuItemSeparator(), startStop, restart, new NativeMenuItemSeparator(), exit],
            IsVisible = true,
        };
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Headline))
            {
                state.Header = vm.Headline;
                tray.ToolTipText = Loc.Format(Strings.FormatTrayTooltip, vm.Headline);
            }
            else if (e.PropertyName == nameof(MainViewModel.StartStopText))
            {
                startStop.Header = vm.StartStopText;
            }
        };
        tray.Clicked += (_, _) => ShowWindow();
        TrayIcon.SetIcons(this, [tray]);
        return tray;
    }

    private MainWindow NewWindow(IClassicDesktopStyleApplicationLifetime desktop, WindowPlacement? placement)
    {
        var window = new MainWindow { DataContext = _vm };

        // The saved place only if a screen is still there (a monitor may have been unplugged since).
        if (placement is { } p && window.Screens.ScreenFromPoint(new PixelPoint(p.X + 40, p.Y + 20)) is not null)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Position = new PixelPoint(p.X, p.Y);
        }

        window.Closing += OnWindowClosing;
        window.Closed += (_, _) => _windowClosed = true;
        desktop.MainWindow = window;
        _windowClosed = false;
        return window;
    }

    private void ShowWindow()
    {
        if (_window is null)
        {
            return;
        }

        // Closed for a sign-out or shutdown that was then cancelled (another app said no): the app runs on in the
        // tray, and a closed window cannot be shown again (code review, 2026-11-07).
        if (_windowClosed && !_exiting && _vm is not null && ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _window = NewWindow(desktop, _vm.Settings.Window);
        }

        _window.Show();
        AttachLicenseWindow(Services?.License, _window);
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
    }

    private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_exiting || _window is null || _vm is null)
        {
            return;
        }

        RememberWindow();
        if (e.CloseReason is WindowCloseReason.OSShutdown or WindowCloseReason.ApplicationShutdown)
        {
            // Windows is shutting down or signing out: cancelling would hold that up ("IceScreen Touch is preventing
            // shutdown") and skip desktop.Exit, which stops the session (code review, 2026-11-07).
            return;
        }

        e.Cancel = true;
        if (_vm.Settings.CloseToTray)
        {
            _window.Hide();
        }
        else
        {
            await ExitAsync();
        }
    }

    private async Task ExitAsync()
    {
        if (_exiting || _vm is null)
        {
            return;
        }

        _exiting = true;
        RememberWindow();
        _timer?.Stop();
        Services?.License?.Dispose();
        if (_tray is not null)
        {
            _tray.IsVisible = false;
        }

        _window?.Hide();
        _vm.Probe?.Cancel();
        await Task.WhenAny(_vm.StopAsync(), Task.Delay(StopTimeout)).ConfigureAwait(true);
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    private void RememberWindow()
    {
        if (_window is { IsVisible: true, WindowState: WindowState.Normal } w)
        {
            _vm?.RememberWindow(w.Position.X, w.Position.Y);
        }
    }

    private void StopBlocking(AppServices services)
    {
        _tray?.Dispose();
        if (!_exiting)
        {
            // The session uses ConfigureAwait(false) throughout, so waiting here cannot deadlock the UI thread.
            _ = Task.Run(services.Mirror.StopAsync).Wait(StopTimeout);
        }
    }
}
