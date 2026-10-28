using System;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DialogHostAvalonia;

namespace NebulaAuth.Core;

/// <summary>
///     In-window modal dialogs over the single root <see cref="DialogHost" /> hosted by MainWindow.
///     Only one dialog can be open at a time: <see cref="Show" /> while a dialog is open fails.
/// </summary>
public static class DialogService
{
    /// <summary>
    ///     Identifier of the root DialogHost. MainWindow must host exactly one
    ///     <c>&lt;dialogHost:DialogHost Identifier="{x:Static core:DialogService.HostIdentifier}" /&gt;</c>.
    /// </summary>
    public const string HostIdentifier = "RootDialogHost";

    /// <summary>
    ///     The root DialogHost instance registered by MainWindow (null until registered).
    ///     Views may use it for visual tweaks (e.g. SettingsView temporarily disables blur/overlay).
    /// </summary>
    public static DialogHost? Host { get; private set; }

    /// <summary>
    ///     XAML-usable command closing the current dialog; CommandParameter becomes the result of <see cref="Show" />.
    ///     Replacement for <c>md:DialogHost.CloseDialogCommand</c>:
    ///     <c>Command="{x:Static core:DialogService.CloseDialogCommand}" CommandParameter="{x:True}"</c>.
    /// </summary>
    public static ICommand CloseDialogCommand { get; } = new CloseCurrentDialogCommand();

    /// <summary>
    ///     Element focused before the first of a chain of dialogs opened. DialogHost.Avalonia does not restore focus when a
    ///     dialog closes (the account list shortcuts and arrow keys would stop working), so <see cref="Show" /> does it.
    /// </summary>
    private static IInputElement? _focusBeforeDialog;

    /// <summary>
    ///     True when a dialog is currently shown. Safe to call from any thread.
    /// </summary>
    public static bool IsDialogOpen => OnUiThread(() => GetSessionInternal() is {IsEnded: false});

    /// <summary>
    ///     Whether a click on the dimmed overlay closes the current dialog (result = null). Default false.
    /// </summary>
    public static bool CloseOnClickAway
    {
        get => OnUiThread(() => Host?.CloseOnClickAway ?? false);
        set => OnUiThread(() =>
        {
            if (Host != null) Host.CloseOnClickAway = value;
            return true;
        });
    }

    /// <summary>
    ///     Called by MainWindow once its root DialogHost is created.
    /// </summary>
    public static void AttachHost(DialogHost host)
    {
        host.Identifier ??= HostIdentifier;
        Host = host;
    }

    /// <summary>
    ///     Shows <paramref name="content" /> (usually a view with its DataContext set) as a modal dialog.
    ///     The task completes when the dialog closes, with the close parameter (e.g. true/false, a string, a VM or null).
    ///     Must be called on the UI thread.
    /// </summary>
    public static Task<object?> Show(object content)
    {
        var host = Host ?? throw new InvalidOperationException("Root DialogHost is not attached (MainWindow not created)");
        return ShowCore(content, host);
    }

    private static async Task<object?> ShowCore(object content, DialogHost host)
    {
        _focusBeforeDialog ??= TopLevel.GetTopLevel(host)?.FocusManager?.GetFocusedElement();
        // Views are TemplatedControls, which clip to their bounds by default: DialogHeader (Margin -1) must be able to
        // draw over the 1px border of the dialog card
        if (content is Control view) view.ClipToBounds = false;
        try
        {
            return await DialogHost.Show(content, host);
        }
        finally
        {
            Dispatcher.UIThread.Post(RestoreFocus, DispatcherPriority.Input);
        }
    }

    private static void RestoreFocus()
    {
        // A chained dialog is open: restore after that one closes
        if (IsDialogOpen) return;
        var element = _focusBeforeDialog;
        _focusBeforeDialog = null;
        if (element is Control {IsEffectivelyVisible: true, IsEffectivelyEnabled: true} control &&
            control.IsAttachedToVisualTree())
        {
            control.Focus();
        }
    }

    /// <summary>
    ///     Current dialog session or null when no dialog is open. Must be called on the UI thread.
    /// </summary>
    public static DialogSession? GetSession()
    {
        return GetSessionInternal();
    }

    /// <summary>
    ///     Closes the current dialog with <paramref name="result" />. Does nothing when no dialog is open.
    ///     Safe to call from any thread.
    /// </summary>
    public static void Close(object? result = null)
    {
        OnUiThread(() =>
        {
            var session = GetSessionInternal();
            if (session is {IsEnded: false})
            {
                session.Close(result);
            }

            return true;
        });
    }

    private static DialogSession? GetSessionInternal()
    {
        var host = Host;
        return host?.CurrentSession;
    }

    private static T OnUiThread<T>(Func<T> func)
    {
        return Dispatcher.UIThread.CheckAccess() ? func() : Dispatcher.UIThread.Invoke(func);
    }

    private sealed class CloseCurrentDialogCommand : ICommand
    {
        public bool CanExecute(object? parameter)
        {
            return true;
        }

        public void Execute(object? parameter)
        {
            Close(parameter);
        }

// Always executable: nothing to notify
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }
    }
}
