using System;
using System.Reflection;
using NebulaAuth.NebulaLink.Protocol;

namespace NebulaAuth.NebulaLink;

/// <summary>What NebulaAuth tells Achiefy about this computer (shown in the cabinet next to the device).</summary>
public static class LinkDeviceInfo
{
    /// <summary>
    ///     The NebulaAuth version sent at pairing and in Hello; the app sets the version its title and updater show. The
    ///     default is the version of the entry assembly.
    /// </summary>
    public static string AppVersion
    {
        get => _appVersion;
        set => _appVersion = Truncate(string.IsNullOrWhiteSpace(value) ? GetAppVersion() : value.Trim(), 32);
    }

    private static string _appVersion = GetAppVersion();

    public static string Os =>
        OperatingSystem.IsWindows() ? NebulaOs.Windows : OperatingSystem.IsMacOS() ? NebulaOs.MacOs : NebulaOs.Linux;

    public static string OsVersion => Truncate(Environment.OSVersion.Version.ToString(), 64);

    /// <summary>Default device name: the computer name (1..64 chars).</summary>
    public static string DefaultDeviceName
    {
        get
        {
            string name;
            try
            {
                name = Environment.MachineName;
            }
            catch
            {
                name = "";
            }

            name = string.IsNullOrWhiteSpace(name) ? "NebulaAuth" : name.Trim();
            return Truncate(name, 64);
        }
    }

    public static NebulaDeviceInfo Create(string? deviceName)
    {
        return new NebulaDeviceInfo
        {
            DeviceName = string.IsNullOrWhiteSpace(deviceName) ? DefaultDeviceName : Truncate(deviceName.Trim(), 64),
            Os = Os,
            OsVersion = OsVersion,
            AppVersion = AppVersion
        };
    }

    private static string GetAppVersion()
    {
        // The version of the application (NebulaAuth), not of this library
        var assembly = Assembly.GetEntryAssembly() ?? typeof(LinkDeviceInfo).Assembly;
        return Truncate(assembly.GetName().Version?.ToString(3) ?? "0.0.0", 32);
    }

    private static string Truncate(string value, int max)
    {
        return value.Length <= max ? value : value[..max];
    }
}
