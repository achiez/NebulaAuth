using SteamLib.ProtoCore.Enums;
using SteamLib.ProtoCore.Services;
using SteamLib.Utility;

namespace SteamLib.Factory.Helpers;

public static class DeviceDetailsDefaultBuilder
{
    [Obsolete("Not recommended")]
    public static DeviceDetails CreateDefault(string? deviceFriendlyName)
    {
        return new DeviceDetails(deviceFriendlyName ?? string.Empty, EAuthTokenPlatformType.WebBrowser, (int?) null,
            null);
    }

    public static DeviceDetails CreateCommunityDetails(string userAgent)
    {
        return new DeviceDetails(userAgent, EAuthTokenPlatformType.WebBrowser, (int?) null, null);
    }

    public static DeviceDetails CreateMobileDetails(string deviceName)
    {
        return new DeviceDetails(deviceName, EAuthTokenPlatformType.MobileApp, EOSType.AndroidUnknown, 528);
    }

    /// <summary>
    ///     Device name of <see cref="GetMobileDefaultDevice" /> (used when an account has no device name of its own).
    /// </summary>
    public const string DEFAULT_MOBILE_DEVICE_NAME = "Pixel 6 Pro";

    public static DeviceDetails GetMobileDefaultDevice()
    {
        return GetMobileDevice(null);
    }

    /// <summary>
    ///     Mobile app device details with the given friendly name (<see cref="DEFAULT_MOBILE_DEVICE_NAME" /> when empty);
    ///     the platform, OS and gaming device type are those of <see cref="GetMobileDefaultDevice" />.
    /// </summary>
    public static DeviceDetails GetMobileDevice(string? deviceFriendlyName)
    {
        var name = string.IsNullOrWhiteSpace(deviceFriendlyName) ? DEFAULT_MOBILE_DEVICE_NAME : deviceFriendlyName;
        return new DeviceDetails(name, EAuthTokenPlatformType.MobileApp, -500, 528);
    }
}