using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;
using NebulaAuth.Core;
using NebulaAuth.Model.Mafiles.Protection;
using NebulaAuth.Utility;

namespace NebulaAuth.ViewModel.Other;

/// <summary>
///     Files of the storage folders that were not loaded or not converted to the storage mode
///     (<see cref="MafileStorageService.SkippedFiles" />). The files stay in place; the user decides what to do with them.
///     The list can hold hundreds of files: it is shown as one flat, virtualized list of group headers and compact rows
///     (<see cref="Rows" />): the readable files of an encrypted storage ("not protected") first, then one group per reason.
/// </summary>
public partial class SkippedFilesVM
{
    public SkippedFilesVM(IEnumerable<SkippedFile> files)
    {
        var all = files.Select(f => new SkippedFileItemVM(f)).ToList();
        var unprotected = all.Where(i => i.IsUnprotected).OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var groups = new List<SkippedFileGroupVM>();
        if (unprotected.Count > 0) groups.Add(SkippedFileGroupVM.Unprotected(unprotected));
        groups.AddRange(all.Where(i => !i.IsUnprotected)
            .GroupBy(i => i.File.Reason)
            .OrderBy(g => g.Key)
            .Select(g => SkippedFileGroupVM.ForReason(g.Key,
                g.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList())));

        Groups = groups;
        Items = groups.SelectMany(g => g.Items).ToList();
        var rows = new List<object>(Items.Count + groups.Count);
        foreach (var g in groups)
        {
            rows.Add(g);
            rows.AddRange(g.Items);
        }

        Rows = rows;
        UnprotectedCount = unprotected.Count;
    }

    /// <summary>Files in display order (not protected first, then by reason and name).</summary>
    public IReadOnlyList<SkippedFileItemVM> Items { get; }

    /// <summary>Not protected files first, then one group per reason.</summary>
    public IReadOnlyList<SkippedFileGroupVM> Groups { get; }

    /// <summary>Flat list for the virtualized view: each <see cref="SkippedFileGroupVM" /> followed by its files.</summary>
    public IReadOnlyList<object> Rows { get; }

    public int Count => Items.Count;
    public int UnprotectedCount { get; }
    public bool HasUnprotected => UnprotectedCount > 0;

    /// <summary>
    ///     One line per group for logs and tooltips: "Not protected: 2 · Damaged: 40 · Other key: 5".
    /// </summary>
    public string SummaryText => string.Join(" · ", Groups.Select(g => $"{g.Title}: {g.Count}"));

    /// <summary>
    ///     Opens the folder of the first listed file (the storage folder when there is none).
    /// </summary>
    [RelayCommand]
    private void OpenFolder()
    {
        var folder = Items.Select(i => Path.GetDirectoryName(i.Path)).FirstOrDefault(Directory.Exists) ??
                     MafileStorageService.Current.Layout.MafilesDirectory;
        PlatformUtils.OpenFolder(folder);
    }
}

/// <summary>
///     Header of a group of skipped files: short title, count, what the reason means.
/// </summary>
public sealed class SkippedFileGroupVM
{
    private SkippedFileGroupVM(string title, string description, bool isUnprotected, MaterialIconKind icon,
        IReadOnlyList<SkippedFileItemVM> items)
    {
        Title = title;
        Description = description;
        IsUnprotected = isUnprotected;
        Icon = icon;
        Items = items;
    }

    public string Title { get; }
    public string Description { get; }
    public bool IsUnprotected { get; }
    public MaterialIconKind Icon { get; }
    public IReadOnlyList<SkippedFileItemVM> Items { get; }
    public int Count => Items.Count;

    public static SkippedFileGroupVM Unprotected(IReadOnlyList<SkippedFileItemVM> items)
    {
        foreach (var i in items) i.ShowReason = true;
        return new SkippedFileGroupVM(
            LocManager.GetOrDefault("Not protected", "SkippedFiles", "Groups", "Unprotected"),
            LocManager.GetOrDefault(
                "These files are readable and contain account secrets. Move them to a safe place or delete them; a valid mafile left in the maFiles folder is encrypted on the next start.",
                "SkippedFiles", "Groups", "UnprotectedDescription"),
            true, MaterialIconKind.ShieldOffOutline, items);
    }

