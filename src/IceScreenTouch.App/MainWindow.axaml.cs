// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using IceScreenTouch.App.Resources;
using IceScreenTouch.App.Settings;
using IceScreenTouch.App.ViewModels;

namespace IceScreenTouch.App;

/// <summary>Small status window (BLE, video source, rotation), settings and log.</summary>
public partial class MainWindow : Window
{
    private MainViewModel? _subscribed;
    private Window? _notices;
    private int _copies;

    /// <summary>Creates the window; the view model comes in as the DataContext.</summary>
    public MainWindow()
    {
        InitializeComponent();

        // The About tab shows the icon large: the .ico's biggest frame (256 px), not the one Avalonia picks.
        using var ico = AssetLoader.Open(new Uri("avares://IceScreenTouch.App/Assets/icon.ico"));
        using var buffer = new MemoryStream();
        ico.CopyTo(buffer);
        if (IconFrames.LargestPng(buffer.GetBuffer().AsSpan(0, (int)buffer.Length)) is { } png)
        {
            AboutIcon.Source = new Bitmap(new MemoryStream(png));
        }
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    /// <inheritdoc/>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // Going to the tray (or exiting) hides this window; Windows does not hide owned windows with their owner.
        if (change.Property == IsVisibleProperty && !IsVisible)
        {
            _notices?.Close();
        }
    }

    /// <inheritdoc/>
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_subscribed is not null)
        {
            _subscribed.LogLines.CollectionChanged -= OnLogChanged;
        }

        _subscribed = ViewModel;
        if (_subscribed is not null)
        {
            _subscribed.LogLines.CollectionChanged += OnLogChanged;
        }
    }

    // Keep the newest line in view.
    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && LogList.ItemCount > 0)
        {
            Dispatcher.UIThread.Post(() => LogList.ScrollIntoView(LogList.ItemCount - 1), DispatcherPriority.Background);
        }
    }

    private async void BrowseUxPlay(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.PickUxPlay,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("uxplay.exe") { Patterns = ["uxplay.exe", "*.exe"] }],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path && ViewModel is { } vm)
        {
            vm.UxPlayPath = path;
        }
    }

    private async void BrowseMsys(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Strings.PickMsys,
            AllowMultiple = false,
        });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path && ViewModel is { } vm)
        {
            vm.MsysBin = path;
        }
    }

    private async void OpenLogFolder(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.LogFolder is { } folder && Directory.Exists(folder))
        {
            await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(AppPaths.ForExternalProgram(folder)));
        }
    }

    private async void OpenRepository(object? sender, RoutedEventArgs e) =>
        await Launcher.LaunchUriAsync(new Uri(AppInfo.RepositoryUrl));

    private async void OpenIssues(object? sender, RoutedEventArgs e) =>
        await Launcher.LaunchUriAsync(new Uri(AppInfo.IssuesUrl));

    private async void OpenDataFolder(object? sender, RoutedEventArgs e)
    {
        // The folder exists once the app has written its log or settings; before that there is nothing to show.
        if (Directory.Exists(AppPaths.Root))
        {
            await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(AppPaths.ForExternalProgram(AppPaths.Root)));
        }
    }

    private async void CopyInfo(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is not { } clipboard || ViewModel?.About is not { } about)
        {
            return;
        }

        try
        {
            await clipboard.SetTextAsync(about.Diagnostics);
        }
        catch (Exception ex) when (ex is TimeoutException or System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            // Another program holds the clipboard. An exception here would end the app (async void handler).
            ViewModel?.WriteLog($"[app ] cannot copy the version information: {ex.Message}");
            return;
        }

        about.Copied = true;

        // Only the latest copy hides the note: an earlier click's timer must not cut this one short.
        var copy = ++_copies;
        DispatcherTimer.RunOnce(
            () =>
            {
                if (copy == _copies)
                {
                    about.Copied = false;
                }
            },
            TimeSpan.FromSeconds(3));
    }

    // The full notices (about 430 KB): one virtualized line per item, so the window opens at once.
    private void ShowNotices(object? sender, RoutedEventArgs e)
    {
        if (_notices is not null)
        {
            _notices.Activate();
            return;
        }

        var list = new ListBox
        {
            ItemsSource = AppInfo.NoticesText.ReplaceLineEndings("\n").Split('\n'),
            FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
            FontSize = 11,
            ItemTemplate = new FuncDataTemplate<string>((line, _) => new SelectableTextBlock { Text = line, TextWrapping = TextWrapping.Wrap }),
        };
        var window = new Window
        {
            Title = Strings.NoticesTitle,
            Icon = Icon,
            Width = 760,
            Height = 640,
            Content = list,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        // The same background as the main window, following the theme when it changes (a copied brush would not).
        window.Bind(BackgroundProperty, window.GetResourceObservable("AppWindowBackground"));
        window.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Setter(PaddingProperty, new Thickness(8, 0)),
                new Setter(MinHeightProperty, 0d),
            },
        });
        window.Closed += (_, _) => _notices = null;
        _notices = window;
        window.Show(this);
    }
}
