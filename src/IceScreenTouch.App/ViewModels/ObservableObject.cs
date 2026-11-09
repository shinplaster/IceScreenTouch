// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace IceScreenTouch.App.ViewModels;

/// <summary>Minimal INotifyPropertyChanged base (no MVVM package needed for one window).</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Sets <paramref name="field"/> and raises PropertyChanged if the value changed.</summary>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    /// <summary>Raises PropertyChanged.</summary>
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A command that runs an action; <see cref="CanExecute"/> follows a predicate.</summary>
public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    /// <inheritdoc/>
    public event EventHandler? CanExecuteChanged;

    /// <inheritdoc/>
    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    /// <inheritdoc/>
    public void Execute(object? parameter)
    {
        if (CanExecute(parameter))
        {
            execute();
        }
    }

    /// <summary>Asks the UI to query <see cref="CanExecute"/> again.</summary>
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>A command that runs an async action; disabled while it runs.</summary>
public sealed class AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null) : ICommand
{
    private bool _running;

    /// <inheritdoc/>
    public event EventHandler? CanExecuteChanged;

    /// <summary>The last run (for tests).</summary>
    public Task Last { get; private set; } = Task.CompletedTask;

    /// <inheritdoc/>
    public bool CanExecute(object? parameter) => !_running && (canExecute?.Invoke() ?? true);

    /// <inheritdoc/>
    public void Execute(object? parameter)
    {
        if (CanExecute(parameter))
        {
            Last = RunAsync();
        }
    }

    /// <summary>Asks the UI to query <see cref="CanExecute"/> again.</summary>
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    private async Task RunAsync()
    {
        _running = true;
        Refresh();
        try
        {
            await execute();
        }
        finally
        {
            _running = false;
            Refresh();
        }
    }
}
