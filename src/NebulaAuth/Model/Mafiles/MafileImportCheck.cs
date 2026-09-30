using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using NebulaAuth.Model.Entities;
using NebulaAuth.Model.Exceptions;
using NebulaAuth.Model.Mafiles.Protection;
using NebulaAuth.Model.MafilesLegacy;

namespace NebulaAuth.Model.Mafiles;

/// <summary>
///     A selected file that can be imported.
/// </summary>
/// <param name="Path">Selected file.</param>
/// <param name="Mafile">Its content.</param>
/// <param name="RequiresRelogin">No valid session / SteamID: imported through a new login (single file only).</param>
/// <param name="HasConflict">
///     A mafile with the same storage name exists, or an earlier file of the same selection gets that name.
/// </param>
public sealed record MafileImportCandidate(string Path, Mafile Mafile, bool RequiresRelogin, bool HasConflict);

public enum MafileImportProblem
{
    /// <summary>Not a mafile (not JSON, missing data), too large, or can't be read.</summary>
    Invalid,

    /// <summary>
    ///     NebulaAuth encrypted file that this storage can't open (other storage, damaged, encryption off):
    ///     <see cref="MafileImportFailure.Reason" /> says why.
    /// </summary>
    Refused,

    /// <summary>The mafile has no session / SteamID and was selected together with other files.</summary>
    NeedsSeparateImport,

    /// <summary>SDA encrypted mafile that could not be decrypted (no manifest, wrong or no password).</summary>
    SdaNotDecrypted
}

/// <param name="Path">Selected file.</param>
/// <param name="Problem">Why it is not imported.</param>
/// <param name="Reason">Storage reason for <see cref="MafileImportProblem.Refused" />.</param>
public sealed record MafileImportFailure(string Path, MafileImportProblem Problem, SkippedFileReason? Reason = null);

/// <summary>
///     Result of the check before an import: every selected file is either a candidate or a failure, nothing is written.
/// </summary>
public sealed class MafileImportCheck
{
    public int Total { get; init; }
    public IReadOnlyList<MafileImportCandidate> Candidates { get; init; } = [];
    public IReadOnlyList<MafileImportFailure> Failures { get; init; } = [];

    public int Valid => Candidates.Count;
    public int Conflicts => Candidates.Count(c => c.HasConflict);
    public int Errors => Failures.Count;
}

/// <summary>
///     Reads the selected files of an import (file picker, drag and drop, clipboard) and sorts them into candidates and
///     failures. Invalid / unsupported files are only counted: they are never written.
/// </summary>
public static class MafileImportChecker
{
    /// <summary>Real mafiles are a few KB: larger files are not read into memory.</summary>
    public const long MAX_FILE_SIZE = 1024 * 1024;

    /// <summary>
    ///     Check against the loaded storage (maFiles folder, current naming setting).
    /// </summary>
    /// <param name="paths">Selected files.</param>
    /// <param name="askSdaPassword">
    ///     Asks for the SDA encryption password (at most once per check); null result = no password.
    /// </param>
    public static Task<MafileImportCheck> CheckAsync(IReadOnlyList<string> paths, Func<Task<string?>> askSdaPassword)
    {
        return CheckAsync(paths, askSdaPassword, name => MafilesStorageHelper.FindExistingMafileName(name) != null,
            Settings.Instance.UseAccountNameAsMafileName);
    }

    /// <param name="paths">Selected files.</param>
    /// <param name="askSdaPassword">
    ///     Asks for the SDA encryption password (at most once per check); null result = no password.
    /// </param>
    /// <param name="hasConflict">Whether a mafile with this storage file name already exists.</param>
    /// <param name="useLoginAsFileName">Storage naming setting (file name of a new mafile).</param>
    public static async Task<MafileImportCheck> CheckAsync(IReadOnlyList<string> paths,
        Func<Task<string?>> askSdaPassword, Func<string, bool> hasConflict, bool useLoginAsFileName)
    {
        var candidates = new List<MafileImportCandidate>();
        var failures = new List<MafileImportFailure>();
        var plannedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sda = new SdaState();
        foreach (var path in paths)
        {
            try
            {
                var mafile = await ReadAsync(path, sda, askSdaPassword);
                if (mafile == null)
                {
                    failures.Add(new MafileImportFailure(path, MafileImportProblem.SdaNotDecrypted));
                    continue;
                }

                candidates.Add(new MafileImportCandidate(path, mafile, false, Conflict(mafile)));
            }
            catch (StorageFileException ex)
            {
                Shell.Logger.Warn("Import: {file} refused ({reason})", Path.GetFileName(path), ex.Reason);
                failures.Add(new MafileImportFailure(path, MafileImportProblem.Refused, ex.Reason));
            }
            catch (MafileNeedReloginException ex)
            {
                // A new login is interactive: only possible when this is the only selected file
                if (paths.Count == 1 && ex.Mafile != null)
                    candidates.Add(new MafileImportCandidate(path, ex.Mafile, true, Conflict(ex.Mafile)));
                else
                    failures.Add(new MafileImportFailure(path, MafileImportProblem.NeedsSeparateImport));
            }
            catch (Exception ex)
            {
                Shell.Logger.Warn(ex, "Import: {file} is not a valid mafile", Path.GetFileName(path));
                failures.Add(new MafileImportFailure(path, MafileImportProblem.Invalid));
            }
        }

        return new MafileImportCheck {Total = paths.Count, Candidates = candidates, Failures = failures};

        bool Conflict(Mafile mafile)
        {
            var fileName = MafilesStorageHelper.CreateMafileFileName(mafile, useLoginAsFileName);
            var exists = hasConflict(fileName);
            // Two files of one selection for the same account: the second one conflicts with the first
            return !plannedNames.Add(fileName) || exists;
        }
    }

    private sealed class SdaState
    {
        public SDAEncryptionHelper.Context? Context;
        public bool PasswordPrompted;
    }

    /// <returns>The mafile, or null for an SDA encrypted mafile that could not be decrypted.</returns>
    private static async Task<Mafile?> ReadAsync(string path, SdaState sda, Func<Task<string?>> askSdaPassword)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("The file does not exist", path);
        if (info.Length > MAX_FILE_SIZE) throw new FormatException($"The file is too large ({info.Length} bytes)");

        var bytes = await File.ReadAllBytesAsync(path);
        if (StorageCrypto.HasMagic(bytes))
        {
            // NebulaAuth encrypted mafile (decided by the header): readable only when it belongs to this storage
            var plain = MafileStorageService.Current.DecodeImportedFile(bytes);
            try
            {
                return NebulaSerializer.Deserialize(Encoding.UTF8.GetString(plain), StorageFileNames.ToPlainName(path));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }

        var content = await File.ReadAllTextAsync(path);
        if (!SDAEncryptionHelper.LooksLikeSdaEncryptedBlob(content))
            return NebulaSerializer.Deserialize(content, path);

        // SDA encrypted: needs the manifest next to the file and the password
        sda.Context ??= SDAEncryptionHelper.TryDetect(path, null);
        if (sda.Context != null)
        {
            if (sda.Context.Password == null && !sda.PasswordPrompted)
            {
                sda.PasswordPrompted = true;
                var password = await askSdaPassword();
                sda.Context = sda.Context.WithPassword(password);
            }

            var decrypted = SDAEncryptionHelper.TryDecrypt(content, path, sda.Context);
            if (decrypted == null) return null;
            content = decrypted;
        }

        return NebulaSerializer.Deserialize(content, path);
    }
}
