using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using AchiesUtilities.Extensions;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;
using NebulaAuth.Core;
using NebulaAuth.Model;
using NebulaAuth.Model.Entities;
using NebulaAuth.Model.Mafiles;
using NebulaAuth.Model.Mafiles.Protection;
using NebulaAuth.Utility;
using NebulaAuth.View;
using NebulaAuth.View.Dialogs;
using NebulaAuth.ViewModel.Other;
using SteamLibForked.Exceptions.Authorization;

namespace NebulaAuth.ViewModel;

public partial class MainVM //File //TODO: Refactor
{
    private readonly ImportGate _importGate = new();

    public Settings Settings => Settings.Instance;

    /// <summary>An import runs (from getting the files to the result): new imports are rejected.</summary>
    public bool IsImportBusy => _importGate.IsBusy;

    /// <summary>The selected files are being checked / confirmed / written (status bar indicator).</summary>
    public bool IsImportRunning => _importGate.Phase == ImportPhase.Importing;

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


    [RelayCommand(CanExecute = nameof(CanStartImport))]
    private Task AddMafile()
    {
        // Several files at once: the whole set goes through one check, one confirmation and one result
        return StartImport(() => FileDialogs.OpenFilesAsync("Mafile", MafilePatterns, true));
    }

    /// <summary>
    ///     Imports files dropped on the window.
    /// </summary>
    public Task ImportMafiles(string[] paths)
    {
        return StartImport(() => Task.FromResult(paths));
    }

    /// <summary>
    ///     The single entry of every import. Another trigger while an import runs (reading, check, dialog, writing) is
    ///     rejected with a short message.
    /// </summary>
    private async Task StartImport(Func<Task<string[]>> getFiles)
    {
        if (!await _importGate.RunAsync(getFiles, ImportCoreAsync))
        {
            SnackbarController.SendSnackbar(GetLocalization("ImportAlreadyRunning"), TimeSpan.FromSeconds(3));
        }
    }

    private bool CanStartImport()
    {
        return !_importGate.IsBusy;
    }

    private void OnImportPhaseChanged()
    {
        OnPropertyChanged(nameof(IsImportBusy));
        OnPropertyChanged(nameof(IsImportRunning));
        AddMafileCommand.NotifyCanExecuteChanged();
    }

    private async Task ImportCoreAsync(string[] paths)
    {
        var check = await MafileImportChecker.CheckAsync(paths, DialogsController.ShowSdaPasswordDialog);
        Shell.Logger.Info("Import check: {total} file(s), valid {valid}, conflicts {conflicts}, errors {errors}",
            check.Total, check.Valid, check.Conflicts, check.Errors);
        ReportImportFailures(check.Failures);

        if (check.Valid == 0)
        {
            // Nothing to confirm: the check result only
            SnackbarController.SendSnackbar(string.Format(GetLocalization("ImportNothingValid"), check.Total,
                check.Errors), TimeSpan.FromSeconds(5));
            return;
        }

        var summary = new MafileImportSummary();
        summary.Add(errors: check.Errors);
        var showImportDialog = Settings.ConfirmMafileImport || check.Conflicts > 0;
        MafileImportDialogResult? importOptions = null;
        if (showImportDialog)
        {
            importOptions = await DialogsController.ShowMafileImportDialog(Groups,
                new MafileImportCounts(check.Total, check.Valid, check.Conflicts, check.Errors));
            if (importOptions == null)
            {
                SnackbarController.SendSnackbar(LocManager.GetCommonOrDefault("Canceled", "Canceled"));
                return;
            }
        }

        var importGroup = importOptions?.Group;
        var overwriteConflicts = importOptions?.OverwriteConflicts ?? false;
        foreach (var item in check.Candidates)
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

            try
            {
                var res = await Storage.AddNewMafileFromData(item.Mafile, overwriteConflicts);
                summary.Apply(res);
            }
            catch (Exception ex)
            {
                // A failed write leaves the existing file intact (atomic publish); the next files are still imported
                Shell.Logger.Error(ex, "Import: can't write {file}", Path.GetFileName(item.Path));
                summary.ErrorOne();
            }
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

    /// <summary>
    ///     Per-file details for the failures the user can act on (encrypted file of another storage, mafile without a
    ///     session among several files). A long list is covered by the error count only (details are in the log).
    /// </summary>
    private static void ReportImportFailures(IReadOnlyList<MafileImportFailure> failures)
    {
        const int maxDetailed = 3;
        var detailed = failures.Where(f => f.Problem is MafileImportProblem.Refused
            or MafileImportProblem.NeedsSeparateImport).ToList();
        if (detailed.Count > maxDetailed) return;
        foreach (var failure in detailed)
        {
            var name = Path.GetFileName(failure.Path);
            var text = failure.Problem == MafileImportProblem.Refused
                ? string.Format(GetLocalization("ImportEncryptedRefused"), name,
                    SkippedFileItemVM.GetReasonText(failure.Reason ?? SkippedFileReason.UnsupportedFormat))
                : $"{GetLocalization("MafileImportError")} {name}{GetLocalization("MissingSessionInMafile")}";
            SnackbarController.SendSnackbar(text, TimeSpan.FromSeconds(failure.Problem == MafileImportProblem.Refused ? 8 : 4));
        }
    }

    private static void ApplyImportGroup(Mafile mafile, string? group)
    {
        if (string.IsNullOrWhiteSpace(group)) return;
        mafile.Group = group;
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

    // Concurrent executions allowed: a repeated Ctrl+V reaches the import gate, which tells that an import runs
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task PasteMafilesFromClipboard()
    {
        return StartImport(async () =>
        {
            // Errors are logged inside
            var arr = await ClipboardHelper.GetFilesAsync();
            return arr.Any(p => p.ContainsIgnoreCase("mafile")) ? arr : [];
        });
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