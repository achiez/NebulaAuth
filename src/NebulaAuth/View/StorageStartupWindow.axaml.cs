using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using NebulaAuth.ViewModel.Other;

namespace NebulaAuth.View;

public partial class StorageStartupWindow : Window
{
    private bool _closeAllowed;

    public StorageStartupWindow() : this(new StorageStartupVM())
    {
    }

    public StorageStartupWindow(StorageStartupVM vm)
    {
        InitializeComponent();
        DataContext = ViewModel = vm;
        vm.PropertyChanged += ViewModel_OnPropertyChanged;
        vm.QuitRequested += (_, _) => CloseWindow();
        Opened += (_, _) =>
        {
            FocusPassword();
            OnDebugOpened();
        };
        PasswordBoxHints.PropertyChanged += (_, e) =>
        {
            if (e.Property == PasswordHints.IsCapsLockOnProperty) vm.SetCapsLockOn(PasswordBoxHints.IsCapsLockOn);
        };
        Closing += OnClosing;
        Closed += (_, _) => vm.OnWindowClosed();
    }

    public StorageStartupVM ViewModel { get; }

    /// <summary>
    ///     Closes the window from the startup flow (also during the progress page, which the user can't close).
    /// </summary>
    public void CloseWindow()
    {
        _closeAllowed = true;
        Close();
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        // An interrupted mode change is being finished: closing now would only restart it on the next start
        if (!_closeAllowed && ViewModel.IsProgressPage) e.Cancel = true;
    }

    private void ViewModel_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(StorageStartupVM.PasswordError) when ViewModel.PasswordError != null:
            case nameof(StorageStartupVM.IsChecking) when !ViewModel.IsChecking && ViewModel.IsUnlockPage:
                FocusPassword();
                break;
        }
    }

    private void FocusPassword()
    {
        if (!ViewModel.IsUnlockPage) return;
        Dispatcher.UIThread.Post(() =>
        {
            PasswordBox.Focus();
            PasswordBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    partial void OnDebugOpened();
}
