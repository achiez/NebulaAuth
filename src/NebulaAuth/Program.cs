using System;
using Avalonia;
using Avalonia.Media;
using NebulaAuth.Core;
using Velopack;

namespace NebulaAuth;

internal static class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        // Must run first: handles the Velopack install/update hooks (and exits for them) and sets up the locator.
        // Downloaded updates are installed only when the user asks for it (UpdateManager), never silently on startup.
        VelopackApp.Build()
            .SetArgs(args)
            .SetAutoApplyOnStartup(false)
            .Run();
        // Before anything reads or writes user data (settings, logs, maFiles)
        DataRoot.Resolve();
        var exitCode = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        // "Restart and update": the updater starts only now, after the app has shut down
        Core.UpdateManager.StartPendingApply();
        return exitCode;
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .With(new FontManagerOptions {DefaultFamilyName = "fonts:Inter#Inter"})
            .LogToTrace();
    }
}
