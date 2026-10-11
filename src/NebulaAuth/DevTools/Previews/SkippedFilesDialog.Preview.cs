using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NebulaAuth.Model;
using NebulaAuth.Model.Mafiles.Protection;
using NebulaAuth.ViewModel.Other;

namespace NebulaAuth.View.Dialogs;

public partial class SkippedFilesDialog
{
    /// <summary>
    ///     Preview (NEBULA_PREVIEW=NebulaAuth.View.Dialogs.SkippedFilesDialog): without an argument one file of
    ///     several reasons, including a readable (not protected) mafile in an encrypted storage. NEBULA_PREVIEW_ARG=&lt;n&gt;
    ///     lists n generated files of mixed reasons (scaling check), "live" the skipped files of the data folder; the number
    ///     of realized rows is written to the log.
    ///     No file is touched.
    /// </summary>
    internal static object CreatePreview(string? arg)
    {
        var files = arg == "live" ? MafileStorageService.Current.SkippedFiles.ToList()
            : int.TryParse(arg, out var n) && n > 0 ? Generate(n) : Sample();
        var dialog = new SkippedFilesDialog {DataContext = new SkippedFilesVM(files)};
        dialog.AttachedToVisualTree += (_, _) => DispatcherTimer.RunOnce(() =>
        {
            var list = dialog.GetVisualDescendants().OfType<SkippedFilesList>().FirstOrDefault();
            var rows = list?.FindControl<ItemsControl>("Rows");
            Shell.Logger.Info("Debug: skipped files preview: {files} file(s), {rows} row(s), {realized} realized",
                files.Count, rows?.ItemCount ?? -1, rows?.GetRealizedContainers().Count() ?? -1);
        }, TimeSpan.FromSeconds(2));
        return dialog;
    }

    private static List<SkippedFile> Sample()
    {
        var layout = MafileStorageService.Current.Layout;
        return
        [
            new SkippedFile("76561198000000001.mafile", Path.Combine(layout.MafilesDirectory, "76561198000000001.mafile"),
                SkippedFileReason.Invalid, true),
            new SkippedFile("alpha.mafile.encrypted", Path.Combine(layout.MafilesDirectory, "alpha.mafile.encrypted"),
                SkippedFileReason.ForeignStorage, false),
            new SkippedFile("bravo.mafile.encrypted", Path.Combine(layout.MafilesDirectory, "bravo.mafile.encrypted"),
                SkippedFileReason.Corrupted, false),
            new SkippedFile("charlie.mafile", Path.Combine(layout.RemovedDirectory, "charlie.mafile"),
                SkippedFileReason.Conflict, true)
        ];
    }

    /// <summary>
    ///     n files: mostly damaged, some of another key, a few not protected, the rest spread over the other reasons.
    /// </summary>
    private static List<SkippedFile> Generate(int n)
    {
        var layout = MafileStorageService.Current.Layout;
        var dirs = layout.AccountDirectories;
        var rare = new[]
        {
            SkippedFileReason.Invalid, SkippedFileReason.NeedsRelogin, SkippedFileReason.Unreadable,
            SkippedFileReason.UnsupportedFormat, SkippedFileReason.ExtensionMismatch, SkippedFileReason.Conflict,
            SkippedFileReason.UnfinishedWrite
        };
        var files = new List<SkippedFile>(n);
        for (var i = 0; i < n; i++)
        {
            var id = 76561198000100000L + i * 37;
            var dir = dirs[i % 7 == 0 ? 2 : i % 5 == 0 ? 1 : 0];
            SkippedFile file = (i % 50) switch
            {
                0 or 17 => new SkippedFile($"{id}.mafile", Path.Combine(dir, $"{id}.mafile"), SkippedFileReason.Invalid,
                    true),
                < 30 => new SkippedFile($"{id}.mafile.encrypted", Path.Combine(dir, $"{id}.mafile.encrypted"),
                    SkippedFileReason.Corrupted, false),
                < 40 => new SkippedFile($"account_{i:000}_with_a_rather_long_login_name.mafile.encrypted",
                    Path.Combine(dir, $"account_{i:000}_with_a_rather_long_login_name.mafile.encrypted"),
                    SkippedFileReason.ForeignStorage, false),
                _ => new SkippedFile($"{id}.mafile", Path.Combine(dir, $"{id}.mafile"), rare[i % rare.Length], false)
            };
            files.Add(file);
        }

        return files;
    }
}
