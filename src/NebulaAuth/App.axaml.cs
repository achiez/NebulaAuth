using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using NebulaAuth.Core;
using NebulaAuth.Model;
using NebulaAuth.Model.Exceptions;
using NebulaAuth.ViewModel;
using NLog;

namespace NebulaAuth;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        global::NebulaAuth.Theme.Motion.Install();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Nothing may close the app while only the splash (or no window) is shown
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = StartupAsync(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void AboutMenu_Click(object? sender, EventArgs e)
    {
        if (AppHost.MainWindow is not {IsVisible: true, DataContext: MainVM vm} || DialogService.IsDialogOpen) return;
        vm.OpenLinksViewCommand.Execute(null);
    }

    private static async Task StartupAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        Window? splashScreen = null;
        try
        {
            splashScreen = CreateSplashScreen();
            splashScreen.Show();
            // Let the splash render before the (partly synchronous) initialization starts
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            await Shell.Initialize();
            var mainWindow = new MainWindow();
            desktop.MainWindow = mainWindow;
            mainWindow.Show();
            // Hidden-to-tray main window is not closed, so the app keeps running until MainWindow closes or Exit
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            splashScreen.Close();
#if DEBUG
            DebugPreview.TryShow();
#endif
        }
        catch (Exception ex)
        {
            await FailStartupAsync(desktop, splashScreen, ex);
        }
    }

    // Must never throw: the startup task is not observed, so an exception escaping from here would leave the process
    // running without any window (ShutdownMode is still OnExplicitShutdown)
    private static async Task FailStartupAsync(IClassicDesktopStyleApplicationLifetime desktop, Window? splashScreen,
        Exception ex)
    {
        try
        {
            // Shell.Logger may not exist yet if initialization failed early.
            // Logging can fail too (read-only log directory, broken config): it must not prevent the error message
            // ReSharper disable once ConditionalAccessQualifierIsNonNullableAccordingToAPIContract
            BestEffort(() => (Shell.Logger ?? LogManager.GetLogger("Logger")).Fatal(ex, "Application startup failed"));
            BestEffort(() => splashScreen?.Close());

            var msg = ex.ToString();
            if (ex is CantAlignTimeException)
            {
                BestEffort(() => msg = LocManager.Get("CantAlignTimeError") ?? msg);
            }

            // Visible when started from a terminal (Linux / macOS), also if the error window below cannot be shown
            BestEffort(() => Console.Error.WriteLine(msg));
            await ShowStartupError(msg);
        }
        catch
        {
            // The error window itself failed: nothing more can be shown
        }
        finally
        {
            try
            {
                desktop.Shutdown(1);
            }
            catch
            {
                Environment.Exit(1);
            }
        }
    }

    private static void BestEffort(Action action)
    {
        try
        {
            action();
        }
        catch
        {
            // ignored: used only on the startup failure path
        }
    }

    private static Window CreateSplashScreen()
    {
        var bitmap = new Bitmap(AssetLoader.Open(new Uri("avares://NebulaAuth/Theme/SplashScreen.png")));
        var image = new Image {Source = bitmap, Stretch = Stretch.Uniform};
        var window = new Window
        {
            Title = "NebulaAuth",
            WindowDecorations = WindowDecorations.None,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            CanResize = false,
            ShowInTaskbar = false,
            Topmost = true,
            Background = Brushes.Transparent,
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
            Content = image
        };

        // One image pixel per screen pixel: the splash keeps the image's pixel size (384 px) on every display scaling
        void FitToPixels()
        {
            var scaling = window.RenderScaling > 0 ? window.RenderScaling : 1;
            image.Width = bitmap.PixelSize.Width / scaling;
            image.Height = bitmap.PixelSize.Height / scaling;
        }

        FitToPixels();
        window.ScalingChanged += (_, _) => FitToPixels();
        return window;
    }

    // Plain error window: the main window and its dialog host do not exist yet
    private static Task ShowStartupError(string message)
    {
        var tcs = new TaskCompletionSource();
        var okButton = new Button
        {
            Content = "OK",
            MinWidth = 80,
            HorizontalAlignment = HorizontalAlignment.Right,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            IsDefault = true,
            IsCancel = true
        };
        var panel = new DockPanel {Margin = new Thickness(16)};
        DockPanel.SetDock(okButton, Dock.Bottom);
        panel.Children.Add(okButton);
        panel.Children.Add(new ScrollViewer
        {
            Margin = new Thickness(0, 0, 0, 12),
            Content = new SelectableTextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap
            }
        });

        var window = new Window
        {
            Title = "Error",
            Width = 560,
            SizeToContent = SizeToContent.Height,
            MaxHeight = 600,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Topmost = true,
            Content = panel
        };
        okButton.Click += (_, _) => window.Close();
        window.Closed += (_, _) => tcs.TrySetResult();
        window.Show();
        return tcs.Task;
    }
}
