#if DEBUG
using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Threading;
using NebulaAuth.Core;
using NebulaAuth.Model;
using NebulaAuth.ViewModel.Other;

namespace NebulaAuth.View;

public partial class SettingsView
{
    /// <summary>
    ///     Debug preview (NEBULA_PREVIEW=NebulaAuth.View.SettingsView). NEBULA_PREVIEW_ARG, comma separated:
    ///     <c>theme</c> - open the Theme tab; <c>rename</c> - show a sample rename result;
    ///     <c>drag</c> - Theme tab in the "slider is dragged" state (transparent dialog);
    ///     <c>live</c> - after opening, switches theme to Luxury and language to Russian through the VM (live switch check).
    /// </summary>
    internal static object CreatePreview(string? arg)
    {
        var args = (arg ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var vm = new SettingsVM();
        var view = new SettingsView {DataContext = vm};
        if (args.Contains("theme") || args.Contains("drag"))
            view.SettingsTabs.SelectedIndex = 1;
        if (args.Contains("live"))
            view.Loaded += (_, _) => DispatcherTimer.RunOnce(() =>
            {
                vm.ThemeType = ThemeType.Luxury;
                vm.Language = LocalizationLanguage.Russian;
            }, TimeSpan.FromSeconds(2));
        if (args.Contains("rename"))
        {
            var format = LocManager.GetCodeBehindOrDefault("{0} {1} {2} {3} {4}", "SettingsVM", "MafileRenaming",
                "PartialSuccess");
            vm.RenameResultText = string.Format(format, 12, 10, 1, 1, "mafiles_backup_2026-09-26.zip");
        }

        if (args.Contains("drag"))
            view.Loaded += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                view.BeginSliderDrag();
                // Keep the drag look for the screenshot when the window is not in the foreground
                if (view._dragTopLevel is WindowBase window)
                    window.Deactivated -= view.Window_OnDeactivated;
            }, DispatcherPriority.Background);
        return view;
    }
}
#endif
