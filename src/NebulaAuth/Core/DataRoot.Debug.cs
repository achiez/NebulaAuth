#if DEBUG
using System;

namespace NebulaAuth.Core;

public static partial class DataRoot
{
    /// <summary>
    ///     Debug builds only: simulates another OS layout for the data folder rules.
    ///     <c>NEBULA_DATAROOT_SIM_OS=macos|linux|windows</c> (platform rules), <c>NEBULA_DATAROOT_SIM_BASEDIR=&lt;path&gt;</c>
    ///     (exe folder, e.g. <c>/private/var/folders/x/T/AppTranslocation/ID/d/NebulaAuth.app/Contents/MacOS</c> shows the
    ///     translocation window), <c>NEBULA_DATAROOT_SIM_USERDATA=&lt;path&gt;</c> (default macOS / Linux user data folder).
    ///     The simulated macOS / Linux rules ignore the Velopack locator of the real process. <c>APPIMAGE</c> and
    ///     <c>NEBULA_DATA_DIR</c> are read from the real environment.
    /// </summary>
    static partial void ApplyDebugEnvironment(ref DataRootEnvironment environment)
    {
        var os = Environment.GetEnvironmentVariable("NEBULA_DATAROOT_SIM_OS");
        var baseDir = Environment.GetEnvironmentVariable("NEBULA_DATAROOT_SIM_BASEDIR");
        var userData = Environment.GetEnvironmentVariable("NEBULA_DATAROOT_SIM_USERDATA");
        if (!string.IsNullOrWhiteSpace(os))
        {
            var platform = os.Trim().ToLowerInvariant() switch
            {
                "macos" or "mac" or "osx" => DataRootPlatform.MacOS,
                "linux" => DataRootPlatform.Linux,
                _ => DataRootPlatform.Windows
            };
            environment = environment with
            {
                Platform = platform,
                VelopackRootAppDir = platform == DataRootPlatform.Windows ? environment.VelopackRootAppDir : null
            };
        }

        if (!string.IsNullOrWhiteSpace(baseDir)) environment = environment with {BaseDirectory = baseDir};
        if (!string.IsNullOrWhiteSpace(userData)) environment = environment with {UserDataFolder = userData};
    }
}
#endif
