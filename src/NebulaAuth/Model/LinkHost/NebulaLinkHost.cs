using System;
using System.IO;
using System.Threading.Tasks;
using AchiesUtilities.Web.Models;
using NebulaAuth.Core;
using NebulaAuth.Model.Entities;
using NebulaAuth.Model.MAAC;
using NebulaAuth.Model.Mafiles;
using NebulaAuth.Model.Mafiles.Protection;
using NebulaAuth.NebulaLink;
using NebulaAuth.NebulaLink.Confirmations;
using NebulaAuth.NebulaLink.Credentials;
using NebulaAuth.Utility;
using SteamLib;

namespace NebulaAuth.Model.LinkHost;

/// <summary>
///     The composition root of Nebula Link (library NebulaAuth.NebulaLink) in the app: accounts, sessions, proxies,
///     storage, logging and the auto-confirmer are wired here, and the rest of the app reaches Nebula Link only through
///     this class. Reads local files only; the connection starts only when the user paired.
/// </summary>
public static partial class NebulaLinkHost
{
    private static NebulaLinkIntegration? _integration;

    /// <summary>The login code registry of the app: Nebula Link and the app's own password logins share it.</summary>
    public static GuardCodeService GuardCodes { get; } = new();

    /// <summary>The integration (null before startup).</summary>
    public static NebulaLinkIntegration? Integration => _integration;

    /// <summary>Mobile confirmations of the manual screen (the integration's service).</summary>
    public static ConfirmationService Confirmations =>
        _integration?.Confirmations ?? throw new InvalidOperationException("Nebula Link is not initialized");

    public static void Initialize()
    {
        NebulaLinkLog.Sanitizer = text => LogSanitizer.Sanitize(text);
        NebulaLinkFiles.Write = (path, data) => AtomicFile.Write(path, data);
        NebulaLinkFiles.TryDelete = path => AtomicFile.TryDelete(path);
        // The version the window title and the updater show
        LinkDeviceInfo.AppVersion = UpdateManager.InstalledVersionText;

        var storage = MafileStorageService.Current;
        var integration = new NebulaLinkIntegration(new NebulaLinkOptions
        {
            DataDirectory = DataRoot.Path,
            Http = new NebulaHttpFactory(),
            Selector = DeviceCredentialStoreSelector.CreateDefault(() =>
                storage.CanProtectNebulaDevice ? new EncryptedDeviceCredentialStore(storage) : null),
            Mafiles = Shell.SnapshotMafiles,
            SessionBackend = new AppHeadlessSessionBackend(),
            InteractiveSessions = new AppInteractiveSessionRunner(),
            ClientPool = new AccountSteamClientPool(m => (m as Mafile)?.Proxy?.Data ?? MaClient.DefaultProxy),
            GuardCodes = GuardCodes,
            FileInfo = FileInfoOf,
            AccountLinked = mafile =>
            {
                if (mafile is Mafile m) MAACStorage.ForgetAccount(m);
            },
            PurgeEncryptedCredential = () => AtomicFile.TryDelete(storage.NebulaDeviceFile)
        });
        _integration = integration;
        storage.ModeChanged += mode => _ = integration.OnStorageModeChangedAsync(mode == StorageMode.Plain);
        // Added / removed / replaced mafiles change the state of linked accounts
        Storage.MaFiles.CollectionChanged += (_, _) => NotifyLocalChange();
        integration.Initialize();
    }

    /// <summary>The account of this mafile is linked to Achiefy (its auto-confirmation stays off).</summary>
    public static bool IsLinked(MobileDataExtended? mafile)
    {
        return _integration?.IsLinked(mafile) == true;
    }

    /// <summary>
    ///     A mafile, its session or a saved password changed: Achiefy gets the new state of the linked accounts (debounced,
    ///     in the background). Any thread.
    /// </summary>
    public static void NotifyLocalChange()
    {
        _integration?.NotifyLocalChange();
    }

    /// <summary>File name and modification time of a mafile in the storage (duplicate mafiles are listed with them).</summary>
    private static MafileFileInfo? FileInfoOf(MobileDataExtended account)
    {
        if (account is not Mafile mafile) return null;
        var path = Storage.TryGetMafilePath(mafile);
        DateTimeOffset? modified = path != null && File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
        return new MafileFileInfo(mafile.Filename, modified);
    }

    /// <summary>App exit: the connection is closed and the request history written (off the UI thread: no deadlock).</summary>
    public static void Shutdown()
    {
        if (_integration is { } integration)
            Task.Run(() => integration.ShutdownAsync()).Wait(TimeSpan.FromSeconds(4));
    }

    private sealed class AppInteractiveSessionRunner : IInteractiveSessionRunner
    {
        public Task<T> RunAsync<T>(Func<Task<T>> func, MobileDataExtended mafile, HttpClientHandlerPair chp)
        {
            return SessionHandler.Handle(func, (Mafile) mafile, chp);
        }
    }
}
