using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DialogHostAvalonia;
using NebulaAuth.Core;
using NebulaAuth.Localization;
using NebulaAuth.Model;
using NebulaAuth.Utility;
using NebulaAuth.View.Dialogs;
using NebulaAuth.ViewModel;
using NebulaAuth.ViewModel.Other;

namespace NebulaAuth;

public partial class MainWindow : Window
{
    private const int UPDATE_BLINK_SECONDS = 6;
    private const double SNACKBAR_FOOTER_GAP = 12;

    private readonly List<MenuItem> _groupMenuItems = [];
    private DispatcherTimer? _updateBlinkTimer;
    private bool _ctrlClickSuppressed;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainVM();
        AppHost.RegisterMainWindow(this);
        DialogService.AttachHost(DialogHostInstance);
        TrayManager.InitializeTray(this);
        ThemeManager.InitializeTheme();
        ControlsLocale.Initialize();
        Title = Title + " " + Assembly.GetExecutingAssembly().GetName().Version?.ToString(3);
        UpdateManager.PendingUpdateDetected += OnPendingUpdateDetected;
        InitializeInput();
        ViewModel!.PropertyChanged += ViewModel_OnPropertyChanged;
        Closing += MainWindow_OnClosing;
        DebugOnCreated();
    }

    private MainVM? ViewModel => DataContext as MainVM;

    partial void DebugOnCreated();

    private static void MainWindow_OnClosing(object? sender, WindowClosingEventArgs e)
    {
        // Enabling / disabling encryption must not be cut off (it would be resumed on the next start)
        if (!StorageOperationLock.IsBusy)
        {
            // Closing the main window exits the app: the clipboard is still reachable through this window here
            DecryptedMafileClipboard.OnAppExit();
            return;
        }

        e.Cancel = true;
        SnackbarController.SendSnackbar(StorageOperationLock.BusyMessage);
    }

    /// <summary>
    ///     Platform hotkeys (Ctrl on Windows/Linux, Cmd on macOS). Same lookup as Avalonia's ItemSelectionEventTriggers.
    /// </summary>
    private PlatformHotkeyConfiguration? Hotkeys =>
        (this.GetPlatformSettings() ?? Application.Current?.PlatformSettings)?.HotkeyConfiguration;

    private KeyModifiers CommandModifiers => Hotkeys?.CommandModifiers ?? KeyModifiers.Control;

    private void InitializeInput()
    {
        // Window shortcuts: bubbling KeyDown, so a focused TextBox keeps its own Ctrl+V (paste text)
        AddHandler(KeyDownEvent, OnWindowKeyDown);
        SearchField.AddHandler(KeyDownEvent, SearchField_OnKeyDown, RoutingStrategies.Tunnel);
        MafileListBox.AddHandler(PointerPressedEvent, MafileListBox_OnPreviewPointerPressed, RoutingStrategies.Tunnel);
        MafileListBox.AddHandler(PointerReleasedEvent, MafileListBox_OnPreviewPointerReleased, RoutingStrategies.Tunnel);
        TimerBox.AddHandler(TextInputEvent, TimerBox_OnPreviewTextInput, RoutingStrategies.Tunnel);
        MafileContextMenu.Opening += MafileContextMenu_OnOpening;
        Footer.SizeChanged += (_, e) => UpdateSnackbarMargin(e.NewSize.Height);
        // The theme opens the app bar drop-downs 8 px left of their menu item (template value, a style cannot change
        // it), which puts the first one against the window edge: align them with the item instead
        foreach (var menuItem in MainMenu.Items.OfType<MenuItem>())
        {
            menuItem.TemplateApplied += (_, e) =>
            {
                if (e.NameScope.Find<Popup>("PART_Popup") is { } popup) popup.HorizontalOffset = 0;
            };
        }

        ContentRoot.AddHandler(DragDrop.DragEnterEvent, ContentRoot_OnDragEnter);
        ContentRoot.AddHandler(DragDrop.DragOverEvent, ContentRoot_OnDragOver);
        ContentRoot.AddHandler(DragDrop.DragLeaveEvent, ContentRoot_OnDragLeave);
        ContentRoot.AddHandler(DragDrop.DropEvent, ContentRoot_OnDrop);
        DialogHostInstance.PropertyChanged += DialogHostInstance_OnPropertyChanged;
        KeyBindingHelper.AddMacBackspaceForDelete(ProxyBox);

        // The account list gestures are written with Ctrl: use Cmd on macOS
        var commandModifiers = CommandModifiers;
        foreach (var binding in MafileListBox.KeyBindings)
        {
            binding.Gesture = KeyBindingHelper.WithCommandModifier(binding.Gesture, commandModifiers)!;
        }

        foreach (var item in new[] {CopyLoginMenuItem, CopyPasswordMenuItem, CopyMafileMenuItem})
        {
            item.InputGesture = KeyBindingHelper.WithCommandModifier(item.InputGesture, commandModifiers);
        }
    }

    private void ViewModel_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainVM.TimerCheckSeconds)) return;
        // MainVM rejects intervals below 5 s and raises the change from inside the setter, which Avalonia ignores while
        // it updates the source. Re-read the value afterwards; skip it when the text already matches (keeps the caret).
        Dispatcher.UIThread.Post(() =>
        {
            if (ViewModel is { } vm && TimerBox.Text != vm.TimerCheckSeconds.ToString())
                BindingOperations.GetBindingExpressionBase(TimerBox, TextBox.TextProperty)?.UpdateTarget();
        });
    }

    private void OnPendingUpdateDetected()
    {
        Dispatcher.UIThread.Post(StartUpdateBlink);
    }

    /// <summary>
    ///     Shows the update badge and blinks it together with the "by achies" link (DodgerBlue / OrangeRed every second)
    ///     for 6 seconds, then hides the badge and restores the link color.
    /// </summary>
    private void StartUpdateBlink()
    {
        _updateBlinkTimer?.Stop();
        UpdateBadgeIcon.IsVisible = true;
        var tick = 0;
        ApplyBlinkColor(tick);
        _updateBlinkTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, (s, _) =>
        {
            tick++;
            if (tick < UPDATE_BLINK_SECONDS)
            {
                ApplyBlinkColor(tick);
                return;
            }

            ((DispatcherTimer) s!).Stop();
            UpdateBadgeIcon.IsVisible = false;
            UpdateBadgeIcon.Foreground = Brushes.DodgerBlue;
            ByAchiesLink.ClearValue(ForegroundProperty);
        });
        _updateBlinkTimer.Start();
    }

    private void ApplyBlinkColor(int tick)
    {
        var brush = tick % 2 == 0 ? Brushes.DodgerBlue : Brushes.OrangeRed;
        UpdateBadgeIcon.Foreground = brush;
        ByAchiesLink.Foreground = brush;
    }

    private void UpdateSnackbarMargin(double footerHeight)
    {
        var margin = SnackbarHost.Margin;
        SnackbarHost.Margin = new Thickness(margin.Left, margin.Top, margin.Right, footerHeight + SNACKBAR_FOOTER_GAP);
    }

    #region Keyboard / mouse

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || DialogService.IsDialogOpen) return;
        var hotkeys = Hotkeys;
        var commandModifiers = CommandModifiers;

        var isPaste = hotkeys != null
            ? hotkeys.Paste.Any(g => g.Matches(e))
            : e.Key == Key.V && e.KeyModifiers == commandModifiers;
        if (isPaste)
        {
            var command = ViewModel?.PasteMafilesFromClipboardCommand;
            if (command?.CanExecute(null) != true) return;
            e.Handled = true;
            command.Execute(null);
            return;
        }

        // Find (Ctrl+F)
        if (e.Key == Key.F && e.KeyModifiers == commandModifiers)
        {
            e.Handled = true;
            SearchField.Focus();
        }
    }

    private void SearchField_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !SearchField.IsFocused) return;
        // Leave the box for the selected account (arrow keys and Ctrl+C/X keep working).
        // The ListBox itself is not focusable in Avalonia, only its items are
        e.Handled = true;
        var index = MafileListBox.SelectedIndex;
        if (index >= 0 && MafileListBox.ContainerFromIndex(index)?.Focus() == true) return;
        FocusManager?.Focus(null); // no (realized) selected account: clear focus
    }

    /// <summary>
    ///     Ctrl+click on the already selected account must not deselect it.
    ///     Avalonia toggles selection with the platform command modifier (Cmd on macOS), so check that one.
    /// </summary>
    private void MafileListBox_OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _ctrlClickSuppressed = false;
        var toggleModifiers = CommandModifiers;
        if ((e.KeyModifiers & toggleModifiers) != toggleModifiers) return;
        if (!e.GetCurrentPoint(MafileListBox).Properties.IsLeftButtonPressed) return;
        if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(true) is not {IsSelected: true}) return;
        _ctrlClickSuppressed = true;
        e.Handled = true;
    }

    private void MafileListBox_OnPreviewPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_ctrlClickSuppressed) return;
        _ctrlClickSuppressed = false;
        e.Handled = true;
    }

    /// <summary>
    ///     Timer seconds field: digits only.
    /// </summary>
    private void TimerBox_OnPreviewTextInput(object? sender, TextInputEventArgs e)
    {
        if (!int.TryParse(e.Text, out _)) e.Handled = true;
    }

    /// <summary>
    ///     "Add to group" = "Create group", separator, then one item per existing group.
    /// </summary>
    private void MafileContextMenu_OnOpening(object? sender, EventArgs e)
    {
        foreach (var item in _groupMenuItems)
        {
            AddToGroupMenuItem.Items.Remove(item);
        }

        _groupMenuItems.Clear();
        if (ViewModel is not { } vm) return;

        var mafile = MafileListBox.SelectedItem;
        foreach (var group in vm.Groups)
        {
            var item = new MenuItem
            {
                Header = group,
                Command = vm.AddToGroupCommand,
                CommandParameter = new[] {group, mafile}
            };
            _groupMenuItems.Add(item);
            AddToGroupMenuItem.Items.Add(item);
        }
    }

    #endregion

    #region Drag'n'Drop

    private void DialogHostInstance_OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != DialogHost.IsOpenProperty) return;
        var isOpen = e.GetNewValue<bool>();
        DragDrop.SetAllowDrop(ContentRoot, !isOpen);
        if (isOpen) SetDropOverlayVisible(false);
    }

    private DateTime _lastRefusedDragLog;

    /// <summary>
    ///     No drop target while a dialog is open or an import runs (its check / writing has no dialog).
    /// </summary>
    private bool CanAcceptDrop(DragEventArgs e)
    {
        return !DialogService.IsDialogOpen && ViewModel?.IsImportBusy != true &&
               DroppedFiles.MayContainFiles(e.DataTransfer);
    }

    private void ContentRoot_OnDragEnter(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!CanAcceptDrop(e))
        {
            // Once per drag (the event repeats for every control under the pointer): the formats show what was offered
            if (!DialogService.IsDialogOpen && ViewModel?.IsImportBusy != true &&
                DateTime.UtcNow - _lastRefusedDragLog > TimeSpan.FromSeconds(5))
            {
                _lastRefusedDragLog = DateTime.UtcNow;
                Shell.Logger.Info("Drag and drop: no files offered ({formats})", DroppedFiles.DescribeFormats(e.DataTransfer));
            }
            e.DragEffects = DragDropEffects.None;
            return;
        }

        e.DragEffects = DragDropEffects.Copy;
        SetDropOverlayVisible(true);
    }

    private void ContentRoot_OnDragOver(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        e.DragEffects = CanAcceptDrop(e) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void ContentRoot_OnDragLeave(object? sender, DragEventArgs e)
    {
        SetDropOverlayVisible(false);
    }

    private async void ContentRoot_OnDrop(object? sender, DragEventArgs e)
    {
        SetDropOverlayVisible(false);
        e.Handled = true;
        if (DialogService.IsDialogOpen) return;
        // Read before the first await: the platform data of a drop is only valid during the event
        var filePaths = DroppedFiles.GetLocalPaths(e.DataTransfer);
        if (filePaths.Length == 0)
        {
            SnackbarController.SendSnackbar(LocManager.GetOrDefault("No files could be read from the drop",
                "MainWindow", "Global", "DropNoFiles"));
            return;
        }

        e.DragEffects = DragDropEffects.Copy;
        if (ViewModel is { } mainVm)
        {
            // A drop during an import is rejected by the import itself (with a message)
            await mainVm.ImportMafiles(filePaths);
        }
    }

    private void SetDropOverlayVisible(bool visible)
    {
        DragNDropOverlay.IsVisible = visible;
    }

    #endregion
}
