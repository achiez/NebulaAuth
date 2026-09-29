using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace NebulaAuth.Core;

/// <summary>
///     One snackbar notification. Immutable.
/// </summary>
public sealed class SnackbarMessage
{
    public SnackbarMessage(string text, string? actionText, Action? action, TimeSpan duration)
    {
        Text = text;
        ActionText = actionText;
        Action = action;
        Duration = duration;
    }

    public string Text { get; }

    /// <summary>
    ///     Text of the action button, null when the message has no button.
    /// </summary>
    public string? ActionText { get; }

    public Action? Action { get; }
    public TimeSpan Duration { get; }
    public bool HasAction => ActionText != null;

    public bool IsDuplicateOf(SnackbarMessage other)
    {
        return Text == other.Text && ActionText == other.ActionText;
    }
}

/// <summary>
///     Snackbar message queue: shows messages one by one,
///     each for its <see cref="SnackbarMessage.Duration" />. Thread-safe: <see cref="Enqueue" /> can be called from any
///     thread, all state changes (<see cref="Current" />, <see cref="IsActive" />) happen on the UI thread.
///     A Snackbar control binds to <see cref="Current" /> / <see cref="IsActive" /> and calls
///     <see cref="InvokeAction" />, <see cref="Dismiss" />, <see cref="Pause" />, <see cref="Resume" />.
/// </summary>
public sealed partial class SnackbarMessageQueue : ObservableObject
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(100);

    /// <summary>
    ///     Pause between two messages so the control can play its hide/show animation.
    /// </summary>
    private static readonly TimeSpan GapBetweenMessages = TimeSpan.FromMilliseconds(300);

    private static readonly TimeSpan DisplayPollInterval = TimeSpan.FromMilliseconds(500);

    private readonly object _lock = new();
    private readonly LinkedList<SnackbarMessage> _pending = new();
    private SnackbarMessage? _displayed; // guarded by _lock, mirrors Current
    private TaskCompletionSource? _dismissTcs;
    private bool _isRunning; // guarded by _lock
    private int _pauseCount;
    private int _attachedCount; // UI thread

    /// <summary>
    ///     Message currently displayed, null when nothing is displayed.
    /// </summary>
    [ObservableProperty] private SnackbarMessage? _current;

    /// <summary>
    ///     True while <see cref="Current" /> is displayed (turns false slightly before <see cref="Current" /> is cleared,
    ///     so the control can animate out with the old content).
    /// </summary>
    [ObservableProperty] private bool _isActive;

    /// <summary>
    ///     When true, a message equal (same text and action text) to the displayed or a pending one is dropped.
    /// </summary>
    public bool DiscardDuplicates { get; init; }

    /// <summary>
    ///     A message is taken from the queue only while a Snackbar shows the queue in a visible, not minimized main window.
    ///     Messages sent during startup or while the window is hidden to tray wait instead of expiring unseen.
    /// </summary>
    private bool CanDisplay =>
        _attachedCount > 0 && AppHost.MainWindow is {IsVisible: true} && !AppHost.IsMainWindowMinimized;

    public void Enqueue(SnackbarMessage message)
    {
        bool start;
        lock (_lock)
        {
            if (DiscardDuplicates &&
                ((_displayed != null && _displayed.IsDuplicateOf(message)) ||
                 _pending.Any(m => m.IsDuplicateOf(message))))
            {
                return;
            }

            _pending.AddLast(message);
            start = !_isRunning;
            _isRunning = true;
        }

        if (start)
        {
            Dispatcher.UIThread.Post(() => _ = RunAsync());
        }
    }

    /// <summary>
    ///     Hides the current message immediately. UI thread.
    /// </summary>
    public void Dismiss()
    {
        _dismissTcs?.TrySetResult();
    }

    /// <summary>
    ///     Executes the action of the current message (if any) and hides it. UI thread.
    /// </summary>
    public void InvokeAction()
    {
        var message = Current;
        Dismiss();
        if (message?.Action == null) return;
        try
        {
            message.Action();
        }
        catch (Exception ex)
        {
            Model.Shell.Logger?.Error(ex, "Snackbar action failed");
        }
    }

    /// <summary>
    ///     Pauses the display timer (e.g. while the pointer is over the snackbar). Calls are counted. UI thread.
    /// </summary>
    public void Pause()
    {
        _pauseCount++;
    }

    /// <summary>
    ///     Resumes the display timer paused by <see cref="Pause" />. UI thread.
    /// </summary>
    public void Resume()
    {
        if (_pauseCount > 0) _pauseCount--;
    }

    /// <summary>
    ///     Called by a Snackbar control showing this queue when it is attached to the visual tree. UI thread.
    /// </summary>
    internal void Attach()
    {
        _attachedCount++;
    }

    /// <summary>
    ///     Called by a Snackbar control showing this queue when it is detached from the visual tree. UI thread.
    /// </summary>
    internal void Detach()
    {
        if (_attachedCount > 0) _attachedCount--;
    }

    private async Task RunAsync()
    {
        while (true)
        {
            lock (_lock)
            {
                if (_pending.Count == 0)
                {
                    _displayed = null;
                    _isRunning = false;
                    return;
                }
            }

            while (!CanDisplay)
            {
                await Task.Delay(DisplayPollInterval);
            }

            SnackbarMessage message;
            lock (_lock)
            {
                message = _pending.First!.Value;
                _pending.RemoveFirst();
                _displayed = message;
            }

            var dismissTcs = new TaskCompletionSource();
            _dismissTcs = dismissTcs;
            Current = message;
            IsActive = true;

            var remaining = message.Duration;
            while (remaining > TimeSpan.Zero)
            {
                var completed = await Task.WhenAny(dismissTcs.Task, Task.Delay(Tick));
                if (completed == dismissTcs.Task) break;
                if (_pauseCount == 0) remaining -= Tick;
            }

            _dismissTcs = null;
            IsActive = false;
            await Task.Delay(GapBetweenMessages);
            lock (_lock)
            {
                _displayed = null;
            }

            Current = null;
        }
    }
}
