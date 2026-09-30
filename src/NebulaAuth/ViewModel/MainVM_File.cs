using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using AchiesUtilities.Extensions;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;
using NebulaAuth.Core;
using NebulaAuth.Model;
using NebulaAuth.Model.Entities;
using NebulaAuth.Model.Exceptions;
using NebulaAuth.Model.Mafiles;
using NebulaAuth.Model.Mafiles.Protection;
using NebulaAuth.Model.MafilesLegacy;
using NebulaAuth.Utility;
using NebulaAuth.View;
using NebulaAuth.View.Dialogs;
using NebulaAuth.ViewModel.Other;
using SteamLibForked.Exceptions.Authorization;

namespace NebulaAuth.ViewModel;

public partial class MainVM //File //TODO: Refactor
{
    private record MafileReadResult(
        Mafile? Mafile,
        bool SdaPasswordPrompted,
        SDAEncryptionHelper.Context? SdaContext);

    private record MafileImportPlanItem(Mafile Mafile, bool RequiresRelogin, bool HasConflict);

    public Settings Settings => Settings.Instance;

    // "*.mafile.encrypted": encrypted mafiles of this storage (e.g. from maFiles_removed). Encrypted files of another
    // storage are recognized by the header and refused with a reason; SDA encrypted mafiles keep their own import path.
    // File picker globs are case-sensitive on Linux (GTK / portals): match any case of ".mafile" there
    private static readonly string[] MafilePatterns = OperatingSystem.IsLinux()
        ? ["*.[mM][aA][fF][iI][lL][eE]", "*.[mM][aA][fF][iI][lL][eE].[eE][nN][cC][rR][yY][pP][tT][eE][dD]"]
        : ["*.mafile", "*.maFile", "*.mafile.encrypted"];


    [RelayCommand]
    private void OpenMafileFolder()
    {
        var mafile = SelectedMafile;

        var path = Storage.MafilesDirectory;
        string? mafilePath = null;
        if (mafile != null)
        {
            mafilePath = Storage.TryGetMafilePath(mafile);
        }

        try
        {
            if (mafilePath != null)
            {
                PlatformUtils.RevealFile(mafilePath);
            }
            else
            {
                PlatformUtils.OpenFolder(path);
            }
        }
        catch (Exception ex)
        {
            SnackbarController.SendSnackbar(ex.Message);
        }
    }


    [RelayCommand]
    private async Task AddMafile()
    {
        var files = await FileDialogs.OpenFilesAsync("Mafile", MafilePatterns);
        if (files.Length > 0)
        {
            await AddMafile(files);
        }
    }

    //TODO: extract flow and refactor, this method is too long and does too many things
    public async Task AddMafile(string[] path)
    {
        var summary = new MafileImportSummary();
        var items = new List<MafileImportPlanItem>();
        SDAEncryptionHelper.Context? sdaContext = null;
        var sdaPasswordPrompted = false;
        foreach (var str in path)
        {
            try
            {
                var (mafile, passwordPrompted, context) = await TryReadMafile(str, sdaContext, sdaPasswordPrompted);
                sdaContext = context;
                sdaPasswordPrompted = passwordPrompted;
                if (mafile == null)
                {
                    summary.ErrorOne();
                    continue;
                }

                items.Add(new MafileImportPlanItem(mafile, false, HasImportConflict(mafile)));
            }
            catch (FormatException)
            {
                summary.ErrorOne();
            }
            catch (MafileNeedReloginException ex)
            {
                if (path.Length == 1 && ex.Mafile != null)
                {
                    items.Add(new MafileImportPlanItem(ex.Mafile, true, HasImportConflict(ex.Mafile)));
                }
                else
                {
                    summary.ErrorOne();
                    SnackbarController.SendSnackbar(
                        $"{GetLocalization("MafileImportError")} {Path.GetFileName(str)}{GetLocalization("MissingSessionInMafile")}",
                        TimeSpan.FromSeconds(4));
                }
            }
        }

        if (items.Count == 0)
        {
            ShowImportSummary(summary);
            return;
        }

        var conflictCount = items.Count(i => i.HasConflict);
        var showImportDialog = Settings.ConfirmMafileImport || conflictCount > 0;
        MafileImportDialogResult? importOptions = null;
        if (showImportDialog)
        {
            importOptions = await DialogsController.ShowMafileImportDialog(Groups, items.Count, conflictCount);
            if (importOptions == null)
            {
                SnackbarController.SendSnackbar(LocManager.GetCommonOrDefault("Canceled", "Canceled"));
                return;
            }
        }

        var importGroup = importOptions?.Group;
        var overwriteConflicts = importOptions?.OverwriteConflicts ?? false;
        foreach (var item in items)
        {
            ApplyImportGroup(item.Mafile, importGroup);
            if (item.RequiresRelogin)
            {
                if (item.HasConflict && !overwriteConflicts)
                {
                    summary.SkippedOne();
                    continue;
                }

                if (await HandleAddMafileWithoutSession(item.Mafile))
                {
                    summary.AddedOne();
                }
                else
                {
                    summary.ErrorOne();
                }

                continue;
            }

            var res = await Storage.AddNewMafileFromData(item.Mafile, overwriteConflicts);
            summary.Apply(res);
        }

        if (summary.Added > 0)
        {
            QueryGroups();
            if (!string.IsNullOrWhiteSpace(importGroup))
            {
                SelectedGroup = importGroup;
            }
        }

        ShowImportSummary(summary);
    }