    public static SkippedFileGroupVM ForReason(SkippedFileReason reason, IReadOnlyList<SkippedFileItemVM> items)
    {
        return new SkippedFileGroupVM(SkippedFileItemVM.GetReasonTitle(reason), SkippedFileItemVM.GetReasonText(reason),
            false, MaterialIconKind.FileAlertOutline, items);
    }
}

public partial class SkippedFileItemVM
{
    public SkippedFileItemVM(SkippedFile file)
    {
        File = file;
        Folder = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(file.Path)) ?? string.Empty;
    }

    public SkippedFile File { get; }
    public string Name => File.Name;
    public string Path => File.Path;

    /// <summary>Name of the containing folder (maFiles, maFiles_backup...); the full path is in the tooltip.</summary>
    public string Folder { get; }

    public string ReasonText => GetReasonText(File.Reason);
    public string ReasonTitle => GetReasonTitle(File.Reason);
    public bool IsUnprotected => File.IsUnprotectedPlain;
    public string? Details => File.Details;

    /// <summary>
    ///     The row shows its short reason (rows of the "not protected" group: the group title is not the reason).
    /// </summary>
    public bool ShowReason { get; internal set; }

    /// <summary>Full path and the reason (row tooltip).</summary>
    public string ToolTip => Path + Environment.NewLine + ReasonText;

    [RelayCommand]
    private void Reveal()
    {
        if (System.IO.File.Exists(Path)) PlatformUtils.RevealFile(Path);
        else PlatformUtils.OpenFolder(System.IO.Path.GetDirectoryName(Path) ?? Path);
    }

    /// <summary>
    ///     Short localized title of a reason (group header, summary).
    /// </summary>
    public static string GetReasonTitle(SkippedFileReason reason)
    {
        var def = reason switch
        {
            SkippedFileReason.Invalid => "Not a valid mafile",
            SkippedFileReason.NeedsRelogin => "No SteamID",
            SkippedFileReason.Unreadable => "Can't be read",
            SkippedFileReason.ForeignStorage => "Other key",
            SkippedFileReason.UnsupportedFormat => "Unknown format",
            SkippedFileReason.ExtensionMismatch => "Wrong extension",
            SkippedFileReason.Corrupted => "Damaged",
            SkippedFileReason.EncryptedInPlainStorage => "Encrypted, no key",
            SkippedFileReason.Conflict => "Two versions",
            SkippedFileReason.ConversionFailed => "Not converted",
            SkippedFileReason.UnfinishedWrite => "Interrupted write",
            _ => reason.ToString()
        };
        return LocManager.GetOrDefault(def, "SkippedFiles", "ReasonTitles", reason.ToString());
    }

    /// <summary>
    ///     Short localized reason of a skipped file.
    /// </summary>
    public static string GetReasonText(SkippedFileReason reason)
    {
        var def = reason switch
        {
            SkippedFileReason.Invalid => "Not a valid mafile: damaged or not readable JSON.",
            SkippedFileReason.NeedsRelogin =>
                "The mafile has no valid SteamID. Import it again (Add mafile) and log in to the account.",
            SkippedFileReason.Unreadable =>
                "The file can't be read: another program has it open, access is denied or a disk error occurred.",
            SkippedFileReason.ForeignStorage =>
                "Encrypted by another NebulaAuth storage (a different master password). Export a readable mafile in the app that created it and import that file.",
            SkippedFileReason.UnsupportedFormat =>
                "Unknown encrypted format or a newer NebulaAuth version. Import mafiles of other programs through Add mafile.",
            SkippedFileReason.ExtensionMismatch =>
                "The file content does not match its extension (the file was renamed?).",
            SkippedFileReason.Corrupted => "Integrity check failed: the encrypted file is damaged or was changed.",
            SkippedFileReason.EncryptedInPlainStorage =>
                "Encrypted file, but encryption is off: there is no key to read it.",
            SkippedFileReason.Conflict =>
                "The readable and the encrypted versions of this file differ. Both are kept: remove the wrong one.",
            SkippedFileReason.ConversionFailed =>
                "Converting to the current storage mode failed. It is retried when the operation is resumed or on the next start.",
            SkippedFileReason.UnfinishedWrite =>
                "Temporary file of an interrupted write: the original file is missing or damaged. It may hold the latest data.",
            _ => reason.ToString()
        };
        return LocManager.GetOrDefault(def, "SkippedFiles", "Reasons", reason.ToString());
    }
}
