using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NebulaAuth.Core;
using NebulaAuth.Model.MAAC;
using NebulaAuth.Model.MafileExport;
using NebulaAuth.Model.Mafiles;
using NebulaAuth.Model.Mafiles.Protection;
using NLog;
using NLog.Extensions.Logging;
using SteamLib.Core;
using SteamLib.SteamMobile;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace NebulaAuth.Model;

public static class Shell
{
    // Usable before Initialize (data folder choice, startup errors): NLog writes to the temp folder until DataRoot is set
    public static Logger Logger { get; private set; } = LogManager.GetLogger("Logger");
    public static ILogger ExtensionsLogger { get; private set; } = null!;

    /// <summary>
    ///     First startup phase: logging, localization, error handlers. Nothing here reads mafiles; the storage is unlocked
    ///     and reconciled after this (<see cref="MafileStorageService" />), then <see cref="InitializeAccounts" /> runs.
    /// </summary>
    public static void InitializeBase()
    {
        // log.log of 1.x versions (logs are in the "logs" folder now)
        File.Delete(DataRoot.Combine("log.log"));
        LocManager.Init();
        LocManager.SetApplicationLocalization(Settings.Instance.Language);
        Logger.Info("NebulaAuth {version} started. Data folder: {dataRoot}. Program: {exe}",
            UpdateManager.InstalledVersionText, DataRoot.Describe(), Environment.ProcessPath);
        var lp = new NLogLoggerProvider();
        var logger = lp.CreateLogger("SteamLib");
        SteamLibErrorMonitor.MonitorLogger = logger;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomainOnUnhandledException;

        var loggerFactory = new NLogLoggerFactory();
        ExtensionsLogger = loggerFactory.CreateLogger("Logger");
    }

    /// <summary>
    ///     Second startup phase, after the storage is unlocked and prepared: loads the accounts and starts the services
    ///     that use them (MAAC, proxies, exporter).
    /// </summary>
    public static async Task InitializeAccounts()
    {
        try
        {
            await TimeAligner.AlignTimeAsync();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to align time with Steam");
            TimeAligner.SetTimeDifference(0);
        }

        var threads = Environment.ProcessorCount > 0 ? Environment.ProcessorCount : 1;
        await Storage.Initialize(threads);
        UpdateManager.Initialize();
        ProxyAssignmentCache.Initialize(Storage.MaFiles);
        MAACStorage.Initialize();
        MafileExporterStorage.Initialize();
        _ = LinksManager.FetchAsync();

        ExtensionsLogger.LogDebug("Application started");
    }

    private static void CurrentDomainOnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Logger.Fatal((Exception) e.ExceptionObject);
        LogManager.Shutdown();
    }
}