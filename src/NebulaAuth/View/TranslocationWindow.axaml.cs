using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NebulaAuth.Utility;

namespace NebulaAuth.View;

public partial class TranslocationWindow : Window
{
    private readonly TaskCompletionSource _completion = new();

    public TranslocationWindow()
    {
        InitializeComponent();
        Closed += (_, _) => _completion.TrySetResult();
    }

    public TranslocationWindow(string appPath) : this()
    {
        AppPathText.Text = appPath;
    }

    /// <summary>
    ///     Completes when the window is closed (the app then exits).
    /// </summary>
    public Task Completion => _completion.Task;

    private void Quit_OnClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OpenDownloads_OnClick(object? sender, RoutedEventArgs e)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        PlatformUtils.OpenFolder(Path.Combine(home, "Downloads"));
    }
}