    private static void ApplyImportGroup(Mafile mafile, string? group)
    {
        if (string.IsNullOrWhiteSpace(group)) return;
        mafile.Group = group;
    }

    private static bool HasImportConflict(Mafile mafile)
    {
        var fileName = MafilesStorageHelper.CreateMafileFileName(mafile, Settings.Instance.UseAccountNameAsMafileName);
        return MafilesStorageHelper.FindExistingMafileName(fileName) != null;
    }


    private async Task<MafileReadResult> TryReadMafile(string path, SDAEncryptionHelper.Context? sdaContext,
        bool sdaPasswordPrompted)
    {
        try
        {
            Mafile mafile;
            var bytes = await File.ReadAllBytesAsync(path);
            if (StorageCrypto.HasMagic(bytes))
            {
                // NebulaAuth encrypted mafile (decided by the header): readable only when it belongs to this storage
                var plain = MafileStorageService.Current.DecodeImportedFile(bytes);
                try
                {
                    mafile = NebulaSerializer.Deserialize(Encoding.UTF8.GetString(plain), StorageFileNames.ToPlainName(path));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plain);
                }

                return new MafileReadResult(mafile, sdaPasswordPrompted, sdaContext);
            }

            var content = await File.ReadAllTextAsync(path);
            if (!SDAEncryptionHelper.LooksLikeSdaEncryptedBlob(content))
            {
                mafile = NebulaSerializer.Deserialize(content, path);
                return new MafileReadResult(mafile, sdaPasswordPrompted, sdaContext);
            }

            // Looks like encrypted, let's see if we have manifest
            sdaContext ??= SDAEncryptionHelper.TryDetect(path, sdaContext?.SdaManifest);
            if (sdaContext != null)
            {
                // We have manifest, but we must ensure we have password
                if (sdaContext.Password == null && !sdaPasswordPrompted)
                {
                    sdaPasswordPrompted = true;
                    var password = await DialogsController.ShowSdaPasswordDialog();
                    sdaContext = sdaContext.WithPassword(password);
                }

                var decrypted = SDAEncryptionHelper.TryDecrypt(content, path, sdaContext);
                if (decrypted == null) return new MafileReadResult(null, sdaPasswordPrompted, sdaContext);
                content = decrypted;
            }

            // If we are here, it means that either file is not encrypted, or we successfully decrypted it
            mafile = NebulaSerializer.Deserialize(content, path);
            return new MafileReadResult(mafile, sdaPasswordPrompted, sdaContext);
        }
        catch (StorageFileException ex)
        {
            // Encrypted by another storage / format, damaged, or encryption is off: never read with another key
            Shell.Logger.Warn("Import: {file} refused ({reason})", Path.GetFileName(path), ex.Reason);
            SnackbarController.SendSnackbar(string.Format(GetLocalization("ImportEncryptedRefused"), Path.GetFileName(path),
                SkippedFileItemVM.GetReasonText(ex.Reason)), TimeSpan.FromSeconds(8));
            return new MafileReadResult(null, sdaPasswordPrompted, sdaContext);
        }
        catch (Exception ex)
            when (ex is not MafileNeedReloginException)
        {
            Shell.Logger.Warn(ex, "Failed to import mafile");
            throw new FormatException($"Failed to read mafile {Path.GetFileName(path)}", ex);
        }
    }

    private async Task<bool> HandleAddMafileWithoutSession(Mafile data)
    {
        var loginAgainVm = await DialogsController.ShowLoginAgainOnImportDialog(data, Proxies);
        if (loginAgainVm == null)
        {
            return false;
        }

        var password = loginAgainVm.Password;
        if (!loginAgainVm.UseMafileProxy)
        {
            data.Proxy = loginAgainVm.SelectedProxy;
        }

        // The name of the imported file is not a storage name: the login saves the mafile, which must be written under
        // the storage name (the one SaveMafileAsync below uses), never under the name of the imported file
        data.Filename = null;
        var waitDialog = new WaitLoginDialog();
        var wait = DialogService.Show(waitDialog);
        try
        {
            await MaClient.LoginAgain(data, password, loginAgainVm.SavePassword);
            SnackbarController.SendSnackbar(GetLocalization("SuccessfulLogin"));
        }
        catch (LoginException ex)
        {
            SnackbarController.SendSnackbar(ErrorTranslatorHelper.TranslateLoginError(ex.Error),
                TimeSpan.FromSeconds(1.5));
        }
        catch (Exception ex)
            when (ExceptionHandler.Handle(ex))
        {
            Shell.Logger.Error(ex);
        }
        finally
        {
            DialogsController.CloseDialog();
            await wait;
        }

        var result = data.SessionData != null;
        if (!result) return result;
        data.Filename = null;
        await Storage.SaveMafileAsync(data);
        {
            ResetQuery();
            SearchText = data.AccountName ?? string.Empty;
            SelectedMafile = data;
        } //As this operation used only for 1 mafile at time, we can safely assume that we can select it for convenience
        return result;
    }

    private static void ShowImportSummary(MafileImportSummary summary)
    {
        var msg = GetLocalization("Import");
        if (summary.Added > 0)
        {
            msg += $" {GetLocalization("ImportAdded")} {summary.Added}.";
        }

        if (summary.NotAdded > 0)
        {
            msg += $" {GetLocalization("ImportSkipped")} {summary.NotAdded}.";
        }

        if (summary.Errors > 0)
        {
            msg += $" {GetLocalization("ImportErrors")} {summary.Errors}.";
        }

        SnackbarController.SendSnackbar(msg, TimeSpan.FromSeconds(2));
    }


    [RelayCommand]
    private async Task RemoveMafile()
    {
        if (SelectedMafile == null) return;
        var confirm =
            await DialogsController.ShowConfirmCancelDialog(new ConfirmDialogOptions(
                GetLocalization("RemoveMafileTitle"),
                SelectedMafile.AccountName,
                MaterialIconKind.FileRemove,
                GetLocalization("RemoveMafileText"),
                GetLocalization("RemoveMafileHint"),
                HintBoxSeverity.Warning,
                GetLocalization("RemoveMafileConfirm")));

        if (!confirm) return;
        try
        {
            Storage.MoveToRemoved(SelectedMafile);
        }
        catch (UnauthorizedAccessException)
        {
            SnackbarController.SendSnackbar(GetLocalization("CantRemoveAlreadyExist"));
        }
        catch (Exception ex)
        {
            SnackbarController.SendSnackbar(
                $"{GetLocalization("CantRemoveMafile")} {ex.Message}");
        }
    }

    [RelayCommand]
    private Task OpenSettingsDialog()
    {
        return DialogsController.ShowSettingsDialog();
    }

    [RelayCommand]
    private async Task PasteMafilesFromClipboard()
    {
        // Errors are logged inside
        var arr = await ClipboardHelper.GetFilesAsync();
        if (arr.All(p => !p.ContainsIgnoreCase("mafile"))) return;


        await AddMafile(arr);
    }

    [RelayCommand]
    private void CopyLogin(object? mafile)
    {
        if (mafile is not Mafile maf) return;
        if (ClipboardHelper.Set(maf.AccountName))
            SnackbarController.SendSnackbar(GetLocalization("LoginCopied"));
    }

    [RelayCommand]
    private void CopySteamId(object? mafile)
    {
        if (mafile is not Mafile maf) return;

        if (ClipboardHelper.Set(maf.SteamId.ToString()))
            SnackbarController.SendSnackbar(GetLocalization("SteamIdCopied"));
    }

    [RelayCommand]
    private async Task CopyMafile(object? mafile)
    {
        if (mafile is not Mafile maf) return;
        var path = Storage.TryGetMafilePath(maf);
        bool copied;
        if (path != null && maf.Filename != null &&
            StorageFileNames.IsEncrypted(StorageFileNames.Classify(Path.GetFileName(path))))
        {
            // Encrypted storage: the clipboard gets a readable temporary copy (no Steam password), deleted once the
            // clipboard changes, after a time limit, or on the next start
            byte[] plain;
            try
            {
                var decrypted = await MafileStorageService.Current.ReadFileAsync(path);
                plain = StorageContent.StripPassword(decrypted, out _);
                if (!ReferenceEquals(plain, decrypted)) CryptographicOperations.ZeroMemory(decrypted);
            }
            catch (Exception ex) when (ex is StorageFileException or FormatException)
            {
                Shell.Logger.Error(ex, "Copy mafile: {file} can't be read", Path.GetFileName(path));
                SnackbarController.SendSnackbar(GetLocalization("MafileNotCopied"));
                return;
            }

            copied = await DecryptedMafileClipboard.CopyAsync(StorageFileNames.ToPlainName(maf.Filename), plain);
        }
        else
        {
            copied = await ClipboardHelper.SetFilesAsync(path == null ? [] : [path]);
        }

        if (copied)
            SnackbarController.SendSnackbar(GetLocalization("MafileCopied"));
    }

    [RelayCommand(CanExecute = nameof(CanCopyPassword))]
    private void CopyPassword(object? mafile)
    {
        if (mafile is not Mafile maf) return;
        var pass = SteamPasswords.Get(maf.AccountName);
        if (pass == null)
        {
            SnackbarController.SendSnackbar(GetLocalization("CantDecryptPassword"));
            return;
        }

        if (ClipboardHelper.Set(pass))
            SnackbarController.SendSnackbar(GetLocalization("PasswordCopied"));
    }

    private bool CanCopyPassword(object? mafile)
    {
        if (mafile is not Mafile maf) return false;
        return SteamPasswords.Has(maf.AccountName);
    }
}