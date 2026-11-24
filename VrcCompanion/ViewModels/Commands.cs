using System.Windows.Input;

namespace VrcCompanion.ViewModels;

public interface IRelayCommand : ICommand
{
    void RaiseCanExecuteChanged();
}

public sealed class RelayCommand : IRelayCommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => _execute();

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class RelayCommand<T> : IRelayCommand
{
    private readonly Action<T> _execute;
    private readonly Func<T, bool>? _canExecute;

    public RelayCommand(Action<T> execute, Func<T, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter)
        => _canExecute?.Invoke(Cast(parameter)) ?? true;

    public void Execute(object? parameter) => _execute(Cast(parameter));

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    private static T Cast(object? parameter)
        => parameter is T value ? value : default!;
}

public sealed class AsyncRelayCommand : IRelayCommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private readonly Action<Exception> _onFailure;
    private bool _isExecuting;

    /// <param name="onFailure">
    /// Where a failed command is reported. The default writes it to the lifecycle log: a
    /// command's failure is not a crash, and it must not reach the dispatcher's unhandled
    /// handler, which reports it as one and papers over the whole window.
    /// </param>
    public AsyncRelayCommand(
        Func<Task> execute,
        Func<bool>? canExecute = null,
        Action<Exception>? onFailure = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
        _onFailure = onFailure ?? ReportToLifecycleLog;
    }

    internal static void ReportToLifecycleLog(Exception exception)
        => Services.LifecycleLog.Write(
            $"command: {exception.GetType().Name}: {exception.Message}");

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter)
        => !_isExecuting && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _isExecuting = true;
        RaiseCanExecuteChanged();
        try
        {
            await _execute();
        }
        catch (Exception ex)
        {
            // The button has to come back whatever happened, and the failure goes where a
            // command failure belongs rather than out of an async void and into the crash path.
            _onFailure(ex);
        }
        finally
        {
            _isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class AsyncRelayCommand<T> : IRelayCommand
{
    private readonly Func<T, Task> _execute;
    private readonly Func<T, bool>? _canExecute;
    private readonly Action<Exception> _onFailure;
    private bool _isExecuting;

    public AsyncRelayCommand(
        Func<T, Task> execute,
        Func<T, bool>? canExecute = null,
        Action<Exception>? onFailure = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
        _onFailure = onFailure ?? AsyncRelayCommand.ReportToLifecycleLog;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter)
        => !_isExecuting && (_canExecute?.Invoke(Cast(parameter)) ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _isExecuting = true;
        RaiseCanExecuteChanged();
        try
        {
            await _execute(Cast(parameter));
        }
        catch (Exception ex)
        {
            _onFailure(ex);
        }
        finally
        {
            _isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    private static T Cast(object? parameter)
        => parameter is T value ? value : default!;
}
