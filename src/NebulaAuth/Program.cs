using System;
using Avalonia;
using Avalonia.Media;

namespace NebulaAuth;

internal static class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            // Inter everywhere (windows, popups, tooltips) so the UI looks the same on Windows, macOS and Linux;
            // glyphs missing in Inter (e.g. CJK) fall back to system fonts
            .With(new FontManagerOptions {DefaultFamilyName = "fonts:Inter#Inter"})
            .LogToTrace();
    }
}
